using System.Globalization;
using System.Text;
using System.Text.Json;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	Turns tool arguments into C# literals for a snippet the server composes.
/// </summary>
/// <remarks>
/// 	A composed snippet is compiled inside Inventor, so a value pasted into it unescaped is code, not data.
/// 	Every value from a tool argument goes through here instead.
/// </remarks>
internal static class CSharpLiteral
{
	/// <summary>
	/// 	A C# string literal for a value, or <c>null</c>.
	/// </summary>
	/// <param name="value">
	/// 	The text to quote.
	/// </param>
	/// <returns>
	/// 	A regular (not verbatim) string literal with every special character escaped.
	/// </returns>
	public static string String(string? value)
	{
		if (value is null)
			return "null";

		StringBuilder builder = new(value.Length + 2);
		builder.Append('"');

		foreach (char character in value)
		{
			switch (character)
			{
				case '\\': builder.Append(@"\\"); break;
				case '"': builder.Append("\\\""); break;
				case '\r': builder.Append(@"\r"); break;
				case '\n': builder.Append(@"\n"); break;
				case '\t': builder.Append(@"\t"); break;
				default:
					if (char.IsControl(character) || char.IsSurrogate(character) || character > '~')
						builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}");
					else
						builder.Append(character);
					break;
			}
		}

		builder.Append('"');
		return builder.ToString();
	}

	/// <summary>
	/// 	A C# literal for a JSON scalar: a string, number, boolean or null.
	/// </summary>
	/// <remarks>
	/// 	An integer becomes a <c>long</c> and any other number a <c>double</c>. The snippet converts either to the
	/// 	parameter's real type when it binds, so the literal only has to carry the value exactly.
	/// </remarks>
	/// <param name="value">
	/// 	The JSON value.
	/// </param>
	/// <returns>
	/// 	The literal.
	/// </returns>
	/// <exception cref="ArgumentException">
	/// 	The value is an object or an array, which has no single literal form.
	/// </exception>
	public static string FromJson(JsonElement value) =>
		value.ValueKind switch
		{
			JsonValueKind.String => String(value.GetString()),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			JsonValueKind.Null or JsonValueKind.Undefined => "null",
			JsonValueKind.Number when value.TryGetInt64(out long integer) => $"{integer.ToString(CultureInfo.InvariantCulture)}L",
			JsonValueKind.Number => $"{value.GetDouble().ToString("R", CultureInfo.InvariantCulture)}d",
			_ => throw new ArgumentException($"Only strings, numbers, booleans and null can be passed, not a JSON {value.ValueKind}."),
		};
}
