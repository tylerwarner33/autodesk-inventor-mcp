using System.Diagnostics;
using System.Runtime.CompilerServices;

using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

/// <summary>
/// 	Tools that read and drive the running Inventor session.
/// </summary>
/// <remarks>
/// 	Every tool goes through <see cref="BridgeClient"/> to the add-in hosted inside Inventor.exe.
/// 	Tools live here rather than in the add-in so they can change without restarting Inventor.
/// </remarks>
[McpServerToolType]
internal static partial class InventorTool
{
	/// <summary>
	/// 	Run time on Inventor's main thread above which an execution result carries a warning.
	/// </summary>
	/// <remarks>
	/// 	The same limit as the server instructions and the tool descriptions, so a model reads one number.
	/// 	See <c>.agents/rules/inventor-interop.md</c>, "A long snippet can terminate Inventor".
	/// </remarks>
	private static readonly TimeSpan _longExecutionWarningThreshold = TimeSpan.FromSeconds(10);

	/// <summary>
	/// 	The log for tool results, set at startup.
	/// </summary>
	/// <remarks>
	/// 	The tools are static, and <see cref="SafeAsync"/> turns every failure into a normal payload, so without this
	/// 	line the server log cannot count failures.
	/// </remarks>
	public static ILogger ResultLogger { get; set; } = NullLogger.Instance;

	/// <summary>
	/// 	Runs a bridge call and turns a bridge failure into a result the model can read.
	/// </summary>
	/// <remarks>
	/// 	Throwing would surface as an opaque protocol error.
	/// 	A structured object lets the model see that Inventor is merely busy or has no open document, and act on it.
	/// 	A dialog that blocked the call is added as <c>blockingDialogs</c>, so the model knows, ex., that a rule failed.
	/// </remarks>
	/// <typeparam name="TResult">
	/// 	Type the operation returns.
	/// </typeparam>
	/// <param name="work">
	/// 	The bridge call to run.
	/// </param>
	/// <param name="warnOnLongExecution">
	/// 	False for a call that cannot be split, so a warning to split it would only be noise.
	/// </param>
	/// <param name="tool">
	/// 	The tool method, for the log.
	/// </param>
	/// <returns>
	/// 	The result, or a failure description.
	/// </returns>
	private static async Task<object> SafeAsync<TResult>(
		Func<Task<TResult>> work,
		bool warnOnLongExecution = true,
		[CallerMemberName] string tool = "")
	{
		List<DialogReport> dialogs = DialogReports.Begin();
		Stopwatch stopwatch = Stopwatch.StartNew();

		try
		{
			TResult raw = await work().ConfigureAwait(false);
			stopwatch.Stop();

			object result = raw switch
			{
				Contracts.Models.ExecutionResult execution => Complete(execution, stopwatch.ElapsedMilliseconds, warnOnLongExecution),
				_ => raw!
			};

			if (result is Contracts.Models.ExecutionResult { Succeeded: false } failed)
			{
				ResultLogger.LogInformation(
					"Tool {Tool} result: the code failed with {ErrorType} after {WallClockMilliseconds} ms.",
					tool,
					failed.ExceptionType ?? (failed.Diagnostics.Count > 0 ? "compile errors" : "no exception type"),
					stopwatch.ElapsedMilliseconds);
			}

			return dialogs.Count == 0 ? result : new { result, blockingDialogs = dialogs };
		}
		catch (InventorBridgeException exception)
		{
			ResultLogger.LogInformation(
				"Tool {Tool} result: error {ErrorCode} after {WallClockMilliseconds} ms.",
				tool,
				exception.Code,
				stopwatch.ElapsedMilliseconds);

			return dialogs.Count == 0
				? new
				{
					error = exception.Code,
					message = exception.Message,
					detail = exception.Detail
				}
				: new
				{
					error = exception.Code,
					message = exception.Message,
					detail = exception.Detail,
					blockingDialogs = dialogs
				};
		}
	}

	private static Contracts.Models.ExecutionResult Complete(Contracts.Models.ExecutionResult execution, long wallClockMilliseconds, bool warnOnLongExecution)
	{
		Contracts.Models.ExecutionResult timed = execution with { WallClockMilliseconds = wallClockMilliseconds };

		return warnOnLongExecution && execution.ElapsedMilliseconds > _longExecutionWarningThreshold.TotalMilliseconds
			? timed with { Output = [.. execution.Output, LongExecutionWarning(execution.ElapsedMilliseconds)] }
			: timed;
	}

	private static string LongExecutionWarning(long elapsedMilliseconds) =>
		$"WARNING: this call held Inventor's main thread for {elapsedMilliseconds / 1000.0:0.0} s. Inventor cannot process " +
		"window messages while a call runs, and a long call that creates or edits sketches, views or documents can fill " +
		"the message queue and terminate Inventor (Win32Exception 1816). Split work like this into calls under " +
		$"{_longExecutionWarningThreshold.TotalSeconds:0} s.";
}
