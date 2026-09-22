using Inventor;

using InventorMcp.AddIn.Bridge;
using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;

namespace InventorMcp.AddIn.Operations;

internal sealed partial class InventorOperations
{
	private SessionInfo GetSession()
	{
		DocumentInfo? active = null;

		if (_inventor.ActiveDocument is Document document)
			active = ToDocumentInfo(document);

		return new SessionInfo(
			_inventor.SoftwareVersion.DisplayName,
			// Major is the internal version, ex. 29 for Inventor 2025.
			_inventor.SoftwareVersion.Major + 1996,
			// Fully qualified: Inventor.Environment is the ribbon environment type, so the name collides.
			System.Environment.ProcessId,
			BridgeProtocol.Version,
			active,
			_inventor.Documents.Count,
			// Ready is false while Inventor is starting, running a command, or showing a modal dialog.
			_inventor.Ready is false);
	}

	private IReadOnlyList<DocumentInfo> GetDocuments()
	{
		Documents documents = _inventor.Documents;
		List<DocumentInfo> result = new(documents.Count);

		for (int index = 1; index <= documents.Count; index++)
			result.Add(ToDocumentInfo((Document)documents[index]));

		return result;
	}

	private AssemblyTree GetAssemblyTree(AssemblyTreeRequest request)
	{
		Document document = ResolveDocument(request.DocumentName);

		if (document is not AssemblyDocument assembly)
		{
			throw new BridgeFailureException(
				BridgeErrorCodes.WrongDocumentType,
				$"'{document.DisplayName}' is a {DescribeDocumentType(document.DocumentType)}. An occurrence tree exists only on an assembly.");
		}

		// A budget rather than a failure: a production assembly can hold tens of thousands of occurrences,
		// and a truncated answer is far more useful to the model than a timeout.
		WalkBudget budget = new(request.MaxNodes);

		List<OccurrenceNode> roots = WalkOccurrences(assembly.ComponentDefinition.Occurrences, request.MaxDepth, budget);

		return new AssemblyTree(ToDocumentInfo(document), roots, budget.Visited, budget.Truncated);
	}

	private static List<OccurrenceNode> WalkOccurrences(ComponentOccurrences occurrences, int remainingDepth, WalkBudget budget)
	{
		List<OccurrenceNode> nodes = [];

		foreach (ComponentOccurrence occurrence in occurrences)
		{
			if (budget.TryVisit() is false)
				return nodes;

			nodes.Add(ToOccurrenceNode(occurrence, remainingDepth, budget));
		}

		return nodes;
	}

	private static OccurrenceNode ToOccurrenceNode(ComponentOccurrence occurrence, int remainingDepth, WalkBudget budget)
	{
		bool suppressed = occurrence.Suppressed;

		List<OccurrenceNode> children = [];

		// A suppressed occurrence has no usable definition, so reading its children would throw.
		if (suppressed is false && remainingDepth > 1 && occurrence.DefinitionDocumentType is DocumentTypeEnum.kAssemblyDocumentObject)
		{
			foreach (ComponentOccurrence child in occurrence.SubOccurrences)
			{
				if (budget.TryVisit() is false)
					break;

				children.Add(ToOccurrenceNode(child, remainingDepth - 1, budget));
			}
		}
		else if (suppressed is false && remainingDepth <= 1 && occurrence.DefinitionDocumentType is DocumentTypeEnum.kAssemblyDocumentObject)
		{
			budget.MarkTruncated();
		}

		return new OccurrenceNode(
			occurrence.Name,
			ReferencedFileOf(occurrence, suppressed),
			DescribeDocumentType(occurrence.DefinitionDocumentType),
			suppressed,
			suppressed is false && occurrence.Visible,
			occurrence.Grounded,
			occurrence.IsPatternElement,
			children);
	}

	private static string ReferencedFileOf(ComponentOccurrence occurrence, bool suppressed)
	{
		if (suppressed)
			return string.Empty;

		try
		{
			return occurrence.Definition.Document is Document document
				? document.FullFileName
				: string.Empty;
		}
		catch
		{
			// A virtual component has no document behind it.
			return string.Empty;
		}
	}

	/// <summary>
	/// 	Tracks how much of the node budget a tree walk has spent.
	/// </summary>
	private sealed class WalkBudget(int maxNodes)
	{
		private readonly int _maxNodes = maxNodes;

		/// <summary>
		/// 	Number of occurrences the walk has recorded.
		/// </summary>
		public int Visited { get; private set; }

		/// <summary>
		/// 	True when a depth or node limit stopped the walk early.
		/// </summary>
		public bool Truncated { get; private set; }

		/// <summary>
		/// 	Reserves budget for one node.
		/// </summary>
		/// <returns>
		/// 	False when the budget is spent.
		/// </returns>
		public bool TryVisit()
		{
			if (Visited >= _maxNodes)
			{
				Truncated = true;
				return false;
			}

			Visited++;
			return true;
		}

		/// <summary>
		/// 	Records that a depth limit hid part of the tree.
		/// </summary>
		public void MarkTruncated()
		{
			Truncated = true;
		}
	}
}
