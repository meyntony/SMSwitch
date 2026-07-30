namespace SMSwitch.Common
{
	/// <summary>
	/// The provider failover mechanics, which SendOTP and SendSMS previously each carried their own
	/// copy of. Static and free of any database or provider dependency so the queue behaviour can
	/// be tested on its own.
	/// </summary>
	internal static class ProviderFailover
	{
		/// <summary>
		/// Returns the session's existing queue if it still has providers left, otherwise builds a
		/// fresh one from the configured priority for the number's country phone code, falling back
		/// to <see cref="SmsControls.FallBackPriority"/>, repeated
		/// <see cref="SmsControls.MaxRoundRobinAttempts"/> times.
		/// </summary>
		internal static Queue<SmsProvider> BuildQueue(SmsControls smsControls, string countryPhoneCode, Queue<SmsProvider>? existingQueue)
		{
			if (existingQueue?.Count > 0)
			{
				return existingQueue;
			}

			var smsProviders = smsControls.PriorityBasedOnCountryPhoneCode.TryGetValue(countryPhoneCode, out var configuredProviders)
				? configuredProviders
				: smsControls.FallBackPriority;

			var smsProvidersQueue = new Queue<SmsProvider>();
			for (int i = 0; i < smsControls.MaxRoundRobinAttempts; i++)
			{
				foreach (var smsProvider in smsProviders)
				{
					smsProvidersQueue.Enqueue(smsProvider);
				}
			}

			return smsProvidersQueue;
		}

		/// <summary>
		/// Works through the queue until a provider succeeds or the queue runs out, recording each
		/// attempt as it goes.
		/// </summary>
		/// <param name="hasEarlierAttempts">
		/// Whether this session has already been sent through. When it has, the provider at the
		/// head of the queue is the one that handled the previous attempt, so it is discarded
		/// before trying the next one.
		/// </param>
		/// <remarks>
		/// The queue is deliberately left with the provider that succeeded at its head, because
		/// VerifyOTP peeks it to route the verification back to the provider that actually sent the
		/// OTP. An exhausted queue is what marks the session dead, see
		/// <c>SMSwitchSession.HasNotExpired</c>.
		/// </remarks>
		internal static async Task<TResult?> TrySendThroughQueue<TResult>(
			Queue<SmsProvider> smsProvidersQueue,
			bool hasEarlierAttempts,
			Func<SmsProvider, Task<TResult>> send,
			Func<TResult, bool> succeeded,
			Action<SmsProvider, TResult> recordAttempt)
		{
			TResult? result = default;
			var discardBeforeNextAttempt = hasEarlierAttempts;

			while (smsProvidersQueue.Count > 0)
			{
				if (discardBeforeNextAttempt)
				{
					smsProvidersQueue.Dequeue();
					if (smsProvidersQueue.Count == 0)
					{
						break;
					}
				}

				var smsProvider = smsProvidersQueue.Peek();
				result = await send(smsProvider);
				recordAttempt(smsProvider, result);
				discardBeforeNextAttempt = true;

				if (succeeded(result))
				{
					break;
				}
			}

			return result;
		}
	}
}
