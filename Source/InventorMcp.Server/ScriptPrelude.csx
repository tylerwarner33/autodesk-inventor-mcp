// Helpers for inventor_eval_csharp snippets. The server puts this before a snippet that calls one of them.
// Keep the names specific, so they cannot collide with a name that a snippet declares.
// Only declarations: nothing here runs unless the snippet calls it.
// See Source/InventorMcp.Server/Services/ScriptPrelude.cs and Docs/Architecture.md, "Script helpers travel with the snippet".

#nullable enable

// ---- Documents -----------------------------------------------------------------------------------------------------

List<Document> __documentsOpenedHere = new();

// Returns the open document that a name means, or null when none is open.
// The name is a full document name (the path with the model state, ex. C:\Parts\Bracket.ipt<State2>), a path, or a
// display name. The rules are the same as for the documentName of a tool: an open model state has a factory and a
// member with one full document name, and the factory wins. A name of more than one document throws, with the full
// document names to use.
Document? FindOpenDocument(string name)
{
	// COM gives null, not an empty string, for the path and the full document name of a document that was never saved.
	bool Same(string? value) => !string.IsNullOrEmpty(value) && string.Equals(value, name, StringComparison.OrdinalIgnoreCase);
	string FullName(Document document) => document.FullDocumentName ?? "";
	bool IsFactory(Document document) => document switch
	{
		PartDocument part => part.ComponentDefinition.IsModelStateFactory,
		AssemblyDocument assembly => assembly.ComponentDefinition.IsModelStateFactory,
		_ => false
	};

	// A document that was never saved has no full document name, so it is a group of its own.
	List<List<Document>> matches = new();
	foreach (Document open in Application.Documents)
	{
		if (!(Same(open.FullDocumentName) || Same(open.FullFileName) || Same(open.DisplayName)))
			continue;

		List<Document>? group = FullName(open).Length == 0 ? null : matches.FirstOrDefault(
			existing => string.Equals(FullName(existing[0]), FullName(open), StringComparison.OrdinalIgnoreCase));

		if (group is null)
			matches.Add(new List<Document> { open });
		else
			group.Add(open);
	}

	if (matches.Count > 1)
		throw new ArgumentException($"'{name}' matches {matches.Count} open documents. Use one of these full document names: {string.Join("; ", matches.Select(group => FullName(group[0]).Length > 0 ? FullName(group[0]) : group[0].DisplayName))}.");

	return matches.Count == 0 ? null : matches[0].FirstOrDefault(IsFactory) ?? matches[0][0];
}

// Returns the open document that a name means (see FindOpenDocument), or opens the file and remembers that this
// snippet opened it. Documents.Open takes a full document name too, to open one model state.
Document OpenOrReuseDocument(string fullFileName, bool visible = false)
{
	if (FindOpenDocument(fullFileName) is Document open)
		return open;

	Document opened = Application.Documents.Open(fullFileName, visible);
	__documentsOpenedHere.Add(opened);
	return opened;
}

// Closes only the documents that OpenOrReuseDocument opened, with no save.
// Drawings reference assemblies and assemblies reference parts, so close in that order, the same as inventor_run_plugin.
void CloseDocumentsOpenedHere()
{
	foreach (DocumentTypeEnum type in new[] { DocumentTypeEnum.kDrawingDocumentObject, DocumentTypeEnum.kPresentationDocumentObject, DocumentTypeEnum.kAssemblyDocumentObject, DocumentTypeEnum.kPartDocumentObject })
	{
		foreach (Document document in __documentsOpenedHere.Where(document => document.DocumentType == type).ToList())
		{
			// Closing one document can close another, and Close on a closed document throws.
			bool stillOpen = Application.Documents.OfType<Document>().Any(open => ReferenceEquals(open, document));
			if (stillOpen)
			{
				try { document.Close(SkipSave: true); }
				catch (System.Runtime.InteropServices.COMException exception) { Log($"Could not close '{document.DisplayName}': {exception.Message}"); }
			}

			__documentsOpenedHere.Remove(document);
		}
	}
}

// ---- iLogic --------------------------------------------------------------------------------------------------------

// The iLogic automation object. Its methods need an argument typed Inventor.Document, so the helpers below cast.
dynamic ILogicAutomation() =>
	Application.ApplicationAddIns.ItemById["{3BDD8D79-2179-4B11-8A5A-257B1C0263AC}"].Automation;

