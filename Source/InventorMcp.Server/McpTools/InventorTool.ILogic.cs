using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

/// <remarks>
/// 	The iLogic tools are canned snippets through the execution operation, with the prelude helpers, so they need no
/// 	add-in rebuild. Each snippet returns JSON text, which the server parses.
/// 	See <c>Docs/Research/Usage-Findings-And-Knowledge-Delivery.md</c>, R1.3 and R1.5.
/// </remarks>
internal static partial class InventorTool
{
	/// <summary>
	/// 	The helpers of a tool snippet that works on one document: <c>ToolDocument()</c> and <c>FindOrOpenToolDocument</c>.
	/// </summary>
	/// <remarks>
	/// 	The add-in resolves the documentName of the call, so <c>ToolDocument()</c> is the <c>Document</c> global, the same
	/// 	document as for every other tool. When the name is the path of a file that is not open,
	/// 	<see cref="RunJsonSnippetAsync"/> sends the snippet again with a line in place of <see cref="OpenHere"/> that
	/// 	opens it. A document that it opens is in <c>__documentsOpenedHere</c>, and <c>CloseDocumentsOpenedHere()</c> closes
	/// 	it with no save. See <c>Docs/Architecture.md</c>, "Each tool finds its document the same way".
	/// </remarks>
	private const string ToolDocument = """
		Document? __toolDocument = null;

		// The document of the tool call: the one that the add-in found for documentName, or the file that the call opened.
		Document ToolDocument() => __toolDocument ?? Document ?? throw new InvalidOperationException("No document is active. Give documentName: a display name, a full path, or a full document name.");

		// Finds an open document by name, or opens a file with the rules off, so an open trigger does not run.
		Document FindOrOpenToolDocument(string nameOrPath)
		{
			dynamic? automation = null;
			bool? rulesWereEnabled = null;
			try { automation = ILogicAutomation(); rulesWereEnabled = (bool)automation.RulesEnabled; automation.RulesEnabled = false; }
			catch (Exception) { }

			try { return OpenOrReuseDocument(nameOrPath); }
			finally { if (rulesWereEnabled is bool previous) automation!.RulesEnabled = previous; }
		}

		// open-tool-document-here

		""";

	/// <summary>
	/// 	The line of <see cref="ToolDocument"/> that a second send replaces with the open of the file.
	/// </summary>
	private const string OpenHere = "// open-tool-document-here";

	[McpServerTool(Name = "inventor_ilogic_rules", Title = "List iLogic rules", ReadOnly = true, OpenWorld = false)]
	[Description("Lists the iLogic rules of a document: the name, the active flag and the length of each rule. The document can be open, or a file path, which is opened invisibly and closed again.")]
	public static Task<object> ILogicRulesAsync(
		BridgeClient bridge,
		[Description("A display name, a full path, or a full document name (the path with the model state) of an open document, or the full path of a file. Omit for the active document: during an edit in place, that is the document in edit.")] string? documentName = null,
		CancellationToken cancellationToken = default) =>
		SafeAsync(async () => await RunJsonSnippetAsync(bridge, ToolDocument + """
			Document document = ToolDocument();
			string json = System.Text.Json.JsonSerializer.Serialize(new
			{
				document = document.FullFileName.Length > 0 ? document.FullFileName : document.DisplayName,
				rules = ILogicRuleNames(document).Select(rule => new { name = rule.Name, isActive = rule.IsActive, length = rule.Length })
			});
			CloseDocumentsOpenedHere();
			return json;
			""", cancellationToken, documentName).ConfigureAwait(false));

