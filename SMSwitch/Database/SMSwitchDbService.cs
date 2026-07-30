using Meyn.Utilities;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDbService;
using SMSwitch.Common;
using SMSwitch.Common.DTOs;
using SMSwitch.Database.DTOs;

namespace SMSwitch.Database
{
	public sealed class SMSwitchDbService : IHostedService
	{
		private IMongoCollection<SMSwitchSession> _smSwitchSessionCollection;
		private IMongoCollection<SMSwitchSendSMSSession> _smSwitchSendSmsSessionCollection;
		private readonly SMSwitchInitializer _smSwitchInitializer;

		/// <summary>
		/// A single number should only ever have a handful of live sessions at once, so reading
		/// more than this many candidates means something else has gone wrong.
		/// </summary>
		private const int MaximumCandidateSessionsToConsider = 10;

		/// <summary>
		/// Sessions are the audit trail, so they outlive their usefulness for verification by a
		/// grace period rather than disappearing the moment they lapse.
		/// </summary>
		private static readonly TimeSpan SessionRetentionAfterExpiry = TimeSpan.FromDays(30);
		public SMSwitchDbService(
			MongoService mongoService,
			SMSwitchInitializer smSwitchInitializer) 
		{
			_smSwitchInitializer = smSwitchInitializer;

			_smSwitchSessionCollection = mongoService.Database.GetCollection<SMSwitchSession>(nameof(SMSwitchSession), new MongoCollectionSettings() { ReadConcern = ReadConcern.Majority, WriteConcern = WriteConcern.WMajority });

			_smSwitchSendSmsSessionCollection = mongoService.Database.GetCollection<SMSwitchSendSMSSession>(nameof(SMSwitchSendSMSSession), new MongoCollectionSettings() { ReadConcern = ReadConcern.Majority, WriteConcern = WriteConcern.WMajority });

		}

