using System.ComponentModel;
using System.Text.Json;

using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

internal static partial class InventorTool
{
	/// <summary>
	/// 	Reads the active project, the iLogic rule switch and the write state of each open document.
	/// </summary>
	/// <remarks>
	/// 	A snippet, so the session operation of the add-in needs no change. A document in a library path, or checked
	/// 	in to Vault, is not modifiable, and a write to it fails with only E_FAIL, so a model needs this to read the
	/// 	failure. RulesEnabled is in the result because <c>suppressRules</c> changes it for the whole session.
	/// </remarks>
	private const string _sessionStateSnippet = """
		string? only = __DOCUMENT__;
		DesignProject project = Application.DesignProjectManager.ActiveDesignProject;
		// A project path can be relative to the folder of the .ipj, ex. .\Designs.
		string projectFolder = System.IO.Path.GetDirectoryName(project.FullFileName) ?? "";
		List<(string Name, string Path)> libraries = new();
		foreach (ProjectPath path in project.LibraryPaths)
			libraries.Add((path.Name, path.Path.Length == 0 ? "" : System.IO.Path.GetFullPath(System.IO.Path.Combine(projectFolder, path.Path))));

		string? LibraryOf(string file) =>
			libraries.Where(library => library.Path.Length > 0 && file.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(library.Path) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				.Select(library => library.Name)
				.FirstOrDefault();

		List<Document> documents = Application.Documents.OfType<Document>()
			.Where(document => only is null
				|| string.Equals(document.FullFileName, only, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(document.DisplayName, only, StringComparison.OrdinalIgnoreCase))
			.ToList();
		// The active document first, so a caller that needs the target reads the first entry.
		if (only is null && Document is Document active)
		{
			documents.Remove(active);
			documents.Insert(0, active);
		}

		bool? rulesEnabled = null;
		try { rulesEnabled = (bool)ILogicAutomation().RulesEnabled; } catch (Exception) { }

		return System.Text.Json.JsonSerializer.Serialize(new
		{
			project = new
			{
				file = project.FullFileName,
				name = project.Name,
				type = project.ProjectType.ToString(),
				workspace = project.WorkspacePath,
				libraryPaths = libraries.Select(library => new { name = library.Name, path = library.Path }),
				contentCenter = project.ContentCenterPath
			},
			iLogicRulesEnabled = rulesEnabled,
			documentCount = Application.Documents.Count,
			documents = documents.Take(__MAX__).Select(document => new
			{
				name = document.DisplayName,
				path = document.FullFileName,
				type = document.DocumentType.ToString(),
				visible = document.Views.Count > 0,
				dirty = document.Dirty,
				isModifiable = document.IsModifiable,
				readOnlyFile = document.FullFileName.Length > 0 && System.IO.File.Exists(document.FullFileName)
					&& new System.IO.FileInfo(document.FullFileName).IsReadOnly,
				library = document.FullFileName.Length > 0 ? LibraryOf(document.FullFileName) : null
			})
		});
		""";

	private const int _maxSessionDocuments = 200;

