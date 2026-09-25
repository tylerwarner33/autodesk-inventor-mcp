using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

/// <remarks>
/// 	Tools for documents and files. The Inventor calls are canned snippets through the execution operation, and the
/// 	test copy runs Apprentice in a child process, so none of them needs an add-in rebuild.
/// 	See <c>Docs/Tasks/Usage-Findings-Implementation-Plan.md</c>, Phase 4.
/// </remarks>
internal static partial class InventorTool
{
	[McpServerTool(Name = "inventor_close_documents", Destructive = true)]
	[Description("""
		Closes the open documents under a folder, with no save: drawings first, then presentations, assemblies and parts,
		the same order as inventor_run_plugin. A document outside the folder is never closed.

		It refuses when a document under the folder has unsaved changes, and lists them, unless allowUnsavedChanges is
		set. Those changes are then lost. A hidden document that an open document outside the folder references stays
		open, and the result names it.
		""")]
	public static Task<object> CloseDocuments(
		BridgeClient bridge,
		[Description("The full path of a folder. Every open document in it, or in a folder below it, is closed.")] string under,
		[Description("Set true to close documents with unsaved changes. The changes are lost.")] bool allowUnsavedChanges = false,
		CancellationToken cancellationToken = default)
	{
		if (Path.IsPathFullyQualified(under) is false)
			return Task.FromResult<object>(new { error = "invalid-arguments", message = $"'under' must be the full path of a folder, not '{under}'." });

		string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(under)) + Path.DirectorySeparatorChar;

