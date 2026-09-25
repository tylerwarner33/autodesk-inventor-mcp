namespace InventorMcp.AddIn.Bridge;

/// <summary>
/// 	Lets a log line through once per time window for each key, and counts the repeats in between.
/// </summary>
/// <remarks>
/// 	A failure that repeats in a loop once wrote 466,356 lines of "All pipe instances are busy" to <c>addin.log</c> in
/// 	4 minutes. See <c>Docs/Research/Usage-Findings-And-Knowledge-Delivery.md</c>, "Other log facts".
/// 	No Inventor types, so the test project compiles this file too.
/// </remarks>
/// <param name="timeProvider">
/// 	The clock.
/// </param>
/// <param name="window">
/// 	How long a key stays quiet after a line.
/// </param>
internal sealed class LogThrottle(TimeProvider timeProvider, TimeSpan window)
{
	private readonly Dictionary<string, (DateTimeOffset Written, int Suppressed)> _keys = new(StringComparer.Ordinal);
	private readonly object _gate = new();

	/// <summary>
	/// 	Decides whether a line is written.
	/// </summary>
	/// <param name="key">
	/// 	What makes two lines the same, ex. the message text.
	/// </param>
	/// <param name="message">
	/// 	The line.
	/// </param>
	/// <returns>
	/// 	The line to write, with the count of the lines held back since the last one, or null to write nothing.
	/// </returns>
	public string? Filter(string key, string message)
	{
		DateTimeOffset now = timeProvider.GetUtcNow();

		lock (_gate)
		{
			if (_keys.TryGetValue(key, out (DateTimeOffset Written, int Suppressed) entry) && now - entry.Written < window)
			{
				_keys[key] = (entry.Written, entry.Suppressed + 1);
				return null;
			}

			_keys[key] = (now, 0);

			return entry.Suppressed > 0
				? $"{message} (and {entry.Suppressed} more like it since {entry.Written:O}, not written)"
				: message;
		}
	}
}