	/// <summary>
	/// 	Runs <see cref="_sessionStateSnippet"/>, for every document or for one.
	/// </summary>
	private static Task<JsonElement> ReadSessionStateAsync(BridgeClient bridge, string? documentName, CancellationToken cancellationToken) =>
		RunJsonSnippetAsync(
			bridge,
			_sessionStateSnippet
				.Replace("__DOCUMENT__", Services.CSharpLiteral.String(documentName), StringComparison.Ordinal)
				.Replace("__MAX__", _maxSessionDocuments.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
			cancellationToken);

	[McpServerTool(Name = "inventor_session")]
	[Description("""
		Reports whether an Inventor session is reachable, which version it is, and which document is active. Call this
		first when you are unsure what Inventor is doing. If a modal dialog blocks Inventor, it returns blocked-by-dialog
		at once, with the dialog, and sends nothing to Inventor.

		It also gives the active project (.ipj), its workspace and library paths, whether iLogic rules are on, and for
		each open document (up to 200) whether it is modifiable. A write to a document that is not modifiable, ex. one
		in a library path or checked in to Vault, fails with only E_FAIL.
		""")]
	public static Task<object> Session(BridgeClient bridge, CancellationToken cancellationToken) =>
		UnlessBlockedAsync(
			bridge,
			() => SafeAsync(async () =>
			{
				Contracts.Models.SessionInfo session = await bridge.InvokeAsync<Contracts.Models.SessionInfo>(BridgeOperations.Session, null, cancellationToken).ConfigureAwait(false);
				JsonElement state = await ReadSessionStateAsync(bridge, documentName: null, cancellationToken).ConfigureAwait(false);

				return (object)new { session, state };
			}),
			cancellationToken);

	[McpServerTool(Name = "inventor_documents")]
	[Description("Lists every document open in the Inventor session, with its full path, type, unsaved state, and whether it needs a rebuild.")]
	public static Task<object> Documents(BridgeClient bridge, CancellationToken cancellationToken) =>
		SafeAsync(() => bridge.InvokeAsync<IReadOnlyList<Contracts.Models.DocumentInfo>>(BridgeOperations.Documents, null, cancellationToken));

	[McpServerTool(Name = "inventor_assembly_tree")]
	[Description("Walks the occurrence tree of an open assembly, reporting suppression, visibility, grounding, and the file each occurrence references. Large assemblies are truncated rather than refused. Use summary for a large assembly: it gives the count at each depth and the unique documents. A tree over the output limit is returned as the summary, with a message.")]
	public static Task<object> AssemblyTree(
		BridgeClient bridge,
		[Description("Display name or full path of the assembly. Omit to use the active document.")] string? documentName = null,
		[Description("Deepest level to walk. Default 4.")] int maxDepth = 4,
		[Description("Maximum occurrences to return. Default 2000.")] int maxNodes = 2000,
		[Description("Return the counts at each depth and the unique documents, not the tree. Default false.")] bool summary = false,
		CancellationToken cancellationToken = default) =>
		SafeAsync(async () => ShapeTree(
			await bridge.InvokeAsync<Contracts.Models.AssemblyTree>(
				BridgeOperations.AssemblyTree,
				new AssemblyTreeRequest(documentName, maxDepth <= 0 ? 4 : maxDepth, maxNodes <= 0 ? 2000 : maxNodes),
				cancellationToken).ConfigureAwait(false),
			summary));

	/// <summary>
	/// 	The most characters of tree that a result holds before it becomes a summary.
	/// </summary>
	/// <remarks>
	/// 	One tree at depth 3 gave 106,000 characters, over a client's limit for one result.
	/// </remarks>
	private const int _maxTreeCharacters = 60_000;

	private const int _maxSummaryDocuments = 200;

	internal static object ShapeTree(Contracts.Models.AssemblyTree tree, bool summary)
	{
		if (summary)
			return SummarizeTree(tree, message: null);

		int characters = JsonSerializer.Serialize(tree, BridgeProtocol.SerializerOptions).Length;

		return characters <= _maxTreeCharacters
			? tree
			: SummarizeTree(
				tree,
				$"The tree is {characters:N0} characters, over the limit of {_maxTreeCharacters:N0}, so this is the summary. " +
				"Call again with a smaller maxDepth or maxNodes for the tree.");
	}

	private static object SummarizeTree(Contracts.Models.AssemblyTree tree, string? message)
	{
		List<(Contracts.Models.OccurrenceNode Node, int Depth)> nodes = [];
		Stack<(Contracts.Models.OccurrenceNode Node, int Depth)> pending = new(tree.Roots.Reverse().Select(static root => (root, 1)));

		while (pending.TryPop(out (Contracts.Models.OccurrenceNode Node, int Depth) entry))
		{
			nodes.Add(entry);

			foreach (Contracts.Models.OccurrenceNode child in entry.Node.Children.Reverse())
				pending.Push((child, entry.Depth + 1));
		}

		List<object> documents = [.. nodes
			.GroupBy(static entry => entry.Node.ReferencedFile, StringComparer.OrdinalIgnoreCase)
			.OrderByDescending(static group => group.Count())
			.Take(_maxSummaryDocuments)
			.Select(static group => new
			{
				file = group.Key,
				documentType = group.First().Node.DocumentType,
				occurrences = group.Count(),
				suppressed = group.Count(static entry => entry.Node.IsSuppressed)
			})];

		return new
		{
			message,
			document = tree.Document,
			totalNodes = tree.TotalNodes,
			truncated = tree.Truncated,
			countByDepth = nodes.GroupBy(static entry => entry.Depth).OrderBy(static group => group.Key).Select(static group => new { depth = group.Key, occurrences = group.Count() }),
			suppressedOccurrences = nodes.Count(static entry => entry.Node.IsSuppressed),
			uniqueDocuments = nodes.Select(static entry => entry.Node.ReferencedFile).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
			documents,
			documentsLeftOut = Math.Max(0, nodes.Select(static entry => entry.Node.ReferencedFile).Distinct(StringComparer.OrdinalIgnoreCase).Count() - _maxSummaryDocuments)
		};
	}

	[McpServerTool(Name = "inventor_activity")]
	[Description("Reads the feed of Inventor events recorded since a cursor: documents opened or saved, and every committed transaction with the command name behind it. Use this to see what an automation loop actually did, in order. Pass the nextSequence from the previous call. Reading removes nothing, so other clients see the same events.")]
	public static Task<object> Activity(
		BridgeClient bridge,
		[Description("Return events after this sequence number. Pass 0 for everything still buffered.")] long sinceSequence = 0,
		[Description("Maximum events to return. Default 200.")] int maxEntries = 200,
		CancellationToken cancellationToken = default) =>
		SafeAsync(() => bridge.InvokeAsync<Contracts.Models.ActivityFeed>(
			BridgeOperations.Activity,
			new ActivityRequest(sinceSequence < 0 ? 0 : sinceSequence, maxEntries <= 0 ? 200 : maxEntries),
			cancellationToken));
}
