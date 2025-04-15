using Common.Utilities;
using MongoDB.Driver;
using MongoDbService;
using SMSwitch.Common;
using SMSwitch.Common.DTOs;
using SMSwitch.Database.DTOs;

namespace SMSwitch.Database
{
	public sealed class SMSwitchDbService
	{
		private IMongoCollection<SMSwitchSession> _smSwitchSessionCollection;
		private IMongoCollection<SMSwitchSendSMSSession> _smSwitchSendSmsSessionCollection;
		private readonly SMSwitchInitializer _smSwitchInitializer;
		public SMSwitchDbService(
			MongoService mongoService,
			SMSwitchInitializer smSwitchInitializer) 
		{
			_smSwitchInitializer = smSwitchInitializer;

			_smSwitchSessionCollection = mongoService.Database.GetCollection<SMSwitchSession>(nameof(SMSwitchSession), new MongoCollectionSettings() { ReadConcern = ReadConcern.Majority, WriteConcern = WriteConcern.WMajority });

			_smSwitchSendSmsSessionCollection = mongoService.Database.GetCollection<SMSwitchSendSMSSession>(nameof(SMSwitchSendSMSSession), new MongoCollectionSettings() { ReadConcern = ReadConcern.Majority, WriteConcern = WriteConcern.WMajority });

			// Create an index on CountryPhoneCodeAndPhoneNumber
			var indexKeys = Builders<SMSwitchSession>.IndexKeys.Ascending(x => x.CountryPhoneCodeAndPhoneNumber);
			var indexModel = new CreateIndexModel<SMSwitchSession>(indexKeys);
			_ = _smSwitchSessionCollection.Indexes.CreateOneAsync(indexModel);
		}

		private FilterDefinition<SMSwitchSession> Filter(MobileNumber mobileWithCountryCode) => Builders<SMSwitchSession>.Filter.Eq(t => t.CountryPhoneCodeAndPhoneNumber, mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
		private FilterDefinition<SMSwitchSession> Filter(string sessionId) => Builders<SMSwitchSession>.Filter.Eq(t => t.SessionId, sessionId);
		private FilterDefinition<SMSwitchSendSMSSession> FilterSendSMSSession(string sessionId) => Builders<SMSwitchSendSMSSession>.Filter.Eq(t => t.SessionId, sessionId);
		internal async Task<SMSwitchSession> GetOrCreateAndGetLatestSession(MobileNumber mobileWithCountryCode)
		{
			var latestSession = await GetLatestSession(mobileWithCountryCode);
			if (latestSession != null)
			{
				return latestSession;
			}
			latestSession = new SMSwitchSession()
			{
				SessionId = Guid.NewGuid().ToString(),
				CountryPhoneCodeAndPhoneNumber = mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber,
				StartTimeUTC = DateTimeOffset.UtcNow,
				ExpiryTimeUTC = DateTimeOffset.UtcNow.AddSeconds(_smSwitchInitializer.SmsControls.SessionTimeoutInSeconds)
			};

			await _smSwitchSessionCollection.InsertOneAsync(latestSession);

			return latestSession;
		}

		internal async Task UpdateSession(SMSwitchSession session)
		{
			var options = new ReplaceOptions { IsUpsert = true };
			await _smSwitchSessionCollection.ReplaceOneAsync(Filter(session.SessionId), session, options);
		}

		internal async Task<SMSwitchSession?> GetLatestSession(MobileNumber mobileWithCountryCode)
		{
			var allRecords = _smSwitchSessionCollection.Find(Filter(mobileWithCountryCode));

			if (allRecords?.Any() ?? false)
			{
				return await Task.FromResult(allRecords.ToList().Where(r => r.HasNotExpired(_smSwitchInitializer.SmsControls.MaximumFailedAttemptsToVerify))?
				.OrderByDescending(record => record.ExpiryTimeUTC)?
				.FirstOrDefault());
			}
			return null;
		}

		internal async Task<SMSwitchSendSMSSession> GetOrCreateAndGetLatestSendSMSSession(MobileNumber mobileWithCountryCode, string shortMessageServiceMessage)
		{
			var sessionId = CryptoUtils.ComputeSha512Hash($"{mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber}-{shortMessageServiceMessage}");
			var latestSession = await GetLatestSendSMSSession(sessionId);
			if (latestSession != null)
			{
				return latestSession;
			}

			await _smSwitchSendSmsSessionCollection.UpdateOneAsync(FilterSendSMSSession(sessionId), Builders<SMSwitchSendSMSSession>.Update.Set(ar => ar.SessionId, $"{Guid.NewGuid()}_{sessionId}"));

			latestSession = new SMSwitchSendSMSSession()
			{
				SessionId = sessionId,
				CountryPhoneCodeAndPhoneNumber = mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber,
				ShortMessageServiceMessage = shortMessageServiceMessage,
				StartTimeUTC = DateTimeOffset.UtcNow,
				ExpiryTimeUTC = DateTimeOffset.UtcNow.AddSeconds(_smSwitchInitializer.SmsControls.SessionTimeoutInSeconds)
			};

			await _smSwitchSendSmsSessionCollection.InsertOneAsync(latestSession);

			return latestSession;
		}

		private async Task<SMSwitchSendSMSSession?> GetLatestSendSMSSession(string sessionId)
		{
			var allRecords = _smSwitchSendSmsSessionCollection.Find(FilterSendSMSSession(sessionId));

			if (allRecords?.Any() ?? false)
			{
				return await Task.FromResult(allRecords.ToList().Where(r => r.HasNotExpired(_smSwitchInitializer.SmsControls.MaximumFailedAttemptsToVerify))?
				.OrderByDescending(record => record.ExpiryTimeUTC)?
				.FirstOrDefault());
			}
			return null;
		}

		internal async Task UpdateSendSMSSession(SMSwitchSendSMSSession session)
		{
			var options = new ReplaceOptions { IsUpsert = true };
			await _smSwitchSendSmsSessionCollection.ReplaceOneAsync(FilterSendSMSSession(session.SessionId), session, options);
		}
	}
}
