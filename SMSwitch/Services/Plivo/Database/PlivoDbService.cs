using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using MongoDbService;
using SMSwitch.Common.DTOs;
using SMSwitch.Services.Plivo.Database.DTOs;

namespace SMSwitch.Services.Plivo.Database
{
	public sealed class PlivoDbService
	{
		private IMongoCollection<PlivoSession> _plivoSessionCollection;
		private IWebHostEnvironment _hostingEnvironment;
		public PlivoDbService(MongoService mongoService, IWebHostEnvironment hostingEnvironment)
		{
			_plivoSessionCollection = mongoService.Database.GetCollection<PlivoSession>(nameof(PlivoSession), new MongoCollectionSettings() { ReadConcern = ReadConcern.Majority, WriteConcern = WriteConcern.WMajority });
			_hostingEnvironment = hostingEnvironment;
		}

		internal async Task SetLatestSessionUUID(MobileNumber mobileWithCountryCode, string sessionUUID, CancellationToken cancellationToken = default)
		{
			var filter = getFilter(mobileWithCountryCode);
			var options = new ReplaceOptions { IsUpsert = true };
			await _plivoSessionCollection.ReplaceOneAsync(filter, new PlivoSession() { CountryPhoneCodeAndPhoneNumber = mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber, SessionUUID = sessionUUID, TimeStamp = DateTimeOffset.UtcNow }, options, cancellationToken);
		}

		internal async Task UpdateSessionUUID(string mobileNumberCountryPhoneCodeAndPhoneNumber, string sessionUUID, PlivoNotification plivoNotification, CancellationToken cancellationToken = default)
		{
			var filter = getFilter(mobileNumberCountryPhoneCodeAndPhoneNumber, sessionUUID);
			// Appending with a single $push rather than reading the document, mutating the list and
			// replacing it: notifications for the same session can arrive concurrently, and a
			// read-modify-write drops all but the last of them.
			var update = Builders<PlivoSession>.Update.Push(t => t.Notifications, plivoNotification);
			await _plivoSessionCollection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
		}

		private FilterDefinition<PlivoSession> getFilter(string mobileNumberCountryPhoneCodeAndPhoneNumber, string sessionUUID)
		{
			return Builders<PlivoSession>.Filter.Eq(t => t.CountryPhoneCodeAndPhoneNumber, mobileNumberCountryPhoneCodeAndPhoneNumber) & getFilter(sessionUUID);
		}

		private FilterDefinition<PlivoSession> getFilter(string sessionUUID)
		{
			return Builders<PlivoSession>.Filter.Eq(t => t.SessionUUID, sessionUUID);
		}

		private FilterDefinition<PlivoSession> getFilter(MobileNumber mobileWithCountryCode)
		{
			var idAsString = mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber;
			return Builders<PlivoSession>.Filter.Eq(t => t.CountryPhoneCodeAndPhoneNumber, idAsString);
		}

		internal async Task<string?> GetLatestSessionUUID(MobileNumber mobileWithCountryCode, CancellationToken cancellationToken = default)
		{
			var filter = getFilter(mobileWithCountryCode);
			var sessionInDb = await _plivoSessionCollection.Find(filter).FirstOrDefaultAsync(cancellationToken);
			return sessionInDb?.SessionUUID;
		}

		internal async Task ClearSessionUUID(MobileNumber mobileWithCountryCode, CancellationToken cancellationToken = default)
		{
			var filter = getFilter(mobileWithCountryCode);
			await _plivoSessionCollection.DeleteManyAsync(filter, cancellationToken);
		}

		internal async Task<bool> KeepCheckingTheDatabaseIfSentEvery2seconds(string sessionUUID, DateTimeOffset expiry, CancellationToken cancellationToken = default)
		{
			if (!_hostingEnvironment.IsProduction())
			{
				return true;
			}
			var filter = getFilter(sessionUUID);
			var sessionInDb = await _plivoSessionCollection.Find(filter).FirstOrDefaultAsync(cancellationToken);
			if (sessionInDb is not null)
			{
				if (sessionInDb.Notifications.Any(n => n.channelStatus == "delivered"))
				{
					return true;
				}
				else if (sessionInDb.Notifications.Any(n => n.channelStatus == "failed") || DateTimeOffset.UtcNow >= expiry || cancellationToken.IsCancellationRequested)
				{
					return false;
				}
				else
				{
					await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
					return await KeepCheckingTheDatabaseIfSentEvery2seconds(sessionUUID, expiry, cancellationToken);
				}
			}
			return false;
		}
	}
}
