using EarthCountriesInfo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDbService;
using SMSwitch.Countries.Database.DTOs;

namespace SMSwitch.Countries.Database
{
	public sealed class CountryDbService : IHostedService
	{
		private readonly ILogger<CountryDbService> _logger;
		private IMongoCollection<CountryInfo> _countryPhoneCodeCollection;
		private readonly HashSet<CountryInfo> _dataSource;



		public CountryDbService(ILogger<CountryDbService> logger, MongoService mongoService, CountryInitializer countryInitializer)
		{
			_logger = logger;
			_countryPhoneCodeCollection = mongoService.Database.GetCollection<CountryInfo>(nameof(CountryInfo), new MongoCollectionSettings() { ReadConcern = ReadConcern.Majority, WriteConcern = WriteConcern.WMajority });
			// Built once. As an expression-bodied property this rebuilt every country object on
			// each access, several times per load.
			_dataSource = BuildDataSource(countryInitializer);
		}

		private async Task LoadCollectionFromCodeBase()
		{
			// Read the collection directly instead of going through GetAllCountriesFromDb: that
			// method calls back into this one when the collection looks empty, and the previous
			// gate on the *estimated* document count meant the pair could call each other until
			// the stack ran out if the estimate disagreed with reality.
			var allCountriesFromDb = await _countryPhoneCodeCollection.Find(FilterDefinition<CountryInfo>.Empty).ToListAsync();

			if (allCountriesFromDb.Count == 0)
			{
				await _countryPhoneCodeCollection.InsertManyAsync(_dataSource);
				return;
			}

			var localVersionsByCountryCode = _dataSource.ToDictionary(c => c.CountryCode);
			var options = new ReplaceOptions { IsUpsert = true };

			foreach (var countryInfoFromDb in allCountriesFromDb)
			{
				localVersionsByCountryCode.TryGetValue(countryInfoFromDb.CountryCode, out var localVersion);
				if (NeedsAnUpdateInDb(countryInfoFromDb, localVersion, out CountryInfo? latestVersion))
				{
					var filter = Builders<CountryInfo>.Filter.Eq(e => e.CountryCode, countryInfoFromDb.CountryCode);
					await _countryPhoneCodeCollection.ReplaceOneAsync(filter, latestVersion!, options);
				}
			}

			var countryCodesInDb = allCountriesFromDb.Select(c => c.CountryCode).ToHashSet();
			var allCountriesFromLocalNotInDB = _dataSource.Where(c => !countryCodesInDb.Contains(c.CountryCode));
			foreach (var countryFromLocalNotInDB in allCountriesFromLocalNotInDB)
			{
				var filter = Builders<CountryInfo>.Filter.Eq(e => e.CountryCode, countryFromLocalNotInDB.CountryCode);
				await _countryPhoneCodeCollection.ReplaceOneAsync(filter, countryFromLocalNotInDB, options);
			}
		}

		private static bool NeedsAnUpdateInDb(CountryInfo? countryInfoFromDb, CountryInfo? localVersion, out CountryInfo? mergedVersion)
		{
			// If localVersion is null, no update is needed
			if (localVersion == null)
			{
				mergedVersion = countryInfoFromDb;
				return false;
			}

			if (countryInfoFromDb == null)
			{
				mergedVersion = localVersion;
				return true;
			}

			// Both non-null: start with the database version
			mergedVersion = countryInfoFromDb;

			// Check each property for differences
			bool updateNeeded = false;


			if (mergedVersion.CountryPhoneCode != localVersion.CountryPhoneCode)
			{
				mergedVersion.CountryPhoneCode = localVersion.CountryPhoneCode;
				updateNeeded = true;
			}

			if (mergedVersion.IsSupported != localVersion.IsSupported)
			{
				mergedVersion.IsSupported = localVersion.IsSupported;
				updateNeeded = true;
			}

			// For the dictionaries, we'll merge them
			if (localVersion.CountryNames != null)
			{
				mergedVersion.CountryNames ??= [];
				foreach (var pair in localVersion.CountryNames)
				{
					if (!mergedVersion.CountryNames.ContainsKey(pair.Key) || mergedVersion.CountryNames[pair.Key] != pair.Value)
					{
						mergedVersion.CountryNames[pair.Key] = pair.Value;
						updateNeeded = true;
					}
				}
			}

			if (localVersion.ValidLengthsAndFormat != null)
			{
				mergedVersion.ValidLengthsAndFormat ??= [];
				foreach (var pair in localVersion.ValidLengthsAndFormat)
				{
					if (!mergedVersion.ValidLengthsAndFormat.ContainsKey(pair.Key) || mergedVersion.ValidLengthsAndFormat[pair.Key] != pair.Value)
					{
						mergedVersion.ValidLengthsAndFormat[pair.Key] = pair.Value;
						updateNeeded = true;
					}
				}
			}

			return updateNeeded;
		}


