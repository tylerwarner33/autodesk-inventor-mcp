using System.Text.Json;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	Compares the styles of two documents, as the <c>inventor_styles</c> snippet reads them.
/// </summary>
/// <remarks>
/// 	A style is the same style in both documents when its type and name are the same.
/// </remarks>
internal static class StyleDiff
{
	/// <summary>
	/// 	The style fields that are compared, in the order of the result.
	/// </summary>
	private static readonly string[] _comparedFields = ["location", "upToDate", "inUse"];

	/// <param name="first">
	/// 	The styles of the first document: <c>document</c>, <c>standard</c> and <c>styles</c>.
	/// </param>
	/// <param name="second">
	/// 	The styles of the second document, in the same shape.
	/// </param>
	/// <returns>
	/// 	The styles in only one document, and the differences in the styles that both have.
	/// </returns>
	public static object Compare(JsonElement first, JsonElement second)
	{
		Dictionary<string, JsonElement> a = Index(first);
		Dictionary<string, JsonElement> b = Index(second);
		List<object> changed = [];

		foreach ((string key, JsonElement left) in a)
		{
			if (b.TryGetValue(key, out JsonElement right) is false)
				continue;

			List<object> differences = [];

			foreach (string field in _comparedFields)
			{
				string leftValue = Text(left, field);
				string rightValue = Text(right, field);

				if (leftValue != rightValue)
					differences.Add(new { field, first = leftValue, second = rightValue });
			}

			Dictionary<string, string> leftDetails = Details(left);
			Dictionary<string, string> rightDetails = Details(right);

			foreach (string field in leftDetails.Keys.Union(rightDetails.Keys).Order(StringComparer.Ordinal))
			{
				string? leftValue = leftDetails.GetValueOrDefault(field);
				string? rightValue = rightDetails.GetValueOrDefault(field);

				if (leftValue != rightValue)
					differences.Add(new { field, first = leftValue, second = rightValue });
			}

			if (differences.Count > 0)
				changed.Add(new { type = Text(left, "type"), name = Text(left, "name"), differences });
		}

		return new
		{
			first = Text(first, "document"),
			second = Text(second, "document"),
			standard = Text(first, "standard") == Text(second, "standard")
				? (object)Text(first, "standard")
				: new { first = Text(first, "standard"), second = Text(second, "standard") },
			onlyInFirst = a.Keys.Except(b.Keys).Select(key => Label(a[key])).Order(StringComparer.Ordinal).ToList(),
			onlyInSecond = b.Keys.Except(a.Keys).Select(key => Label(b[key])).Order(StringComparer.Ordinal).ToList(),
			changed,
			same = a.Keys.Intersect(b.Keys).Count() - changed.Count
		};
	}

	private static Dictionary<string, JsonElement> Index(JsonElement document)
	{
		Dictionary<string, JsonElement> styles = new(StringComparer.Ordinal);

		foreach (JsonElement style in document.GetProperty("styles").EnumerateArray())
			styles.TryAdd(Label(style), style);

		return styles;
	}

	private static string Label(JsonElement style) => $"{Text(style, "type")}: {Text(style, "name")}";

	private static Dictionary<string, string> Details(JsonElement style) =>
		style.TryGetProperty("details", out JsonElement details) && details.ValueKind is JsonValueKind.Object
			? details.EnumerateObject().ToDictionary(static property => property.Name, static property => property.Value.ToString(), StringComparer.Ordinal)
			: [];

	private static string Text(JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value) && value.ValueKind is not JsonValueKind.Null ? value.ToString() : "";
}
