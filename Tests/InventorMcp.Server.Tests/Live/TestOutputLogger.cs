using Microsoft.Extensions.Logging;

namespace InventorMcp.Server.Tests.Live;

/// <summary>
/// 	Writes the log of the code under test to the output of the current test, so a failure shows what it saw.
/// </summary>
internal sealed class TestOutputLogger<T> : ILogger<T>
{
	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		// The output helper exists only while a test runs, and a background task can log after its test ended.
		try
		{
			TestContext.Current.TestOutputHelper?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {logLevel}: {formatter(state, exception)}");
		}
		catch (InvalidOperationException)
		{
		}
	}
}