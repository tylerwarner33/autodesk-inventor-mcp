using System.ComponentModel;

using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

internal static partial class InventorTool
{
	[McpServerTool(Name = "inventor_session")]
	[Description("Reports whether an Inventor session is reachable, which version it is, and which document is active. Call this first when you are unsure what Inventor is doing.")]
	public static Task<object> Session(BridgeClient bridge, CancellationToken cancellationToken) =>
		SafeAsync(() => bridge.InvokeAsync<Contracts.Models.SessionInfo>(BridgeOperations.Session, null, cancellationToken));

	[McpServerTool(Name = "inventor_documents")]
	[Description("Lists every document open in the Inventor session, with its full path, type, unsaved state, and whether it needs a rebuild.")]
	public static Task<object> Documents(BridgeClient bridge, CancellationToken cancellationToken) =>
		SafeAsync(() => bridge.InvokeAsync<IReadOnlyList<Contracts.Models.DocumentInfo>>(BridgeOperations.Documents, null, cancellationToken));

	[McpServerTool(Name = "inventor_assembly_tree")]
	[Description("Walks the occurrence tree of an open assembly, reporting suppression, visibility, grounding, and the file each occurrence references. Large assemblies are truncated rather than refused.")]
	public static Task<object> AssemblyTree(
		BridgeClient bridge,
		[Description("Display name or full path of the assembly. Omit to use the active document.")] string? documentName = null,
		[Description("Deepest level to walk. Default 4.")] int maxDepth = 4,
		[Description("Maximum occurrences to return. Default 2000.")] int maxNodes = 2000,
		CancellationToken cancellationToken = default) =>
		SafeAsync(() => bridge.InvokeAsync<Contracts.Models.AssemblyTree>(
			BridgeOperations.AssemblyTree,
			new AssemblyTreeRequest(documentName, maxDepth <= 0 ? 4 : maxDepth, maxNodes <= 0 ? 2000 : maxNodes),
			cancellationToken));

	[McpServerTool(Name = "inventor_activity")]
	[Description("Drains the feed of Inventor events recorded since a cursor: documents opened or saved, and every committed transaction with the command name behind it. Use this to see what an automation loop actually did, in order. Pass the nextSequence from the previous call.")]
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
