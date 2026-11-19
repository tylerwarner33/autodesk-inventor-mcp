namespace InventorMcp.AddIn.Operations;

/// <summary>
/// 	The names of one open document, read from Inventor.
/// </summary>
/// <param name="DisplayName">
/// 	The name in the user interface, ex. "Bracket.ipt (State2)".
/// </param>
/// <param name="FullFileName">
/// 	The path, or an empty string for a document that was never saved.
/// </param>
/// <param name="FullDocumentName">
/// 	The path with the model state, ex. "C:\Parts\Bracket.ipt&lt;State2&gt;". The path alone for the primary model state,
/// 	and an empty string for a document that was never saved.
/// </param>
/// <param name="IsModelStateFactory">
/// 	True for the document that holds every model state of a file. Inventor edits a model state through it.
/// </param>
internal readonly record struct DocumentNames(string DisplayName, string FullFileName, string FullDocumentName, bool IsModelStateFactory);

/// <summary>
/// 	The result of <see cref="DocumentNameMatch.Find"/>.
/// </summary>
/// <param name="Index">
/// 	The index of the document in the list, or null when no document or more than one document matches.
/// </param>
/// <param name="Candidates">
/// 	For an ambiguous name, the full document name of each match (the display name for a document that was never
/// 	saved). Empty otherwise.
/// </param>
internal sealed record DocumentMatch(int? Index, IReadOnlyList<string> Candidates)
{
	public bool IsAmbiguous => Candidates.Count > 1;
}

/// <summary>
/// 	Finds the open document that a name means.
/// </summary>
/// <remarks>
/// 	Every document whose full document name, path or display name equals the name (no case) matches.
/// 	The matches are grouped by full document name, because a model state that is open has two documents with the same
/// 	full document name: the factory and the member. The factory wins, because Inventor edits a model state through it.
/// 	One group is a match. More than one group (ex. a path whose file has two model states open, or two documents with
/// 	one display name) is ambiguous. See <c>Docs/Architecture.md</c>, "Each tool finds its document the same way".
/// </remarks>
internal static class DocumentNameMatch
{
	/// <summary>
	/// 	Finds the document that a name means.
	/// </summary>
	/// <param name="documents">
	/// 	The names of each open document, in the order of <c>Application.Documents</c>.
	/// </param>
	/// <param name="name">
	/// 	A full document name, a path or a display name. Not empty.
	/// </param>
	/// <returns>
	/// 	The match, no match, or the candidates of an ambiguous name.
	/// </returns>
	public static DocumentMatch Find(IReadOnlyList<DocumentNames> documents, string name)
	{
		List<IGrouping<string, int>> groups =
		[
			.. Enumerable.Range(0, documents.Count)
				.Where(index => Matches(documents[index], name))
				// A document that was never saved has no full document name, so it is a group of its own.
				.GroupBy(index => documents[index].FullDocumentName.Length > 0 ? documents[index].FullDocumentName : $"\u0001{index}", StringComparer.OrdinalIgnoreCase)
		];

		return groups.Count switch
		{
			0 => new DocumentMatch(null, []),
			1 => new DocumentMatch(groups[0].FirstOrDefault(index => documents[index].IsModelStateFactory, groups[0].First()), []),
			_ => new DocumentMatch(null, [.. groups.Select(group => Label(documents[group.First()]))])
		};
	}

	private static bool Matches(DocumentNames document, string name) =>
		Same(document.FullDocumentName, name) || Same(document.FullFileName, name) || Same(document.DisplayName, name);

	private static bool Same(string? value, string name) =>
		value is { Length: > 0 } && string.Equals(value, name, StringComparison.OrdinalIgnoreCase);

	private static string Label(DocumentNames document) =>
		document.FullDocumentName.Length > 0 ? document.FullDocumentName : document.DisplayName;
}
