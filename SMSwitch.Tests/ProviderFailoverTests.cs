using SMSwitch.Common;

namespace SMSwitch.Tests
{
	/// <summary>
	/// The failover mechanics decide which provider gets paid and, because VerifyOTP peeks the head
	/// of the queue, which provider a verification is routed back to. Extracting them out of
	/// SendOTP and SendSMS is what makes them reachable without a database.
	/// </summary>
	public sealed class ProviderFailoverTests
	{
		private static SmsControls Controls(
			byte maxRoundRobinAttempts = 1,
			List<SmsProvider>? fallBackPriority = null,
			Dictionary<string, List<SmsProvider>>? priorityBasedOnCountryPhoneCode = null) =>
			new()
			{
				MaximumFailedAttemptsToVerify = 3,
				SessionTimeoutInSeconds = 240,
				MaxRoundRobinAttempts = maxRoundRobinAttempts,
				FallBackPriority = fallBackPriority ?? [SmsProvider.Twilio, SmsProvider.Plivo],
				PriorityBasedOnCountryPhoneCode = priorityBasedOnCountryPhoneCode ?? []
			};

		[Fact]
		public void An_unknown_country_uses_the_fallback_priority()
		{
			var queue = ProviderFailover.BuildQueue(Controls(), "999", existingQueue: null);

			Assert.Equal([SmsProvider.Twilio, SmsProvider.Plivo], queue);
		}

		[Fact]
		public void A_configured_country_uses_its_own_priority_in_order()
		{
			var controls = Controls(priorityBasedOnCountryPhoneCode: new() { ["45"] = [SmsProvider.Plivo, SmsProvider.Twilio] });

			var queue = ProviderFailover.BuildQueue(controls, "45", existingQueue: null);

			Assert.Equal([SmsProvider.Plivo, SmsProvider.Twilio], queue);
		}

		[Fact]
		public void MaxRoundRobinAttempts_repeats_the_priority_list()
		{
			var queue = ProviderFailover.BuildQueue(Controls(maxRoundRobinAttempts: 2), "999", existingQueue: null);

			Assert.Equal([SmsProvider.Twilio, SmsProvider.Plivo, SmsProvider.Twilio, SmsProvider.Plivo], queue);
		}

		/// <summary>
		/// A session that already has providers left keeps them, so a resend continues where the
		/// previous attempt stopped rather than starting the list again.
		/// </summary>
		[Fact]
		public void An_existing_queue_with_providers_left_is_reused()
		{
			var existing = new Queue<SmsProvider>([SmsProvider.DevConsole]);

			var queue = ProviderFailover.BuildQueue(Controls(), "999", existing);

			Assert.Same(existing, queue);
		}

		[Fact]
		public void An_exhausted_existing_queue_is_rebuilt()
		{
			var queue = ProviderFailover.BuildQueue(Controls(), "999", new Queue<SmsProvider>());

			Assert.Equal([SmsProvider.Twilio, SmsProvider.Plivo], queue);
		}

		private static async Task<(List<SmsProvider> Attempted, Queue<SmsProvider> Queue, bool Result)> Send(
			Queue<SmsProvider> queue,
			bool hasEarlierAttempts,
			Func<SmsProvider, bool> outcome)
		{
			var attempted = new List<SmsProvider>();
			var result = await ProviderFailover.TrySendThroughQueue(
				queue,
				hasEarlierAttempts,
				send: smsProvider => Task.FromResult(outcome(smsProvider)),
				succeeded: sent => sent,
				recordAttempt: (smsProvider, _) => attempted.Add(smsProvider));
			return (attempted, queue, result);
		}

		[Fact]
		public async Task The_first_provider_is_tried_first_and_stops_on_success()
		{
			var (attempted, queue, result) = await Send(
				new Queue<SmsProvider>([SmsProvider.Twilio, SmsProvider.Plivo]),
				hasEarlierAttempts: false,
				outcome: _ => true);

			Assert.True(result);
			Assert.Equal([SmsProvider.Twilio], attempted);
			// VerifyOTP peeks the head to route verification, so the provider that succeeded has to
			// still be there.
			Assert.Equal(SmsProvider.Twilio, queue.Peek());
		}

		[Fact]
		public async Task A_failing_provider_falls_through_to_the_next()
		{
			var (attempted, queue, result) = await Send(
				new Queue<SmsProvider>([SmsProvider.Twilio, SmsProvider.Plivo]),
				hasEarlierAttempts: false,
				outcome: smsProvider => smsProvider == SmsProvider.Plivo);

			Assert.True(result);
			Assert.Equal([SmsProvider.Twilio, SmsProvider.Plivo], attempted);
			Assert.Equal(SmsProvider.Plivo, queue.Peek());
		}

		/// <summary>
		/// An empty queue is what marks a session dead in HasNotExpired, so every provider failing
		/// has to drain it.
		/// </summary>
		[Fact]
		public async Task Every_provider_failing_drains_the_queue()
		{
			var (attempted, queue, result) = await Send(
				new Queue<SmsProvider>([SmsProvider.Twilio, SmsProvider.Plivo]),
				hasEarlierAttempts: false,
				outcome: _ => false);

			Assert.False(result);
			Assert.Equal([SmsProvider.Twilio, SmsProvider.Plivo], attempted);
			Assert.Empty(queue);
		}

		/// <summary>
		/// On a resend the head of the queue is the provider that handled the previous attempt, so
		/// it is discarded and the next one is used.
		/// </summary>
		[Fact]
		public async Task A_resend_moves_past_the_provider_that_already_ran()
		{
			var (attempted, _, result) = await Send(
				new Queue<SmsProvider>([SmsProvider.Twilio, SmsProvider.Plivo]),
				hasEarlierAttempts: true,
				outcome: _ => true);

			Assert.True(result);
			Assert.Equal([SmsProvider.Plivo], attempted);
		}

		[Fact]
		public async Task A_resend_with_only_the_spent_provider_left_attempts_nothing()
		{
			var (attempted, queue, result) = await Send(
				new Queue<SmsProvider>([SmsProvider.Twilio]),
				hasEarlierAttempts: true,
				outcome: _ => true);

			Assert.False(result);
			Assert.Empty(attempted);
			Assert.Empty(queue);
		}

		[Fact]
		public async Task Round_robin_retries_the_same_provider_on_a_later_pass()
		{
			var queue = ProviderFailover.BuildQueue(Controls(maxRoundRobinAttempts: 2), "999", existingQueue: null);
			var callCount = 0;

			var (attempted, _, result) = await Send(queue, hasEarlierAttempts: false, outcome: _ => ++callCount == 3);

			Assert.True(result);
			Assert.Equal([SmsProvider.Twilio, SmsProvider.Plivo, SmsProvider.Twilio], attempted);
		}
	}
}