		/// <summary>
		/// Creates the indexes the queries in this service rely on. This used to be started from
		/// the constructor and never awaited, so a failure was discarded silently and the first
		/// queries could run before the index existed.
		/// </summary>
		public async Task StartAsync(CancellationToken cancellationToken)
		{
			var indexKeys = Builders<SMSwitchSession>.IndexKeys.Ascending(x => x.CountryPhoneCodeAndPhoneNumber);
			await _smSwitchSessionCollection.Indexes.CreateOneAsync(new CreateIndexModel<SMSwitchSession>(indexKeys), cancellationToken: cancellationToken);

			// DedupeKey is what GetLatestSendSMSSession looks up by, so it needs its own index now
			// that it is no longer the _id.
			var sendSmsIndexKeys = Builders<SMSwitchSendSMSSession>.IndexKeys.Ascending(x => x.DedupeKey);
			await _smSwitchSendSmsSessionCollection.Indexes.CreateOneAsync(new CreateIndexModel<SMSwitchSendSMSSession>(sendSmsIndexKeys), cancellationToken: cancellationToken);

			// The driver stores a DateTimeOffset as a { DateTime, Ticks, Offset } document, and a
			// TTL index needs a real BSON date, so these go on the DateTime sub-field rather than
			// on ExpiryTimeUTC itself.
			await _smSwitchSessionCollection.Indexes.CreateOneAsync(
				new CreateIndexModel<SMSwitchSession>(
					new BsonDocument($"{nameof(SMSwitchSession.ExpiryTimeUTC)}.DateTime", 1),
					new CreateIndexOptions { ExpireAfter = SessionRetentionAfterExpiry }),
				cancellationToken: cancellationToken);

			await _smSwitchSendSmsSessionCollection.Indexes.CreateOneAsync(
				new CreateIndexModel<SMSwitchSendSMSSession>(
					new BsonDocument($"{nameof(SMSwitchSendSMSSession.ExpiryTimeUTC)}.DateTime", 1),
					new CreateIndexOptions { ExpireAfter = SessionRetentionAfterExpiry }),
				cancellationToken: cancellationToken);
		}

		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		private FilterDefinition<SMSwitchSession> Filter(MobileNumber mobileWithCountryCode) => Builders<SMSwitchSession>.Filter.Eq(t => t.CountryPhoneCodeAndPhoneNumber, mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
		private FilterDefinition<SMSwitchSession> Filter(string sessionId) => Builders<SMSwitchSession>.Filter.Eq(t => t.SessionId, sessionId);
		private FilterDefinition<SMSwitchSendSMSSession> FilterSendSMSSession(string sessionId) => Builders<SMSwitchSendSMSSession>.Filter.Eq(t => t.SessionId, sessionId);
		private FilterDefinition<SMSwitchSendSMSSession> FilterSendSMSSessionByDedupeKey(string dedupeKey) => Builders<SMSwitchSendSMSSession>.Filter.Eq(t => t.DedupeKey, dedupeKey);
		internal async Task<SMSwitchSession> GetOrCreateAndGetLatestSession(MobileNumber mobileWithCountryCode, CancellationToken cancellationToken = default)
		{
			var latestSession = await GetLatestSession(mobileWithCountryCode, cancellationToken);
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

			await _smSwitchSessionCollection.InsertOneAsync(latestSession, cancellationToken: cancellationToken);

			return latestSession;
		}

		internal async Task UpdateSession(SMSwitchSession session, CancellationToken cancellationToken = default)
		{
			var options = new ReplaceOptions { IsUpsert = true };
			await _smSwitchSessionCollection.ReplaceOneAsync(Filter(session.SessionId), session, options, cancellationToken);
		}

		/// <summary>
		/// Records a failed verification attempt with a single server-side $push and returns the
		/// session as it stands afterwards. Reading the session, appending to the list in memory
		/// and replacing the whole document lost increments under concurrency, so parallel wrong
		/// guesses could get past <c>MaximumFailedAttemptsToVerify</c>.
		/// </summary>
		internal async Task<SMSwitchSession?> RecordFailedVerificationAttempt(string sessionId, CancellationToken cancellationToken = default)
		{
			var update = Builders<SMSwitchSession>.Update.Push(t => t.FailedVerificationAttemptsDateTimeOffset, DateTimeOffset.UtcNow);
			return await _smSwitchSessionCollection.FindOneAndUpdateAsync(
				Filter(sessionId),
				update,
				new FindOneAndUpdateOptions<SMSwitchSession> { ReturnDocument = ReturnDocument.After },
				cancellationToken);
		}

		/// <summary>
		/// Marks the session verified, but only while it is still unverified, unexpired and under
		/// its failed-attempt limit. Returns null when those conditions no longer hold, so a
		/// success that raced with the session being exhausted cannot revive it.
		/// </summary>
		internal async Task<SMSwitchSession?> RecordSuccessfulVerification(string sessionId, CancellationToken cancellationToken = default)
		{
			var filter = Filter(sessionId)
				& Builders<SMSwitchSession>.Filter.Eq(t => t.SuccessfullyVerifiedTimestampUTC, null)
				& Builders<SMSwitchSession>.Filter.Gt(t => t.ExpiryTimeUTC, DateTimeOffset.UtcNow)
				& Builders<SMSwitchSession>.Filter.Not(
					Builders<SMSwitchSession>.Filter.SizeGte(t => t.FailedVerificationAttemptsDateTimeOffset, _smSwitchInitializer.SmsControls.MaximumFailedAttemptsToVerify));

			var update = Builders<SMSwitchSession>.Update.Set(t => t.SuccessfullyVerifiedTimestampUTC, DateTimeOffset.UtcNow);

			return await _smSwitchSessionCollection.FindOneAndUpdateAsync(
				filter,
				update,
				new FindOneAndUpdateOptions<SMSwitchSession> { ReturnDocument = ReturnDocument.After },
				cancellationToken);
		}

		internal async Task<SMSwitchSession?> GetLatestSession(MobileNumber mobileWithCountryCode, CancellationToken cancellationToken = default)
		{
			var filter = Filter(mobileWithCountryCode)
				& Builders<SMSwitchSession>.Filter.Gt(t => t.ExpiryTimeUTC, DateTimeOffset.UtcNow)
				& Builders<SMSwitchSession>.Filter.Eq(t => t.SuccessfullyVerifiedTimestampUTC, null);

			// HasNotExpired applies conditions the filter above does not, so the newest match is not
			// necessarily usable and this cannot be Limit(1). It is bounded all the same: without a
			// limit this materialised every unexpired unverified session for the number.
			var candidateRecords = await _smSwitchSessionCollection.Find(filter)
				.SortByDescending(record => record.ExpiryTimeUTC)
				.Limit(MaximumCandidateSessionsToConsider)
				.ToListAsync(cancellationToken);

			return candidateRecords.FirstOrDefault(r => r.HasNotExpired(_smSwitchInitializer.SmsControls.MaximumFailedAttemptsToVerify));
		}

		internal async Task<SMSwitchSendSMSSession> GetOrCreateAndGetLatestSendSMSSession(MobileNumber mobileWithCountryCode, string shortMessageServiceMessage, CancellationToken cancellationToken = default)
		{
			var dedupeKey = CryptoUtils.ComputeSha512Hash($"{mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber}-{shortMessageServiceMessage}");
			var latestSession = await GetLatestSendSMSSession(dedupeKey, cancellationToken);
			if (latestSession != null)
			{
				return latestSession;
			}

			// Completed or expired sessions with the same dedupe key are simply left alone: the
			// filter in GetLatestSendSMSSession already excludes them, so re-sending the same text
			// to the same number just starts a new session.
			latestSession = new SMSwitchSendSMSSession()
			{
				SessionId = Guid.NewGuid().ToString(),
				DedupeKey = dedupeKey,
				CountryPhoneCodeAndPhoneNumber = mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber,
				ShortMessageServiceMessage = shortMessageServiceMessage,
				StartTimeUTC = DateTimeOffset.UtcNow,
				ExpiryTimeUTC = DateTimeOffset.UtcNow.AddSeconds(_smSwitchInitializer.SmsControls.SessionTimeoutInSeconds)
			};

			await _smSwitchSendSmsSessionCollection.InsertOneAsync(latestSession, cancellationToken: cancellationToken);

			return latestSession;
		}

		private async Task<SMSwitchSendSMSSession?> GetLatestSendSMSSession(string dedupeKey, CancellationToken cancellationToken = default)
		{
			var filter = FilterSendSMSSessionByDedupeKey(dedupeKey)
				& Builders<SMSwitchSendSMSSession>.Filter.Gt(t => t.ExpiryTimeUTC, DateTimeOffset.UtcNow)
				& Builders<SMSwitchSendSMSSession>.Filter.Eq(t => t.SuccessfullySentTimestampUTC, null);

			var candidateRecords = await _smSwitchSendSmsSessionCollection.Find(filter)
				.SortByDescending(record => record.ExpiryTimeUTC)
				.Limit(MaximumCandidateSessionsToConsider)
				.ToListAsync(cancellationToken);

			return candidateRecords.FirstOrDefault(r => r.HasNotExpired(_smSwitchInitializer.SmsControls.MaximumFailedAttemptsToVerify));
		}

		internal async Task UpdateSendSMSSession(SMSwitchSendSMSSession session, CancellationToken cancellationToken = default)
		{
			var options = new ReplaceOptions { IsUpsert = true };
			await _smSwitchSendSmsSessionCollection.ReplaceOneAsync(FilterSendSMSSession(session.SessionId), session, options, cancellationToken);
		}
	}
}
