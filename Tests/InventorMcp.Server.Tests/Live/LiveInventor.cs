using System.Text.Json;

using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using Microsoft.Extensions.Logging;

namespace InventorMcp.Server.Tests.Live;

/// <summary>
/// 	Access to a live Inventor with the add-in loaded, for the tests that set <c>INVENTORMCP_LIVE_TESTS=1</c>.
/// </summary>
internal static class LiveInventor
{
	public const string EnableVariable = "INVENTORMCP_LIVE_TESTS";

	/// <summary>
	/// 	Skips the test unless the live level is on.
	/// </summary>
	/// <remarks>
	/// 	The value is trimmed, because <c>set INVENTORMCP_LIVE_TESTS=1 &amp;&amp; ...</c> in cmd stores a trailing space.
	/// </remarks>
	public static void Require() =>
		Assert.SkipUnless(
			Environment.GetEnvironmentVariable(EnableVariable)?.Trim() == "1",
			$"Set {EnableVariable}=1, and start Inventor with the add-in, to run the live tests.");

	/// <summary>
	/// 	A client of the real pipe, with the real detector and the real clock.
	/// </summary>
	public static BridgeClient CreateClient() =>
		new(new TestOutputLogger<BridgeClient>(), new BlockingDialogs(), TimeProvider.System);

	/// <summary>
	/// 	The longest a live call may take, so a call that hangs fails its test and does not stop the run.
	/// </summary>
	private static readonly TimeSpan _callTimeout = TimeSpan.FromSeconds(60);

	/// <summary>
	/// 	A name that iLogic has not seen, because it shows the same rule error only once in 120 minutes.
	/// </summary>
	public static string UniqueName(string prefix) => $"{prefix}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";

	/// <summary>
	/// 	Runs a C# snippet through the bridge, the same way <c>inventor_eval_csharp</c> does.
	/// </summary>
	/// <remarks>
	/// 	The snippets of these tests work only on documents they create, so the check for unsaved changes in the active
	/// 	document is turned off.
	/// </remarks>
	public static Task<ExecutionResult> EvalAsync(BridgeClient client, string code, CancellationToken cancellationToken) =>
		client.InvokeAsync<ExecutionResult>(
			BridgeOperations.EvalCSharp,
			new ExecuteRequest(code, null, AllowUnsavedChanges: true),
			cancellationToken).WaitAsync(_callTimeout, cancellationToken);

	/// <summary>
	/// 	Runs an iLogic rule body through the bridge, the same way <c>inventor_run_ilogic</c> does.
	/// </summary>
	public static Task<ExecutionResult> RunILogicAsync(BridgeClient client, string code, string documentName, CancellationToken cancellationToken) =>
		client.InvokeAsync<ExecutionResult>(
			BridgeOperations.RunILogic,
			new ExecuteRequest(code, documentName, AllowUnsavedChanges: true),
			cancellationToken).WaitAsync(_callTimeout, cancellationToken);

	/// <summary>
	/// 	A snippet that creates a temporary part, adds a rule that fails, runs it through the iLogic automation object,
	/// 	and closes the part without a save. This is the path of the real incident.
	/// </summary>
	public static string ILogicErrorSnippet(string ruleName, string missingRule) => $$"""
		dynamic automation = Application.ApplicationAddIns.ItemById["{3BDD8D79-2179-4B11-8A5A-257B1C0263AC}"].Automation;
		Document part = (Document)Application.Documents.Add(
			DocumentTypeEnum.kPartDocumentObject,
			Application.FileManager.GetTemplateFile(DocumentTypeEnum.kPartDocumentObject),
			false);
		try
		{
			automation.AddRule(part, "{{ruleName}}", "iLogicVb.RunExternalRule(\"{{missingRule}}\")");
			automation.RunRule(part, "{{ruleName}}");
		}
		finally
		{
			part.Close(true);
		}
		return "done";
		""";