// The names of the rules in a document, with the active flag and the length of the text.
List<(string Name, bool IsActive, int Length)> ILogicRuleNames(object document)
{
	List<(string Name, bool IsActive, int Length)> rules = new();
	dynamic automation = ILogicAutomation();
	dynamic all = automation.get_Rules((Document)document);
	if (all is null)
		return rules;

	foreach (dynamic rule in all)
		rules.Add(((string)rule.Name, (bool)rule.IsActive, ((string)rule.Text).Length));

	return rules;
}

// The text of one rule, or null when the document has no rule with that name.
string? ILogicRuleText(object document, string ruleName)
{
	dynamic rule = ILogicAutomation().GetRule((Document)document, ruleName);
	return rule is null ? null : (string)rule.Text;
}

// Sets the text of a rule, and adds the rule when it does not exist.
// A rule text change can make iLogic ask the user to trust the rule at the next run (an iLogic Security Alert).
void SetILogicRuleText(object document, string ruleName, string text)
{
	dynamic automation = ILogicAutomation();
	dynamic rule = automation.GetRule((Document)document, ruleName);
	if (rule is null)
		automation.AddRule((Document)document, ruleName, text);
	else
		rule.Text = text;
}

// Runs one rule of a document.
void RunILogicRule(object document, string ruleName) => ILogicAutomation().RunRule((Document)document, ruleName);

// ---- Units ---------------------------------------------------------------------------------------------------------

// Inventor stores lengths in centimeters and angles in radians.
double ToInches(double centimeters) => centimeters / 2.54;
double FromInches(double inches) => inches * 2.54;
double ToMillimeters(double centimeters) => centimeters * 10.0;
double FromMillimeters(double millimeters) => millimeters / 10.0;
// The names before 2.1.0, so a snippet written with them still compiles. The tool description lists only the new ones.
double ToMillimetres(double centimeters) => ToMillimeters(centimeters);
double FromMillimetres(double millimeters) => FromMillimeters(millimeters);
double ToDegrees(double radians) => radians * 180.0 / Math.PI;
double FromDegrees(double degrees) => degrees * Math.PI / 180.0;

// ---- Parameters ----------------------------------------------------------------------------------------------------

// Finds a user parameter by name in a part, assembly or drawing, with no exception when it does not exist.
bool TryGetUserParameter(object document, string name, out UserParameter parameter)
{
	UserParameters userParameters = document switch
	{
		PartDocument part => part.ComponentDefinition.Parameters.UserParameters,
		AssemblyDocument assembly => assembly.ComponentDefinition.Parameters.UserParameters,
		DrawingDocument drawing => drawing.Parameters.UserParameters,
		_ => throw new ArgumentException($"A {((Document)document).DocumentType} has no user parameters.", nameof(document))
	};

	foreach (UserParameter candidate in userParameters)
	{
		if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
		{
			parameter = candidate;
			return true;
		}
	}

	parameter = null!;
	return false;
}

// ---- Transactions --------------------------------------------------------------------------------------------------

// Runs an action as one Undo step on a document, and aborts the step when the action throws.
// Checked in a live Inventor 2025 session: a nested transaction joins the outer one (one step, named after the outer
// one), so no check of CurrentTransaction is needed. Update inside the action stays in the step, and Update after it
// makes a second step. A rule that runs inside, ex. through RunILogicRule, joins the step. A document with no window,
// or one that is not active, gets the step too. Start the step on the document that is written, not on the active one.
// Abort fails when the step is not the current one (ex. the action left a nested step open), so a failure of Abort is
// logged and the first exception is thrown again.
T InTransaction<T>(object document, string name, Func<T> action)
{
	Transaction transaction = Application.TransactionManager.StartTransaction((_Document)document, name);

	try
	{
		T result = action();
		transaction.End();
		return result;
	}
	catch (Exception)
	{
		try { transaction.Abort(); }
		catch (Exception abortProblem) { Log($"Could not abort the transaction '{name}': {abortProblem.Message}"); }

		throw;
	}
}

void InTransaction(object document, string name, Action action) =>
	InTransaction<object?>(document, name, () => { action(); return null; });

// ---- Time guard ----------------------------------------------------------------------------------------------------

// A limit for a loop, so the snippet returns before it holds the main thread too long: if (deadline.Passed) break;
sealed class ScriptDeadline
{
	private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
	private readonly TimeSpan _limit;

	public ScriptDeadline(TimeSpan limit) => _limit = limit;

	public bool Passed => _stopwatch.Elapsed >= _limit;

	public TimeSpan Remaining => _limit - _stopwatch.Elapsed;
}

ScriptDeadline StartDeadline(double seconds = 8) => new(TimeSpan.FromSeconds(seconds));

#nullable restore
