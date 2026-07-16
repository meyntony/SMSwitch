using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
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
				var expectedSecret = plivoInitializer.PlivoSettings?.PlivoPrivateSettings?.WebhookSecret;
				if (!string.IsNullOrWhiteSpace(expectedSecret) && !FixedTimeEquals(secret, expectedSecret))
				{
					return Results.Unauthorized();
				}
				try
				{
					await plivoDbService.UpdateSessionUUID(Recipient, SessionUUID, new PlivoNotification(AttemptSequence, AttemptUUID, Channel, ChannelErrorCode, ChannelStatus, RequestTime, SessionStatus, DateTimeOffset.UtcNow));
					return Results.Ok();
				}
				catch (Exception ex)
				{
					return Results.Problem(ex.Message);
				}

			})
			.Produces(StatusCodes.Status200OK);

			return group;
		}

		private static bool FixedTimeEquals(string? provided, string expected) =>
			provided is not null &&
			CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
	}
}