	/// <summary>
	/// 	A snippet that shows a WinForms message box owned by the Inventor main frame, so the main frame is disabled.
	/// </summary>
	/// <remarks>
	/// 	Through reflection, because a snippet has no reference to System.Windows.Forms.
	/// </remarks>
	public static string MessageBoxSnippet(string caption, string buttons) => $$"""
		System.Reflection.Assembly forms = System.Reflection.Assembly.Load("System.Windows.Forms");
		dynamic owner = Activator.CreateInstance(forms.GetType("System.Windows.Forms.NativeWindow"));
		owner.AssignHandle(new IntPtr(Application.MainFrameHWND));
		try
		{
			Type buttonsType = forms.GetType("System.Windows.Forms.MessageBoxButtons");
			System.Reflection.MethodInfo show = forms.GetType("System.Windows.Forms.MessageBox").GetMethod(
				"Show",
				new[] { forms.GetType("System.Windows.Forms.IWin32Window"), typeof(string), typeof(string), buttonsType });
			return show.Invoke(null, new object[] { owner, "McpTest message text", "{{caption}}", Enum.Parse(buttonsType, "{{buttons}}") }).ToString();
		}
		finally
		{
			owner.ReleaseHandle();
		}
		""";

	/// <summary>
	/// 	Waits in real time until a dialog blocks Inventor, and returns it.
	/// </summary>
	public static async Task<(int ProcessId, DialogSnapshot Dialog)> WaitForDialogAsync(BridgeClient client, CancellationToken cancellationToken)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(20);

		while (DateTime.UtcNow < deadline)
		{
			if (await client.GetBlockStateAsync(cancellationToken) is { Blocked: true, Dialogs: [DialogSnapshot dialog, ..] } state)
				return (state.ProcessId, dialog);

			await Task.Delay(250, cancellationToken);
		}

		throw new TimeoutException("No dialog blocked Inventor within 20 s.");
	}

	/// <summary>
	/// 	The variable that names a part saved by an earlier Inventor release, for the migration dialog tests.
	/// </summary>
	public const string OldReleasePartVariable = "INVENTORMCP_LIVE_OLD_RELEASE_PART";

	/// <summary>
	/// 	Copies the part that <see cref="OldReleasePartVariable"/> names to a new temporary folder, or skips the test.
	/// </summary>
	/// <remarks>
	/// 	A save of the copy migrates the copy only. Inventor cannot save a file in an earlier release's format, so a test
	/// 	cannot make this part itself.
	/// </remarks>
	public static string CopyOldReleasePart()
	{
		string? source = Environment.GetEnvironmentVariable(OldReleasePartVariable)?.Trim();
		Assert.SkipUnless(
			source is { Length: > 0 } && File.Exists(source),
			$"Set {OldReleasePartVariable} to a part saved by an earlier Inventor release, to run the migration dialog tests.");

		string folder = Path.Combine(Path.GetTempPath(), UniqueName("InventorMcpMigration"));
		_ = Directory.CreateDirectory(folder);
		string copy = Path.Combine(folder, Path.GetFileName(source));
		File.Copy(source, copy);

		return copy;
	}

	/// <summary>
	/// 	A snippet that opens a part, changes it, and saves it, which shows the migration dialog for a part of an earlier
	/// 	release. It returns the release that last saved the part.
	/// </summary>
	/// <remarks>
	/// 	The part stays open when the save fails, so the test closes it.
	/// </remarks>
	public static string SaveOldReleasePartSnippet(string path) => $$"""
		bool silent = Application.SilentOperation;
		Application.SilentOperation = false;
		try
		{
			PartDocument part = (PartDocument)Application.Documents.Open(@"{{path}}", true);
			part.ComponentDefinition.Parameters.UserParameters.AddByExpression("McpMigrationTest", "1 in", UnitsTypeEnum.kInchLengthUnits);
			part.Save();
			string savedBy = part.PropertySets["Design Tracking Properties"].ItemByPropId[67].Value.ToString();
			part.Close(true);
			return savedBy;
		}
		finally
		{
			Application.SilentOperation = silent;
		}
		""";

	/// <summary>
	/// 	A snippet that closes every open document from the folder, with no save.
	/// </summary>
	public static string CloseDocumentsUnderSnippet(string folder) => $$"""
		foreach (Document document in Application.Documents.Cast<Document>().ToList())
			if (document.FullFileName.StartsWith(@"{{folder}}", StringComparison.OrdinalIgnoreCase))
				document.Close(true);
		return null;
		""";

	public static string Describe(ExecutionResult result) => JsonSerializer.Serialize(result);
}