	// Not ReadOnly: with outputFolder it writes files, and a client can run a read-only tool with no approval.
	[McpServerTool(Name = "inventor_ilogic_rule_get", Title = "Read an iLogic rule", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
	[Description("Reads the text of one iLogic rule, or writes every rule of a document to files in a folder (one '<rule>.iLogicVb' file each). The document can be open, or a file path. An existing file is never overwritten: nothing is written if one of the files exists.")]
	public static Task<object> ILogicRuleGetAsync(
		BridgeClient bridge,
		[Description("A display name, a full path, or a full document name (the path with the model state) of an open document, or the full path of a file. Omit for the active document: during an edit in place, that is the document in edit.")] string? documentName = null,
		[Description("The rule to read. Omit it and give outputFolder to write every rule to files.")] string? ruleName = null,
		[Description("A folder to write every rule to, as '<rule>.iLogicVb' files. It is made if it does not exist.")] string? outputFolder = null,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(ruleName) == string.IsNullOrWhiteSpace(outputFolder))
			return Task.FromResult<object>(new { error = "invalid-arguments", message = "Give ruleName to read one rule, or outputFolder to write every rule to files. Not both." });

		if (outputFolder is not null && Path.IsPathFullyQualified(outputFolder) is false)
			return Task.FromResult<object>(new { error = "invalid-arguments", message = $"'outputFolder' must be a full path, not '{outputFolder}'." });

		return SafeAsync(async () =>
		{
			JsonElement rules = await RunJsonSnippetAsync(bridge, ToolDocument + $$"""
				Document document = ToolDocument();
				string? onlyRule = {{CSharpLiteral.String(ruleName)}};
				List<object> rules = new();
				foreach ((string Name, bool IsActive, int Length) rule in ILogicRuleNames(document))
				{
					if (onlyRule is null || string.Equals(rule.Name, onlyRule, StringComparison.Ordinal))
						rules.Add(new { name = rule.Name, isActive = rule.IsActive, text = ILogicRuleText(document, rule.Name) });
				}
				string json = System.Text.Json.JsonSerializer.Serialize(new { document = document.FullFileName.Length > 0 ? document.FullFileName : document.DisplayName, rules });
				CloseDocumentsOpenedHere();
				return json;
				""", cancellationToken, documentName).ConfigureAwait(false);

			if (rules.ValueKind is not JsonValueKind.Object)
				return (object)rules;

			JsonElement list = rules.GetProperty("rules");

			if (ruleName is not null)
			{
				return list.GetArrayLength() == 0
					? new { error = BridgeErrorCodes.NotFound, message = $"The document has no rule named '{ruleName}'. Call inventor_ilogic_rules for the names." }
					: list[0];
			}

			List<(string File, string? Text)> planned = [.. list.EnumerateArray().Select(rule => (
				File: Path.Combine(outputFolder!, SafeFileName(rule.GetProperty("name").GetString()!) + ".iLogicVb"),
				Text: rule.GetProperty("text").GetString()))];

			string[] existing = [.. planned.Select(static item => item.File).Where(File.Exists)];
			string[] duplicates = [.. planned.GroupBy(static item => item.File, StringComparer.OrdinalIgnoreCase).Where(static group => group.Count() > 1).Select(static group => group.Key)];

			if (existing.Length > 0 || duplicates.Length > 0)
				return new { error = "target-exists", message = "Some rule files exist already, or two rules have the same file name, so nothing was written. Give a new folder.", existing, duplicates };

			_ = Directory.CreateDirectory(outputFolder!);
			List<string> files = [];

			foreach ((string file, string? text) in planned)
			{
				// CreateNew: a file made after the check above is not overwritten either.
				await using FileStream stream = new(file, FileMode.CreateNew, FileAccess.Write);
				await using StreamWriter writer = new(stream);
				await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
				files.Add(file);
			}

			return new { document = rules.GetProperty("document").GetString(), files };
		});
	}

	[McpServerTool(Name = "inventor_ilogic_rule_set", Title = "Change an iLogic rule", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
	[Description("""
		Changes the text of one iLogic rule at an anchor: the anchor text must occur exactly once in the rule, or nothing
		changes. Line ends are made the same as the rule's. The old text is written to a backup file first, and the
		result has the backup path and a diff of the change.

		If iLogic detects the changed rule to be potentially unsafe (ex. it reads the registry), its next run shows an
		iLogic Security Alert. The server answers it as the user's dialog settings say, and lists the click in
		blockingDialogs. If the call returns blocked-by-dialog, never answer it for the user: 'Don't run the rule'
		disables the rule.

		Read the 'ilogic-rule-edit' skill (inventor_skill) before the first change: it has the steps before and after.
		""")]
	public static Task<object> ILogicRuleSetAsync(
		BridgeClient bridge,
		[Description("The rule to change.")] string ruleName,
		[Description("The new text: it replaces the anchor, or goes before or after it.")] string text,
		[Description("Text that occurs exactly once in the rule. Omit only with mode 'replace-all' or for a new rule.")] string? anchor = null,
		[Description("'replace' (default): replace the anchor. 'insert-before' or 'insert-after': add text next to the anchor. 'replace-all': replace the whole rule, with no anchor.")] string mode = "replace",
		[Description("A display name, a full path, or a full document name (the path with the model state) of an open document, or the full path of a file. Omit for the active document: during an edit in place, that is the document in edit.")] string? documentName = null,
		[Description("Add the rule when it does not exist, with text as its whole text. Default false.")] bool createIfMissing = false,
		[Description("Save the document after the change. Required when documentName is a file that is not open, or the change is lost when the tool closes it.")] bool save = false,
		CancellationToken cancellationToken = default) =>
		SafeAsync(async () =>
		{
			if (mode is not ("replace" or "insert-before" or "insert-after" or "replace-all"))
				return new { error = "invalid-arguments", message = $"Unknown mode '{mode}'. Use replace, insert-before, insert-after or replace-all." };

			JsonElement current = await RunJsonSnippetAsync(bridge, ToolDocument + $$"""
				Document document = ToolDocument();
				string json = System.Text.Json.JsonSerializer.Serialize(new
				{
					document = document.FullFileName.Length > 0 ? document.FullFileName : document.DisplayName,
					isOpenAlready = !__documentsOpenedHere.Contains(document),
					text = ILogicRuleText(document, {{CSharpLiteral.String(ruleName)}})
				});
				CloseDocumentsOpenedHere();
				return json;
				""", cancellationToken, documentName).ConfigureAwait(false);

			if (current.ValueKind is not JsonValueKind.Object)
				return (object)current;

			string documentPath = current.GetProperty("document").GetString()!;
			string? oldText = current.GetProperty("text").GetString();

			if (current.GetProperty("isOpenAlready").GetBoolean() is false && save is false)
				return new { error = "invalid-arguments", message = $"'{documentPath}' is not open, so the change would be lost when the tool closes it. Pass save=true, or open the document first." };

			string newText;

			if (oldText is null)
			{
				if (createIfMissing is false)
					return new { error = BridgeErrorCodes.NotFound, message = $"The document has no rule named '{ruleName}'. Pass createIfMissing=true to add it." };

				newText = text;
			}
			else if (RuleEdit.TryApply(oldText, text, anchor, mode, out string edited, out string? problem))
			{
				newText = edited;
			}
			else
			{
				return new { error = "anchor-not-unique", message = problem };
			}

			string? backup = oldText is null ? null : RuleEdit.WriteBackup(documentPath, ruleName, oldText);

			JsonElement written = await RunJsonSnippetAsync(bridge, ToolDocument + $$"""
				Document document = ToolDocument();
				string? expected = {{CSharpLiteral.String(oldText)}};
				string? now = ILogicRuleText(document, {{CSharpLiteral.String(ruleName)}});
				string status;
				if (now != expected)
				{
					status = "changed-since-read";
				}
				else
				{
					SetILogicRuleText(document, {{CSharpLiteral.String(ruleName)}}, {{CSharpLiteral.String(newText)}});
					if ({{(save ? "true" : "false")}})
						document.Save();
					status = "written";
				}
				CloseDocumentsOpenedHere();
				return System.Text.Json.JsonSerializer.Serialize(new { status });
				""", cancellationToken, documentName, allowOpen: save).ConfigureAwait(false);

			if (written.ValueKind is not JsonValueKind.Object)
				return (object)written;

			if (written.GetProperty("status").GetString() == "changed-since-read")
				return new { error = "rule-changed", message = "The rule changed between the read and the write, so nothing was written. Read it again.", backup };

			return new
			{
				status = "written",
				document = documentPath,
				rule = ruleName,
				created = oldText is null,
				saved = save,
				backup,
				diff = RuleEdit.Diff(oldText ?? string.Empty, newText)
			};
		});

	[McpServerTool(Name = "inventor_set_parameters", Title = "Set several parameters", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
	[Description("""
		Sets several parameters in one call, then updates the document once. This changes the user's model. A numeric
		parameter takes an expression (ex. '50 mm'), a text parameter plain text, and a boolean parameter 'true' or
		'false'.

		With suppressRules, iLogic rules do not run for each change: the rules are turned off for the batch and turned
		back on in a finally block, even when a write fails. runRuleAfter then runs one rule once. RulesEnabled is a
		setting of the whole Inventor session, not of one document. One Undo step in Inventor reverts the call.


		The result has one entry in results for each parameter, and the counts requested, succeeded and failed. When no
		parameter was set, the result has error 'no-parameter-set'. When some were set, send only the failed ones again.
		""")]
	public static Task<object> SetParametersAsync(
		BridgeClient bridge,
		[Description("Parameter names and their new values, ex. {\"Width\": \"50 mm\", \"Finish\": \"Painted\"}.")] Dictionary<string, string> parameters,
		[Description("A display name, a full path, or a full document name (the path with the model state) of an open document, or the full path of a file. Omit for the active document: during an edit in place, that is the document in edit.")] string? documentName = null,
		[Description("Turn iLogic rules off during the writes. Default false.")] bool suppressRules = false,
		[Description("A rule to run once after the writes and the update, ex. the top level rule.")] string? runRuleAfter = null,
		[Description("Add a user parameter that does not exist, from its expression. Numeric only. Default false.")] bool createIfMissing = false,
		[Description("Set true to write to a document with unsaved changes.")] bool allowUnsavedChanges = false,
		CancellationToken cancellationToken = default)
	{
		if (parameters is null || parameters.Count == 0)
			return Task.FromResult<object>(new { error = "invalid-arguments", message = "Give at least one parameter." });

		return SafeAsync(async () => await RunJsonSnippetAsync(
			bridge,
			ComposeSetParametersSnippet(parameters, suppressRules, runRuleAfter, createIfMissing, allowUnsavedChanges),
			cancellationToken,
			documentName,
			allowOpen: false).ConfigureAwait(false));
	}

	/// <summary>
	/// 	Sent with <c>rulesNotRun</c> after a write with <c>suppressRules</c>: the active rules of the document, apart from
	/// 	<c>runRuleAfter</c>.
	/// </summary>
	/// <remarks>
	/// 	Without it, a model that set parameters with suppressRules and no runRuleAfter reported "no rule on this part"
	/// 	while the part had a rule that never ran on the new values. See <c>Docs/Architecture.md</c>, "suppressRules says
	/// 	which rules did not run".
	/// </remarks>
	internal const string RulesNotRunNote =
		"suppressRules turned the iLogic rules off for this write, so these active rules of the document did not run on " +
		"the new values. A rule can set other parameters or properties from them. Run the rule that the user's workflow " +
		"needs (inventor_run_ilogic with ruleName), or tell the user that the rules did not run.";

	/// <summary>
	/// 	Composes the snippet of <see cref="SetParameters"/>, also used by <c>inventor_set_parameter</c> with suppressRules.
	/// </summary>
	/// <remarks>
	/// 	The document is the documentName of the call, which the add-in resolves. Send it with allowOpen false.
	/// </remarks>
	internal static string ComposeSetParametersSnippet(
		IReadOnlyDictionary<string, string> parameters,
		bool suppressRules,
		string? runRuleAfter,
		bool createIfMissing,
		bool allowUnsavedChanges)
	{
		StringBuilder entries = new();

		foreach ((string name, string value) in parameters)
			_ = entries.Append(CultureInfo.InvariantCulture, $"\t({CSharpLiteral.String(name)}, {CSharpLiteral.String(value)}),\n");

		return ToolDocument + $$"""
			Document document = ToolDocument();
			if (!{{(allowUnsavedChanges ? "true" : "false")}} && document.Dirty)
				throw new InvalidOperationException($"'{document.DisplayName}' has unsaved changes. Save it, or pass allowUnsavedChanges.");

			Parameters all = document switch
			{
				PartDocument part => part.ComponentDefinition.Parameters,
				AssemblyDocument assembly => assembly.ComponentDefinition.Parameters,
				DrawingDocument drawing => drawing.Parameters,
				_ => throw new ArgumentException($"A {document.DocumentType} has no parameters.")
			};

			(string Name, string Value)[] writes = new (string Name, string Value)[]
			{
			{{entries}}};

			dynamic? automation = {{(suppressRules || runRuleAfter is not null ? "ILogicAutomation()" : "null")}};
			bool? rulesWereEnabled = null;
			List<object> results = new();
			string? ruleError = null;
			string? ruleAfter = {{CSharpLiteral.String(runRuleAfter)}};
			long writeMilliseconds = 0;
			int succeededCount = 0;
			int failedCount = 0;
			System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

			// The same facts that inventor_parameters gives for a parameter, read after the write.
			object Describe(Parameter parameter, bool created)
			{
				string units = parameter.get_Units();
				UnitsTypeEnum kind = document.UnitsOfMeasure.GetTypeFromString(units);
				string valueKind = kind == UnitsTypeEnum.kTextUnits ? "Text" : kind == UnitsTypeEnum.kBooleanUnits ? "Boolean" : "Numeric";
				string expression = (parameter.Expression ?? string.Empty).Trim();
				double? internalValue = null;
				string displayValue = expression;
				if (valueKind == "Text")
				{
					expression = expression.Trim('"').Trim();
					displayValue = expression;
				}
				else if (valueKind == "Numeric")
				{
					try
					{
						internalValue = parameter._Value;
						displayValue = document.UnitsOfMeasure.GetStringFromValue(internalValue.Value, units);
					}
					catch (Exception) { valueKind = "Unknown"; }
				}

				return new { name = parameter.Name, ok = true, created, valueKind, expression, internalValue, displayValue, units };
			}

			// The value is written when this runs, so a failure to read it back is not a failed write.
			object DescribeWritten(Parameter parameter, bool created)
			{
				try { return Describe(parameter, created); }
				catch (Exception exception) { return new { name = parameter.Name, ok = true, created, readBackError = exception.Message }; }
			}

			if ({{(suppressRules ? "true" : "false")}})
			{
				rulesWereEnabled = (bool)automation!.RulesEnabled;
				automation.RulesEnabled = false;
			}

			try
			{
				// One Undo step for the call. A failed entry is recorded and the batch goes on, so one Undo reverts what was
				// written. An exception outside the loop (ex. from Update) aborts the step, and the document goes back.
				InTransaction(document, "MCP: Set Parameters", () =>
				{
					foreach ((string name, string value) in writes)
					{
						try
						{
							Parameter? parameter = null;
							foreach (Parameter candidate in all)
							{
								if (candidate.Name == name) { parameter = candidate; break; }
							}

							if (parameter is null)
							{
								if (!{{(createIfMissing ? "true" : "false")}})
								{
									failedCount++;
									results.Add(new { name, ok = false, code = "not-found", error = "No parameter has this name. Pass createIfMissing to add a user parameter." });
									continue;
								}

								UserParameter created = all.UserParameters.AddByExpression(name, value, UnitsTypeEnum.kDefaultDisplayLengthUnits);
								succeededCount++;
								results.Add(DescribeWritten((Parameter)created, created: true));
								continue;
							}

							string units = parameter.get_Units();
							UnitsTypeEnum kind = document.UnitsOfMeasure.GetTypeFromString(units);
							if (kind == UnitsTypeEnum.kTextUnits)
							{
								parameter.Value = value;
							}
							else if (kind == UnitsTypeEnum.kBooleanUnits)
							{
								if (!bool.TryParse(value, out bool flag))
								{
									failedCount++;
									results.Add(new { name, ok = false, code = "invalid-expression", error = $"'{name}' is a boolean parameter, so it needs 'true' or 'false' rather than '{value}'." });
									continue;
								}

								parameter.Value = flag;
							}
							else
							{
								// Ask Inventor to parse the expression first, so a bad one reports itself and not a COM error.
								if (!document.UnitsOfMeasure.IsExpressionValid(value, units))
								{
									failedCount++;
									results.Add(new { name, ok = false, code = "invalid-expression", error = $"Inventor cannot parse '{value}' as a value in '{units}'. State the units explicitly, ex. '50 mm'." });
									continue;
								}

								parameter.Expression = value;
							}

							succeededCount++;
							results.Add(DescribeWritten(parameter, created: false));
						}
						catch (Exception exception)
						{
							failedCount++;
							results.Add(new { name, ok = false, code = "set-failed", error = exception.Message });
						}
					}

					document.Update();
					writeMilliseconds = stopwatch.ElapsedMilliseconds;

					// Inside the step, so one Undo reverts what the rule changed too.
					if (ruleAfter is not null)
					{
						try { automation!.RunRule(document, ruleAfter); document.Update(); }
						catch (Exception exception) { ruleError = exception.Message; }
					}
				});
			}
			finally
			{
				if (rulesWereEnabled is bool previous)
					automation!.RulesEnabled = previous;
			}

			// The rules that would have run on these values and did not, so a model does not report the work as done.
			List<string>? rulesNotRun = null;
			if (rulesWereEnabled == true && succeededCount > 0)
			{
				try { rulesNotRun = ILogicRuleNames(document).Where(rule => rule.IsActive && rule.Name != ruleAfter).Select(rule => rule.Name).ToList(); }
				catch (Exception) { }
			}

			Dictionary<string, object?> summary = new()
			{
				["document"] = document.FullFileName.Length > 0 ? document.FullFileName : document.DisplayName,
				["transaction"] = "MCP: Set Parameters",
				["requested"] = writes.Length,
				["succeeded"] = succeededCount,
				["failed"] = failedCount,
				["results"] = results,
				["rulesSuppressed"] = rulesWereEnabled is not null,
				["rulesEnabledNow"] = automation is null ? (bool?)null : (bool)automation.RulesEnabled,
				["writeMilliseconds"] = writeMilliseconds,
				["ruleRunAfter"] = ruleAfter,
				["ruleError"] = ruleError,
				["totalMilliseconds"] = stopwatch.ElapsedMilliseconds
			};

			if (rulesNotRun is { Count: > 0 })
			{
				summary["rulesNotRun"] = rulesNotRun;
				summary["rulesNote"] = {{CSharpLiteral.String(RulesNotRunNote)}};
			}

			// The server marks the call as failed only when no item succeeded: a flag on a partial result would
			// invite a repeat of the whole batch, and a parameter write is not idempotent.
			if (succeededCount == 0 && failedCount > 0)
			{
				summary["error"] = "no-parameter-set";
				summary["message"] = "No parameter was set. Read results for the reason of each item.";
			}

			return System.Text.Json.JsonSerializer.Serialize(summary);
			""";
	}

	/// <summary>
	/// 	Runs a composed snippet and parses the JSON text it returns.
	/// </summary>
	/// <param name="bridge">
	/// 	The bridge.
	/// </param>
	/// <param name="code">
	/// 	The snippet. A snippet for one document starts with <see cref="ToolDocument"/> and calls <c>ToolDocument()</c>.
	/// </param>
	/// <param name="cancellationToken">
	/// 	Cancels the call.
	/// </param>
	/// <param name="documentName">
	/// 	The documentName of the tool. The add-in resolves it, and the snippet gets it as <c>Document</c>.
	/// </param>
	/// <param name="allowOpen">
	/// 	True to open the file when <paramref name="documentName"/> is the path of a file that is not open. A write passes
	/// 	false, because a change to a document that the call opens is lost when it closes.
	/// </param>
	/// <returns>
	/// 	The parsed result, or the execution result when the snippet failed.
	/// </returns>
	private static async Task<JsonElement> RunJsonSnippetAsync(BridgeClient bridge, string code, CancellationToken cancellationToken, string? documentName = null, bool allowOpen = true)
	{
		// A canned snippet that throws must still close what it opened. None of them declares a type, which a block
		// cannot hold. A second close does nothing, because the helper forgets each document it closes.
		if (code.Contains("ToolDocument(", StringComparison.Ordinal) || code.Contains("OpenOrReuseDocument(", StringComparison.Ordinal))
			code = $"try\n{{\n{code}\n}}\nfinally\n{{\n\tCloseDocumentsOpenedHere();\n}}";

		ExecutionResult result;

		try
		{
			// The snippets guard unsaved changes themselves where they write, and a read needs no guard.
			result = await bridge.InvokeAsync<ExecutionResult>(
				BridgeOperations.EvalCSharp,
				new ExecuteRequest(ScriptPrelude.Apply(code), documentName, AllowUnsavedChanges: true),
				cancellationToken).ConfigureAwait(false);
		}
		catch (InventorBridgeException exception) when (exception.Code == BridgeErrorCodes.NotFound
			&& documentName is not null
			&& Path.IsPathFullyQualified(documentName)
			&& File.Exists(documentName))
		{
			if (allowOpen is false || code.Contains(OpenHere, StringComparison.Ordinal) is false)
			{
				throw new InventorBridgeException(
					BridgeErrorCodes.NotFound,
					$"'{documentName}' is not open. Open it first, ex. with inventor_eval_csharp and OpenOrReuseDocument(path, true).");
			}

			result = await bridge.InvokeAsync<ExecutionResult>(
				BridgeOperations.EvalCSharp,
				new ExecuteRequest(ScriptPrelude.Apply(code.Replace(OpenHere, $"__toolDocument = FindOrOpenToolDocument({CSharpLiteral.String(documentName)});", StringComparison.Ordinal)), DocumentName: null, AllowUnsavedChanges: true),
				cancellationToken).ConfigureAwait(false);
		}

		if (result.Succeeded && result.ReturnValue is string json)
			return JsonElement.Parse(json);

		return JsonSerializer.SerializeToElement(new
		{
			error = BridgeErrorCodes.ExecutionFailed,
			message = result.ExceptionMessage,
			exceptionType = result.ExceptionType,
			diagnostics = result.Diagnostics,
			output = result.Output
		});
	}

	private static string SafeFileName(string name) =>
		string.Concat(name.Select(static character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
