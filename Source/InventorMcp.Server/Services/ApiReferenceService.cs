using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

using Microsoft.Extensions.Logging;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	Searches Autodesk's own documentation for the Inventor interop assembly.
/// </summary>
/// <remarks>
/// 	The source is the vendored Autodesk.Inventor.Interop.xml, which ships beside the interop assembly.
/// 	It is roughly 12 MB, far more than fits in a model's context, so it is indexed once and queried on demand.
/// 	One file ships per supported release, because a member added in a later release does not exist in an
/// 	earlier one, and a confident answer about a member that is not there is the worst result this tool can give.
/// 	Nothing here talks to Inventor, so lookups work with Inventor closed.
/// </remarks>
internal sealed partial class ApiReferenceService(ILogger<ApiReferenceService> logger)
{
	private readonly ILogger<ApiReferenceService> _logger = logger;
	private readonly ConcurrentDictionary<int, List<ApiMember>> _indexes = new();

	/// <summary>
	/// 	Finds documented API members matching a query.
	/// </summary>
	/// <param name="query">
	/// 	A type name, a member name, or "Type.Member". Matching is case insensitive.
	/// </param>
	/// <param name="kind">
	/// 	Restrict to "type", "method", "property", "event", or "field". Null returns every kind.
	/// </param>
	/// <param name="maxResults">
	/// 	Cap on the number of members returned.
	/// </param>
	/// <param name="releaseYear">
	/// 	Inventor release to answer for, ex. 2025. Null falls back to the newest documentation that shipped.
	/// </param>
	/// <returns>
	/// 	The matches, best first.
	/// </returns>
	public IReadOnlyList<ApiMember> Search(string query, string? kind, int maxResults, int? releaseYear)
	{
		List<ApiMember> members = EnsureIndex(ResolveReleaseYear(releaseYear));

		if (string.IsNullOrWhiteSpace(query))
			return [];

		string needle = query.Trim();

		IEnumerable<ApiMember> candidates = members;

		if (string.IsNullOrWhiteSpace(kind) is false)
			candidates = candidates.Where(member => string.Equals(member.Kind, kind, StringComparison.OrdinalIgnoreCase));

		// Ranked rather than filtered: an exact name is almost always what was meant,
		// but a substring match is what makes the tool useful when the exact name is not known.
		return [.. candidates
			.Select(member => (Member: member, Rank: RankMatch(member, needle)))
			.Where(static scored => scored.Rank > 0)
			.OrderByDescending(static scored => scored.Rank)
			.ThenBy(static scored => scored.Member.FullName.Length)
			.Take(maxResults)
			.Select(static scored => scored.Member)];
	}

	/// <summary>
	/// 	Finds the members of one type whose names are near a name that does not exist.
	/// </summary>
	/// <remarks>
	/// 	For a compile error such as CS1061, where the model guessed a member name (ex. <c>Balloon.RangeBox</c>).
	/// </remarks>
	/// <param name="typeName">
	/// 	The type, with or without the Inventor namespace.
	/// </param>
	/// <param name="memberName">
	/// 	The name that does not exist.
	/// </param>
	/// <param name="maxResults">
	/// 	Cap on the number of members returned.
	/// </param>
	/// <param name="releaseYear">
	/// 	Inventor release to answer for, or null for the newest.
	/// </param>
	/// <returns>
	/// 	The near members of the type, nearest first, the members with exactly that name on other types, with the
	/// 	type name nearest to <paramref name="typeName"/> first, and how many types have that name.
	/// </returns>
	public (IReadOnlyList<ApiMember> NearMembers, IReadOnlyList<ApiMember> SameNameElsewhere, int SameNameCount) FindNear(
		string typeName,
		string memberName,
		int maxResults,
		int? releaseYear)
	{
		List<ApiMember> members = EnsureIndex(ResolveReleaseYear(releaseYear));
		string declaringType = typeName.StartsWith("Inventor.", StringComparison.Ordinal) ? typeName : "Inventor." + typeName;

		List<ApiMember> nearMembers = [.. members
			.Where(member => string.Equals(member.DeclaringType, declaringType, StringComparison.Ordinal))
			.GroupBy(static member => member.Name, StringComparer.Ordinal)
			.Select(static group => group.First())
			.Select(member => (Member: member, Distance: NameDistance(member.Name, memberName)))
			.Where(scored => scored.Distance <= Math.Max(3, memberName.Length / 2)
				|| scored.Member.Name.Contains(memberName, StringComparison.OrdinalIgnoreCase)
				|| memberName.Contains(scored.Member.Name, StringComparison.OrdinalIgnoreCase))
			.OrderBy(static scored => scored.Distance)
			.Take(maxResults)
			.Select(static scored => scored.Member)];

		// Proxy types repeat every member of their base, so they only add noise here.
		List<ApiMember> sameName = [.. members
			.Where(member => member.Kind is not "type"
				&& string.Equals(member.Name, memberName, StringComparison.Ordinal)
				&& string.Equals(member.DeclaringType, declaringType, StringComparison.Ordinal) is false
				&& member.DeclaringType.EndsWith("Proxy", StringComparison.Ordinal) is false)
			.GroupBy(static member => member.QualifiedName, StringComparer.Ordinal)
			.Select(static group => group.First())];

		string shortType = declaringType["Inventor.".Length..];

		List<ApiMember> sameNameElsewhere = [.. sameName
			.OrderBy(member => NameDistance(member.DeclaringType["Inventor.".Length..], shortType))
			.Take(maxResults)];

		return (nearMembers, sameNameElsewhere, sameName.Count);
	}

