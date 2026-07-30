using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Plivo.Utilities;
using SMSwitch.Services.Plivo.Database;
using SMSwitch.Services.Plivo.Database.DTOs;

namespace SMSwitch.Services.Plivo.WebHook
{
	public static class PlivoNotificationEndpoint
	{
		public const string PlivoNotificationRoute = "/plivonotification";

		private const string SignatureHeader = "X-Plivo-Signature-V3";
		private const string NonceHeader = "X-Plivo-Signature-V3-Nonce";

		public static RouteGroupBuilder GroupPlivoNotificationApisV1(this RouteGroupBuilder group)
		{
			group.MapGet(PlivoNotificationRoute, async (
				[FromServices] PlivoDbService plivoDbService,
				[FromServices] PlivoInitializer plivoInitializer,
				[FromServices] ILoggerFactory loggerFactory,
				HttpContext httpContext,
				[FromQuery] byte AttemptSequence,
				[FromQuery] string AttemptUUID,
				[FromQuery] string Channel,
				[FromQuery] string ChannelErrorCode,
				[FromQuery] string ChannelStatus,
				[FromQuery] string Recipient,
				[FromQuery] DateTime RequestTime,
				[FromQuery] string SessionStatus,
				[FromQuery] string SessionUUID) =>
			{
				var logger = loggerFactory.CreateLogger(typeof(PlivoNotificationEndpoint).FullName!);

				var authToken = plivoInitializer.PlivoSettings?.PlivoPrivateSettings.AuthToken;
				if (string.IsNullOrWhiteSpace(authToken))
				{
					// Fail closed. Treating a missing check as "no check required" left this
					// endpoint anonymous to anyone who could reach it.
					logger.LogError("Plivo is not configured, so the delivery-notification webhook is rejecting every caller.");
					return Results.Unauthorized();
				}

				// Verify against the URL this application handed to Plivo rather than one rebuilt
				// from the incoming request: that is the URL Plivo actually signed, and it does not
				// depend on how a proxy rewrote the scheme or host on the way in.
				var signedUri = $"{plivoInitializer.NotificationUrl}{httpContext.Request.QueryString}";

				if (!IsFromPlivo(
						signedUri,
						authToken,
						httpContext.Request.Headers[SignatureHeader],
						httpContext.Request.Headers[NonceHeader]))
				{
					logger.LogWarning("Rejected a Plivo delivery notification whose {SignatureHeader} did not verify", SignatureHeader);
					return Results.Unauthorized();
				}

				try
				{
					await plivoDbService.UpdateSessionUUID(Recipient, SessionUUID, new PlivoNotification(AttemptSequence, AttemptUUID, Channel, ChannelErrorCode, ChannelStatus, RequestTime, SessionStatus, DateTimeOffset.UtcNow), httpContext.RequestAborted);
					return Results.Ok();
				}
				catch (Exception ex)
				{
					// Exception text can carry connection strings and host names, and this endpoint
					// faces the public internet, so log it rather than returning it.
					logger.LogError(ex, "Failed to record the Plivo delivery notification for session {SessionUUID}", SessionUUID);
					return Results.StatusCode(StatusCodes.Status500InternalServerError);
				}

			})
			.Produces(StatusCodes.Status200OK)
			.Produces(StatusCodes.Status401Unauthorized)
			.Produces(StatusCodes.Status500InternalServerError);

			return group;
		}

		/// <summary>
		/// Checks Plivo's request signature. This replaced a shared secret appended to the callback
		/// URL as a query parameter, which meant the secret was written to Plivo's outbound logs,
		/// every proxy in between, and this server's own access log. The signature is a per-request
		/// HMAC over the URL and a nonce, so nothing secret travels in the URL at all.
		/// </summary>
		/// <remarks>
		/// The header can carry more than one signature, comma separated, when an account has
		/// several auth tokens in play, so any one of them verifying is enough.
		/// </remarks>
		internal static bool IsFromPlivo(string signedUri, string authToken, string? signatureHeader, string? nonce)
		{
			if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(nonce))
			{
				return false;
			}

			foreach (var signature in signatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				try
				{
					if (XPlivoSignatureV3.VerifySignature(signedUri, nonce, signature, authToken, "GET", []))
					{
						return true;
					}
				}
				catch
				{
					// A malformed signature must read as "not from Plivo", not as a 500.
				}
			}

			return false;
		}
	}
}
