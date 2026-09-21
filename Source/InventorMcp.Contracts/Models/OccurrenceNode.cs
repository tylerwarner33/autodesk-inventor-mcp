namespace InventorMcp.Contracts.Models;

/// <summary>
/// 	One node in an assembly occurrence tree.
/// </summary>
/// <param name="Name">
/// 	Occurrence name as shown in the browser, ex. "Bracket:1".
/// </param>
/// <param name="ReferencedFile">
/// 	Full path of the document the occurrence references, or an empty string for a virtual component.
/// </param>
/// <param name="DocumentType">
/// 	Document type of the referenced document.
/// </param>
/// <param name="IsSuppressed">
/// 	True when the occurrence is suppressed.
/// </param>
/// <param name="IsVisible">
/// 	True when the occurrence is visible in the graphics window.
/// </param>
/// <param name="IsGrounded">
/// 	True when the occurrence is grounded.
/// </param>
/// <param name="IsPatternElement">
/// 	True when the occurrence was produced by a pattern rather than placed directly.
/// </param>
/// <param name="Children">
/// 	Sub occurrences. Empty for a part, and empty for a suppressed sub assembly whose children cannot be read.
/// </param>
public sealed record OccurrenceNode(
	string Name,
	string ReferencedFile,
	string DocumentType,
	bool IsSuppressed,
	bool IsVisible,
	bool IsGrounded,
	bool IsPatternElement,
	IReadOnlyList<OccurrenceNode> Children);

/// <summary>
/// 	Result of an assembly tree walk.
/// </summary>
/// <param name="Document">
/// 	The assembly the tree was read from.
/// </param>
/// <param name="Roots">
/// 	Top level occurrences.
/// </param>
/// <param name="TotalNodes">
/// 	Total occurrence count across the whole tree, including nodes omitted by a depth limit.
/// </param>
/// <param name="Truncated">
/// 	True when a depth or node limit stopped the walk before it finished.
/// </param>
public sealed record AssemblyTree(
	DocumentInfo Document,
	IReadOnlyList<OccurrenceNode> Roots,
	int TotalNodes,
	bool Truncated);