	/// <summary>
	/// 	The edit distance of two names, ignoring case.
	/// </summary>
	private static int NameDistance(string first, string second)
	{
		string a = first.ToUpperInvariant();
		string b = second.ToUpperInvariant();
		int[] previous = [.. Enumerable.Range(0, b.Length + 1)];
		int[] current = new int[b.Length + 1];

		for (int i = 1; i <= a.Length; i++)
		{
			current[0] = i;

			for (int j = 1; j <= b.Length; j++)
			{
				int substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
				current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
			}

			(previous, current) = (current, previous);
		}

		return previous[b.Length];
	}

	private static int RankMatch(ApiMember member, string needle)
	{
		if (string.Equals(member.QualifiedName, needle, StringComparison.OrdinalIgnoreCase))
			return 100;

		if (string.Equals(member.Name, needle, StringComparison.OrdinalIgnoreCase))
			return 80;

		if (member.QualifiedName.Contains(needle, StringComparison.OrdinalIgnoreCase))
			return 50;

		if (member.Summary.Contains(needle, StringComparison.OrdinalIgnoreCase))
			return 10;

		return 0;
	}

	/// <summary>
	/// 	Picks the documentation to answer from: the connected release, or the newest that shipped.
	/// </summary>
	/// <param name="releaseYear">
	/// 	Release the caller asked for, or null when no session has been reached.
	/// </param>
	/// <returns>
	/// 	The release whose documentation will be read.
	/// </returns>
	private int ResolveReleaseYear(int? releaseYear)
	{
		string directory = Path.Combine(AppContext.BaseDirectory, "InventorApi");

		if (releaseYear is int requested && File.Exists(Path.Combine(directory, $"{requested}.xml")))
			return requested;

		int[] available = Directory.Exists(directory)
			? [.. Directory.EnumerateFiles(directory, "*.xml")
				.Select(static file => int.TryParse(Path.GetFileNameWithoutExtension(file), out int year) ? year : 0)
				.Where(static year => year > 0)
				.OrderDescending()]
			: [];

		if (available.Length is 0)
			return releaseYear ?? 0;

		if (releaseYear is int missing)
		{
			_logger.LogWarning(
				"No API reference shipped for Inventor {Requested}. Answering from {Fallback} instead.",
				missing,
				available[0]);
		}

		return available[0];
	}

	private List<ApiMember> EnsureIndex(int releaseYear)
	{
		return _indexes.GetOrAdd(releaseYear, BuildIndex);
	}

