using Plivo.Utilities;
using SMSwitch.Services.Plivo.WebHook;

namespace SMSwitch.Tests
{
	/// <summary>
	/// The webhook is reachable by anyone who can reach the host, and accepting a forged call lets
	/// an attacker mark any session delivered. Signatures are generated with Plivo's own helper so
	/// these test the real algorithm rather than a reimplementation of it.
	/// </summary>
	public sealed class PlivoWebhookSignatureTests
	{
		private const string AuthToken = "test-auth-token";
		private const string Nonce = "12345678901234567890";
		private const string SignedUri = "https://example.test/smswitch/plivonotification?SessionUUID=abc&ChannelStatus=delivered";

		private static string ValidSignature(string uri = SignedUri, string nonce = Nonce, string authToken = AuthToken) =>
			XPlivoSignatureV3.ComputeSignature(uri, nonce, authToken, [], "GET");

		[Fact]
		public void A_genuine_signature_is_accepted()
		{
			Assert.True(PlivoNotificationEndpoint.IsFromPlivo(SignedUri, AuthToken, ValidSignature(), Nonce));
		}

		/// <summary>
		/// Plivo may send several comma-separated signatures when an account has more than one
		/// auth token, so any one of them verifying has to be enough.
		/// </summary>
		[Fact]
		public void One_valid_signature_among_several_is_accepted()
		{
			var header = $"someothersignature,{ValidSignature()},yetanother";
			Assert.True(PlivoNotificationEndpoint.IsFromPlivo(SignedUri, AuthToken, header, Nonce));
		}

		[Fact]
		public void A_signature_for_a_different_url_is_rejected()
		{
			var signatureForAnotherSession = ValidSignature("https://example.test/smswitch/plivonotification?SessionUUID=someone-elses");
			Assert.False(PlivoNotificationEndpoint.IsFromPlivo(SignedUri, AuthToken, signatureForAnotherSession, Nonce));
		}

		[Fact]
		public void A_signature_made_with_the_wrong_auth_token_is_rejected()
		{
			Assert.False(PlivoNotificationEndpoint.IsFromPlivo(SignedUri, AuthToken, ValidSignature(authToken: "not-our-token"), Nonce));
		}

		[Fact]
		public void A_replayed_signature_with_a_different_nonce_is_rejected()
		{
			Assert.False(PlivoNotificationEndpoint.IsFromPlivo(SignedUri, AuthToken, ValidSignature(), "09876543210987654321"));
		}

		[Theory]
		[InlineData(null, Nonce)]
		[InlineData("", Nonce)]
		[InlineData("   ", Nonce)]
		[InlineData("not-a-signature", Nonce)]
		[InlineData("!!!not base64!!!", Nonce)]
		public void A_missing_or_malformed_signature_is_rejected(string? signatureHeader, string nonce)
		{
			Assert.False(PlivoNotificationEndpoint.IsFromPlivo(SignedUri, AuthToken, signatureHeader, nonce));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		public void A_missing_nonce_is_rejected(string? nonce)
		{
			Assert.False(PlivoNotificationEndpoint.IsFromPlivo(SignedUri, AuthToken, ValidSignature(), nonce));
		}
	}
}