		public async Task StartAsync(CancellationToken cancellationToken)
		{
			_logger.LogInformation("CountryDbService running.");

			await LoadCollectionFromCodeBase();
		}

		public Task StopAsync(CancellationToken stoppingToken)
		{
			_logger.LogInformation("CountryDbService is stopping.");

			return Task.CompletedTask;
		}

		public async Task<List<CountryInfo>> GetAllCountriesFromDb()
		{
			var allCountries = await _countryPhoneCodeCollection.Find(e => true).ToListAsync();

			if (allCountries != null && allCountries.Any())
			{
				return allCountries.ToList();
			}
			await LoadCollectionFromCodeBase();
			return await _countryPhoneCodeCollection.Find(e => true).ToListAsync();
		}

		// This data is shared by every caller of the database, and it is fed by whatever number a
		// user managed to verify, so implausible lengths must not be able to widen a country's
		// accepted set. E.164 caps the whole number at 15 digits.
		private const byte MinimumPlausiblePhoneNumberLength = 4;
		private const byte MaximumPlausiblePhoneNumberLength = 15;

		public async Task FeedbackAsync(string countryPhoneCode, byte phoneNumberLength, CountryIsoCode? countryIsoCode, CancellationToken cancellationToken = default)
		{
			if (countryIsoCode is null)
			{
				return;
			}

			if (phoneNumberLength is < MinimumPlausiblePhoneNumberLength or > MaximumPlausiblePhoneNumberLength)
			{
				_logger.LogInformation("Ignoring implausible phone number length {PhoneNumberLength} reported for {CountryIsoCode}", phoneNumberLength, countryIsoCode);
				return;
			}

			var lengthKey = phoneNumberLength.ToString();
			var lengthField = $"{nameof(CountryInfo.ValidLengthsAndFormat)}.{lengthKey}";

			// One conditional update rather than read-modify-write: the phone-code check and the
			// "not already recorded" check run on the server, so concurrent observations for
			// different lengths can no longer overwrite each other.
			var filter = Builders<CountryInfo>.Filter.Eq(e => e.CountryCode, countryIsoCode.ToString())
				& Builders<CountryInfo>.Filter.Eq(e => e.CountryPhoneCode, countryPhoneCode)
				& Builders<CountryInfo>.Filter.Exists(lengthField, false);

			// EarthCountriesInfo leaves ValidLengthsAndFormat null, not empty, for a country whose
			// lengths are unknown, and the driver stores that as BSON null. A plain $set on the
			// dotted path fails against null with "Cannot create field ... in element", and the
			// filter above matches in exactly that case, so the countries feedback is most useful
			// for are the ones it would fail on. $ifNull materialises the document first.
			// Requires MongoDB 4.2 or newer for pipeline updates.
			var validLengthsAndFormat = $"${nameof(CountryInfo.ValidLengthsAndFormat)}";
			var update = Builders<CountryInfo>.Update.Pipeline(new BsonDocument[]
			{
				new("$set", new BsonDocument(
					nameof(CountryInfo.ValidLengthsAndFormat),
					new BsonDocument("$mergeObjects", new BsonArray
					{
						new BsonDocument("$ifNull", new BsonArray { validLengthsAndFormat, new BsonDocument() }),
						// The value is a display mask in which the number of '#' equals the length.
						new BsonDocument(lengthKey, new string('#', phoneNumberLength))
					})))
			});

			// Deliberately no upsert: feedback must never bring a country document into existence.
			var result = await _countryPhoneCodeCollection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);

			if (result.MatchedCount == 0)
			{
				_logger.LogDebug("No CountryInfo updated for {CountryIsoCode} with phone code {CountryPhoneCode} and length {PhoneNumberLength}; it is either unknown, mismatched, or already recorded", countryIsoCode, countryPhoneCode, phoneNumberLength);
			}
		}



		private static HashSet<CountryInfo> BuildDataSource(CountryInitializer countryInitializer) =>
			EarthCountriesInfo.Countries.CountryPropertiesDictionary.Select(c => new CountryInfo
			{
				CountryCode = c.Key.ToString(),
				CountryNames = c.Value.CountryNames.ToDictionary(c => c.Key.ToString(), c => c.Value),
				CountryPhoneCode = c.Value.CountryPhoneCode,
				ValidLengthsAndFormat = c.Value.ValidLengthsAndFormat?.ToDictionary(vl => vl.Key.ToString(), vl => vl.Value),
				IsSupported = countryInitializer.SupportedCountries?.Any() ?? false ? countryInitializer.SupportedCountries.Contains(c.Key) : true,
			}).ToHashSet();

	}
}
