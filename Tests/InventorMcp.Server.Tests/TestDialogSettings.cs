using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests;

/// <summary>
/// 	Dialog settings for the tests, built from the embedded defaults and a user file, with no file on disk.
/// </summary>
internal static class TestDialogSettings
{
	private static readonly Lazy<string> _defaultText = new(ReadDefaultText);

	/// <summary>
	/// 	The settings of a user who lets the server run a flagged iLogic rule and trust it.
	/// </summary>
	/// <remarks>
	/// 	The embedded defaults leave both security dialogs open, so the tests of those clicks opt in.
	/// </remarks>
	public static DialogSettings TrustsRules { get; } =
		WithUserFile("""{ "iLogicSecurityAlert": "Run the rule", "iLogicSecurityAdvisor": "OK" }""");

	/// <summary>
	/// 	The embedded defaults with a user file.
	/// </summary>
	/// <param name="userText">
	/// 	The text of the user file.
	/// </param>
	/// <returns>
	/// 	The settings.
	/// </returns>
	public static DialogSettings WithUserFile(string userText) =>
		DialogSettings.Create(_defaultText.Value, userText, autoCloseValue: null, acceptMigrationValue: null);

	private static string ReadDefaultText()
	{
		using Stream stream = typeof(DialogSettings).Assembly.GetManifestResourceStream("InventorMcp.Server.DialogSettings.json")!;
		using StreamReader reader = new(stream);

		return reader.ReadToEnd();
	}
}