	private List<ApiMember> BuildIndex(int releaseYear)
	{
		string path = Path.Combine(AppContext.BaseDirectory, "InventorApi", $"{releaseYear}.xml");

		if (File.Exists(path) is false)
		{
			_logger.LogWarning("The Inventor API reference was not found at {Path}. Lookups will return nothing.", path);
			return [];
		}

		List<ApiMember> members = [];

		try
		{
			// Streamed rather than loaded as a document: the file is large and only two elements matter.
			XmlReaderSettings settings = new() { IgnoreComments = true, IgnoreWhitespace = true };
			using XmlReader reader = XmlReader.Create(path, settings);

			string? currentName = null;
			StringBuilder summary = new();
			bool inSummary = false;

			while (reader.Read())
			{
				if (reader.NodeType is XmlNodeType.Element && reader.Name is "member")
				{
					currentName = reader.GetAttribute("name");
					_ = summary.Clear();
					continue;
				}

				if (currentName is null)
					continue;

				switch (reader.NodeType)
				{
					case XmlNodeType.Element when reader.Name is "summary":
						inSummary = true;
						break;

					case XmlNodeType.EndElement when reader.Name is "summary":
						inSummary = false;
						break;

					case XmlNodeType.Text or XmlNodeType.CDATA when inSummary:
						_ = summary.Append(reader.Value);
						break;

					case XmlNodeType.EndElement when reader.Name is "member":
						if (TryCreate(currentName, summary.ToString(), out ApiMember member))
							members.Add(member);

						currentName = null;
						break;
				}
			}

			_logger.LogInformation("Indexed {Count} Inventor API members from {Path}.", members.Count, path);
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Could not index the Inventor API reference at {Path}.", path);
		}

		return members;
	}

	private static bool TryCreate(string rawName, string summary, out ApiMember member)
	{
		member = default!;

		// Names look like "T:Inventor.Application" or "M:Inventor.ExtrudeFeatures.Add(Inventor.Profile)".
		if (rawName.Length < 3 || rawName[1] is not ':')
			return false;

		string kind = rawName[0] switch
		{
			'T' => "type",
			'M' => "method",
			'P' => "property",
			'E' => "event",
			'F' => "field",
			_ => string.Empty
		};

		if (kind.Length is 0)
			return false;

		string fullName = rawName[2..];

		// Drop the parameter list for naming; it is kept in FullName for the caller to read.
		int parenthesis = fullName.IndexOf('(');
		string withoutParameters = parenthesis < 0 ? fullName : fullName[..parenthesis];

		int lastDot = withoutParameters.LastIndexOf('.');
		string name = lastDot < 0 ? withoutParameters : withoutParameters[(lastDot + 1)..];

		string declaringType = kind is "type" || lastDot < 0
			? string.Empty
			: withoutParameters[..lastDot];

		// "Inventor." prefixes every type and only adds noise to a match.
		string qualifiedName = withoutParameters.StartsWith("Inventor.", StringComparison.Ordinal)
			? withoutParameters["Inventor.".Length..]
			: withoutParameters;

		member = new ApiMember(
			kind,
			name,
			qualifiedName,
			fullName,
			declaringType,
			CollapseWhitespace(summary).Trim());

		return true;
	}

	private static string CollapseWhitespace(string value) => WhitespaceRegex().Replace(value, " ");

	[GeneratedRegex(@"\s+")]
	private static partial Regex WhitespaceRegex();
}

/// <summary>
/// 	One documented member of the Inventor API.
/// </summary>
/// <param name="Kind">
/// 	"type", "method", "property", "event", or "field".
/// </param>
/// <param name="Name">
/// 	The member's own name, ex. "AddByDistanceExtent".
/// </param>
/// <param name="QualifiedName">
/// 	Type and member without the Inventor namespace, ex. "ExtrudeFeatures.AddByDistanceExtent".
/// </param>
/// <param name="FullName">
/// 	The full documented name including any parameter list.
/// </param>
/// <param name="DeclaringType">
/// 	The type the member belongs to, or an empty string for a type.
/// </param>
/// <param name="Summary">
/// 	Autodesk's own description, with whitespace collapsed.
/// </param>
public sealed record ApiMember(
	string Kind,
	string Name,
	string QualifiedName,
	string FullName,
	string DeclaringType,
	string Summary);
