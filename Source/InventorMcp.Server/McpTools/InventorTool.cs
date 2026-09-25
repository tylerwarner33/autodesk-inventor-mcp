using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

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
	/// 	See <c>.agents/rules/inventor-interop.md</c>, "A long snippet can terminate Inventor".
	/// </remarks>
	private static readonly TimeSpan _longExecutionWarningThreshold = TimeSpan.FromSeconds(20);

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
	/// <returns>
	/// 	The result, or a failure description.
	/// </returns>
	private static async Task<object> SafeAsync<TResult>(Func<Task<TResult>> work)
	{
		List<DialogReport> dialogs = DialogReports.Begin();

		try
		{
			object result = await work().ConfigureAwait(false) switch
			{
				Contracts.Models.ExecutionResult execution when execution.ElapsedMilliseconds > _longExecutionWarningThreshold.TotalMilliseconds =>
					execution with { Output = [.. execution.Output, LongExecutionWarning(execution.ElapsedMilliseconds)] },
				TResult other => other!,
				_ => null!
			};

			return dialogs.Count == 0 ? result : new { result, blockingDialogs = dialogs };
		}
		catch (InventorBridgeException exception)
		{
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

	private static string LongExecutionWarning(long elapsedMilliseconds) =>
		$"WARNING: this call held Inventor's main thread for {elapsedMilliseconds / 1000.0:0.0} s. Inventor cannot process " +
		"window messages while a call runs, and a long call that creates or edits sketches, views or documents can fill " +
		"the message queue and terminate Inventor (Win32Exception 1816). Split work like this into calls under " +
		$"{_longExecutionWarningThreshold.TotalSeconds:0} s.";
}
