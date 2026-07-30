using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using SMSwitch.Services.Plivo.Database;
using SMSwitch.Services.Plivo.Database.DTOs;
using System.Security.Cryptography;
using System.Text;

namespace SMSwitch.Services.Plivo.WebHook
{
	public static class PlivoNotificationEndpoint
	{
		public const string PlivoNotificationRoute = "/plivonotification";
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
				[FromQuery] string SessionUUID,
				[FromQuery] string? secret) =>
			{
				var logger = loggerFactory.CreateLogger(typeof(PlivoNotificationEndpoint).FullName!);

				// Fail closed. Treating an unset secret as "no check required" left this endpoint
				// anonymous, so anyone who could reach it could inject delivery notifications for
				// any session UUID.
				var expectedSecret = plivoInitializer.PlivoSettings?.PlivoPrivateSettings?.WebhookSecret;
				if (string.IsNullOrWhiteSpace(expectedSecret))
				{
					logger.LogError("SMSwitchSettings:Plivo:WebhookSecret is not configured, so the Plivo delivery-notification webhook is rejecting every caller. Set it to the same value registered with Plivo.");
					return Results.Unauthorized();
				}
				if (!FixedTimeEquals(secret, expectedSecret))
				{
					logger.LogWarning("Rejected a Plivo delivery notification carrying a missing or incorrect secret");
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

		private static bool FixedTimeEquals(string? provided, string expected) =>
			provided is not null &&
			CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
	}
}