		return SafeAsync(async () => await RunJsonSnippetAsync(bridge, $$"""
			string folder = {{CSharpLiteral.String(folder)}};
			bool Under(Document document) => document.FullFileName.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
			bool IsOpen(Document document) => Application.Documents.OfType<Document>().Any(open => ReferenceEquals(open, document));

			// Read every fact before the first close: a member of a closed document throws E_FAIL.
			var targets = Application.Documents.OfType<Document>().Where(Under)
				.Select(document => (Document: document, Name: document.FullFileName, Type: document.DocumentType, Visible: document.Views.Count > 0, Dirty: document.Dirty))
				.ToList();
			List<string> dirty = targets.Where(target => target.Dirty).Select(target => target.Name).ToList();
			if (dirty.Count > 0 && !{{(allowUnsavedChanges ? "true" : "false")}})
				return System.Text.Json.JsonSerializer.Serialize(new { error = "unsaved-changes", message = "These documents have unsaved changes, so nothing was closed. Save them, or pass allowUnsavedChanges.", dirty });

			List<string> closed = new();
			List<object> leftOpen = new();
			// Visible documents first: closing one also closes the hidden documents that only it referenced.
			foreach (bool visible in new[] { true, false })
			{
				foreach (DocumentTypeEnum type in new[] { DocumentTypeEnum.kDrawingDocumentObject, DocumentTypeEnum.kPresentationDocumentObject, DocumentTypeEnum.kAssemblyDocumentObject, DocumentTypeEnum.kPartDocumentObject })
				{
					foreach (var (document, name, _, _, _) in targets.Where(target => target.Type == type && target.Visible == visible))
					{
						if (!IsOpen(document))
						{
							closed.Add(name);
							continue;
						}

						List<string> users = document.ReferencingDocuments.OfType<Document>().Where(user => !Under(user)).Select(user => user.FullFileName).ToList();
						if (users.Count > 0)
						{
							leftOpen.Add(new { document = name, referencedBy = users });
							continue;
						}

						try { document.Close(SkipSave: true); closed.Add(name); }
						catch (System.Runtime.InteropServices.COMException exception) { leftOpen.Add(new { document = name, error = exception.Message }); }
					}
				}
			}

			return System.Text.Json.JsonSerializer.Serialize(new
			{
				folder,
				closed = closed.Distinct(StringComparer.OrdinalIgnoreCase),
				leftOpen,
				stillOpenUnder = Application.Documents.OfType<Document>().Count(Under),
				openDocuments = Application.Documents.Count
			});
			""", cancellationToken).ConfigureAwait(false));
	}

	/// <summary>
	/// 	The iProperties that <c>inventor_file_info</c> reads when the caller names none.
	/// </summary>
	private static readonly string[] _defaultFileProperties =
		["Part Number", "Description", "Revision Number", "Stock Number", "Title", "Project", "Designer", "Vendor", "Material"];

	[McpServerTool(Name = "inventor_file_info", ReadOnly = true)]
	[Description("""
		Reads facts about many files in one call: the release that saved each one, its model state names, work point
		and iMate names, and iProperties for each model state (the common Design Tracking and Summary properties, and
		every custom property). Opens a file that is not open invisibly, with iLogic rules off so no open trigger
		runs, and closes it again with no save.

		Pages over the paths: a call stops before about 8 s, and nextOffset says where the next call starts.
		""")]
	public static Task<object> FileInfo(
		BridgeClient bridge,
		[Description("Full paths of the files, or display names of open documents.")] string[] paths,
		[Description("The first path to read, from 0. Pass nextOffset from the previous call.")] int offset = 0,
		[Description("The most paths to read in this call. Default 20.")] int limit = 20,
		[Description("iProperty names to read in place of the default set. Custom properties are always read.")] string[]? propertyNames = null,
		CancellationToken cancellationToken = default)
	{
		if (paths is null || paths.Length == 0)
			return Task.FromResult<object>(new { error = "invalid-arguments", message = "Give at least one path." });

		int first = Math.Clamp(offset, 0, paths.Length);
		string[] page = [.. paths.Skip(first).Take(limit <= 0 ? 20 : limit)];
		string names = string.Join(", ", (propertyNames is { Length: > 0 } ? propertyNames : _defaultFileProperties).Select(CSharpLiteral.String));
		string files = string.Join(", ", page.Select(CSharpLiteral.String));

		return SafeAsync(async () =>
		{
			JsonElement result = await RunJsonSnippetAsync(bridge, _findToolDocument + $$"""
				string[] files = new string[] { {{files}} };
				HashSet<string> wanted = new(new string[] { {{names}} }, StringComparer.OrdinalIgnoreCase);
				ScriptDeadline deadline = StartDeadline(8);
				dynamic automation = ILogicAutomation();
				bool rulesWereEnabled = (bool)automation.RulesEnabled;
				List<object> results = new();
				int read = 0;

				Dictionary<string, object?> Properties(Document document)
				{
					Dictionary<string, object?> values = new();
					foreach (PropertySet set in document.PropertySets)
					{
						bool custom = set.InternalName == "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}";
						foreach (Property property in set)
						{
							if (!custom && !wanted.Contains(property.Name))
								continue;

							object? value;
							try { value = property.Value; } catch (Exception) { continue; }
							values[custom ? $"custom:{property.Name}" : property.Name] = value is string or bool or double or int or DateTime ? value : value?.ToString();
						}
					}

					return values;
				}

				automation.RulesEnabled = false;
				try
				{
					foreach (string file in files)
					{
						if (read > 0 && deadline.Passed)
							break;

						read++;
						try
						{
							Document document = FindToolDocument(file);
							// The interop types do not convert to the ComponentDefinition base, so each member is read per type.
							(ModelStates? states, WorkPoints? workPoints, iMateDefinitions? iMates) = document switch
							{
								PartDocument part => (part.ComponentDefinition.ModelStates, part.ComponentDefinition.WorkPoints, part.ComponentDefinition.iMateDefinitions),
								AssemblyDocument assembly => (assembly.ComponentDefinition.ModelStates, assembly.ComponentDefinition.WorkPoints, assembly.ComponentDefinition.iMateDefinitions),
								_ => ((ModelStates?)null, (WorkPoints?)null, (iMateDefinitions?)null)
							};

							List<object> modelStates = new();
							if (states is not null)
							{
								foreach (ModelState state in states)
								{
									Dictionary<string, object?>? values = null;
									string? problem = null;
									// ModelState.Document is null for the active model state, which is the document itself.
									try { values = (state.Name == document.ModelStateName ? document : state.Document as Document) is Document member ? Properties(member) : null; }
									catch (Exception exception) { problem = exception.Message; }
									modelStates.Add(new { name = state.Name, type = state.ModelStateType.ToString(), properties = values, error = problem });
								}
							}

							results.Add(new
							{
								file,
								document = document.FullFileName,
								type = document.DocumentType.ToString(),
								savedBy = document.SoftwareVersionSaved.DisplayVersion,
								activeModelState = document.ModelStateName,
								properties = states is null ? Properties(document) : null,
								modelStates,
								// ToList: a lazy query would run at the serialization, after the documents are closed.
								workPoints = workPoints?.OfType<WorkPoint>().Select(point => point.Name).ToList(),
								iMates = iMates?.OfType<iMateDefinition>().Select(iMate => iMate.Name).ToList()
							});
						}
						catch (Exception exception)
						{
							results.Add(new { file, error = exception.Message });
						}
					}
				}
				finally
				{
					automation.RulesEnabled = rulesWereEnabled;
					CloseDocumentsOpenedHere();
				}

				return System.Text.Json.JsonSerializer.Serialize(new { read, results });
				""", cancellationToken).ConfigureAwait(false);

			if (result.TryGetProperty("read", out JsonElement read) is false)
				return (object)result;

			int next = first + read.GetInt32();

			return new
			{
				results = result.GetProperty("results"),
				offset = first,
				nextOffset = next < paths.Length ? next : (int?)null,
				total = paths.Length
			};
		});
	}

	/// <summary>
	/// 	The longest a test copy may run before the child process is stopped.
	/// </summary>
	private static readonly TimeSpan _testCopyTimeout = TimeSpan.FromMinutes(10);

	[McpServerTool(Name = "inventor_test_copy", Destructive = true)]
	[Description("""
		Copies a document tree to a new folder for a test, so a test never changes the masters. Every file of the tree
		under the source folder (also in folders below it, and also suppressed components) is copied, the read-only
		attribute is cleared, and the references between the copies are pointed at the copies. References to files
		outside the source folder (ex. library paths and the Content Center) stay as they are.

		It uses Inventor Apprentice in a separate process, so no iLogic rule runs, and Inventor can be closed. The
		source files are not changed, and it refuses a target that has any of the copies already. An iLogic rule that
		names a file in its text still names the master: check the rules of the copy.

		Do not check the copy into Vault. Delete it when the test is done.
		""")]
	public static Task<object> TestCopy(
		[Description("Full paths of the top files, ex. the top assembly and its drawing.")] string[] sources,
		[Description("The full path of the target folder.")] string target,
		[Description("The folder to copy from. Default: the folder of the first source.")] string? sourceFolder = null,
		[Description("A prefix for the name of each copy, ex. 'TEST_'. Required when the target is inside the source folder.")] string? prefix = null,
		CancellationToken cancellationToken = default) =>
		SafeAsync(async () =>
		{
			if (sources is null || sources.Length == 0 || sources.Any(static source => Path.IsPathFullyQualified(source) is false || File.Exists(source) is false))
				return (object)new { error = "invalid-arguments", message = "Give the full path of at least one existing file in sources." };

			if (Path.IsPathFullyQualified(target) is false)
				return new { error = "invalid-arguments", message = $"'target' must be a full path, not '{target}'." };

			string folder = sourceFolder ?? Path.GetDirectoryName(sources[0])!;
			string requestFile = Path.Combine(Path.GetTempPath(), $"InventorMcp.TestCopy.{Guid.NewGuid():N}.json");
			string scriptFile = Path.ChangeExtension(requestFile, ".ps1");

			try
			{
				await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(new { sources, target, sourceFolder = folder, prefix }), cancellationToken).ConfigureAwait(false);
				await File.WriteAllTextAsync(scriptFile, ReadEmbeddedText("InventorMcp.Server.TestCopy.ps1"), Encoding.UTF8, cancellationToken).ConfigureAwait(false);

				(int exitCode, string output, string error) = await RunPowerShellAsync(scriptFile, requestFile, cancellationToken).ConfigureAwait(false);
				string? json = output.Split('\n').Select(static line => line.Trim()).LastOrDefault(static line => line.StartsWith('{'));

				return json is null
					? new { error = "copy-failed", message = $"The copy process ended with code {exitCode} and no result.", output, detail = error }
					: JsonDocument.Parse(json).RootElement.Clone();
			}
			finally
			{
				File.Delete(requestFile);
				File.Delete(scriptFile);
			}
		});

	private static async Task<(int ExitCode, string Output, string Error)> RunPowerShellAsync(string scriptFile, string requestFile, CancellationToken cancellationToken)
	{
		// Windows PowerShell, not pwsh: it is on every Windows machine, and Apprentice is a 64-bit in-process COM server.
		ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8
		};

		foreach (string argument in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptFile, "-RequestFile", requestFile])
			start.ArgumentList.Add(argument);

		using Process process = Process.Start(start) ?? throw new InvalidOperationException("Windows PowerShell did not start.");
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_testCopyTimeout);

		Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
		Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);

		try
		{
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			process.Kill(entireProcessTree: true);
			throw;
		}

		return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
	}

	private static string ReadEmbeddedText(string name)
	{
		using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
			?? throw new InvalidOperationException($"The resource '{name}' is not in the server assembly.");
		using StreamReader reader = new(stream);

		return reader.ReadToEnd();
	}
}
