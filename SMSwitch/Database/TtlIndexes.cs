using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace SMSwitch.Database
{
	/// <summary>
	/// Creating and amending the TTL indexes that expire stored sessions.
	///
	/// Extracted because three collections need it and because getting it wrong is expensive here:
	/// these indexes are created from <c>IHostedService.StartAsync</c>, so an unhandled index conflict
	/// does not degrade a query, it stops the application from starting.
	/// </summary>
	internal static class TtlIndexes
	{
		private const int IndexOptionsConflictErrorCode = 85;
		private const int IndexKeySpecsConflictErrorCode = 86;

		/// <summary>
		/// Ensures <paramref name="dottedDateField"/> carries a TTL index expiring
		/// <paramref name="expireAfter"/> after the stored date, or removes the index when
		/// <paramref name="expireAfter"/> is null.
		///
		/// The field is dotted rather than a lambda because the driver stores a
		/// <see cref="DateTimeOffset"/> as a <c>{ DateTime, Ticks, Offset }</c> document and a TTL index
		/// needs a real BSON date, so callers target the <c>.DateTime</c> sub-field.
		/// </summary>
		internal static async Task EnsureAsync<TDocument>(
			IMongoCollection<TDocument> collection,
			string dottedDateField,
			TimeSpan? expireAfter,
			ILogger logger,
			CancellationToken cancellationToken = default)
		{
			if (expireAfter is null)
			{
				// Retention was turned off. Simply not creating the index would leave any index a
				// previous configuration created still quietly deleting documents, so the setting
				// would appear to do nothing on every existing deployment.
				await DropAsync(collection, dottedDateField, logger, cancellationToken);
				return;
			}

			var indexModel = new CreateIndexModel<TDocument>(
				new BsonDocument(dottedDateField, 1),
				new CreateIndexOptions { ExpireAfter = expireAfter });

			try
			{
				await collection.Indexes.CreateOneAsync(indexModel, cancellationToken: cancellationToken);
			}
			catch (MongoCommandException exception) when (exception.Code is IndexOptionsConflictErrorCode or IndexKeySpecsConflictErrorCode)
			{
				// The index exists with a different expireAfterSeconds and MongoDB refuses to recreate
				// it, so amend it in place. Without this, changing the retention setting would throw
				// out of StartAsync and the application would not boot.
				await AmendAsync(collection, dottedDateField, expireAfter.Value, logger, cancellationToken);
			}
		}

		/// <summary>
		/// Finds the existing index on this field. Matching requires a single-key index: several of
		/// these collections have compound or other single-field indexes, and amending or dropping one
		/// of those would break the queries that depend on it.
		/// </summary>
		private static async Task<BsonValue?> FindIndexName<TDocument>(
			IMongoCollection<TDocument> collection,
			string dottedDateField,
			CancellationToken cancellationToken)
		{
			using var cursor = await collection.Indexes.ListAsync(cancellationToken);
			var indexes = await cursor.ToListAsync(cancellationToken);

			var existing = indexes.FirstOrDefault(index =>
				index.TryGetValue("key", out var key)
				&& key is BsonDocument keyDocument
				&& keyDocument.ElementCount == 1
				&& keyDocument.Contains(dottedDateField));

			return existing is not null && existing.TryGetValue("name", out var name) ? name : null;
		}

		/// <summary>
		/// collMod addresses an index by name and fails on a name that is not there, so the existing
		/// name is looked up rather than assumed - releases have differed on whether these indexes
		/// were created named or left for the server to name.
		/// </summary>
		private static async Task AmendAsync<TDocument>(
			IMongoCollection<TDocument> collection,
			string dottedDateField,
			TimeSpan expireAfter,
			ILogger logger,
			CancellationToken cancellationToken)
		{
			var name = await FindIndexName(collection, dottedDateField, cancellationToken);

			if (name is null)
			{
				// Nothing single-key on that field to amend, so the conflict was about something else.
				// Leave the collection alone rather than inventing an index.
				logger.LogWarning(
					"Unable to apply the configured retention to {Collection}: no single-field index on {Field} was found to amend.",
					collection.CollectionNamespace.CollectionName,
					dottedDateField);
				return;
			}

			await collection.Database.RunCommandAsync<BsonDocument>(
				new BsonDocument
				{
					{ "collMod", collection.CollectionNamespace.CollectionName },
					{ "index", new BsonDocument
						{
							{ "name", name },
							{ "expireAfterSeconds", expireAfter.TotalSeconds }
						}
					}
				},
				cancellationToken: cancellationToken);

			logger.LogInformation(
				"Amended the retention on {Collection}.{Field} to {ExpireAfterSeconds} seconds.",
				collection.CollectionNamespace.CollectionName,
				dottedDateField,
				expireAfter.TotalSeconds);
		}

		private static async Task DropAsync<TDocument>(
			IMongoCollection<TDocument> collection,
			string dottedDateField,
			ILogger logger,
			CancellationToken cancellationToken)
		{
			var name = await FindIndexName(collection, dottedDateField, cancellationToken);

			if (name is null)
			{
				return;
			}

			await collection.Indexes.DropOneAsync(name.AsString, cancellationToken);

			logger.LogWarning(
				"Retention is disabled, so the expiry index on {Collection}.{Field} was removed and these documents will now be kept indefinitely.",
				collection.CollectionNamespace.CollectionName,
				dottedDateField);
		}
	}
}
