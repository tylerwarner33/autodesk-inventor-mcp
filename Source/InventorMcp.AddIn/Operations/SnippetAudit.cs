using System.Security.Cryptography;
using System.Text;

namespace InventorMcp.AddIn.Operations;

/// <summary>
/// 	Makes the text of each entry of the executed code audit trail.
/// </summary>
/// <remarks>
/// 	Most calls are the server's own snippets, which are the same text each time, and they hid the snippets that a
/// 	model wrote. So the full code is written one time for each file, and a later entry of the same code holds only
/// 	its SHA-256 and the time of the full entry.
/// 	The add-in decides this from the code that it runs. The caller cannot mark its code as known, so the trail stays
/// 	independent of the caller.
/// </remarks>
internal sealed class SnippetAudit
{
	/// <summary>
	/// 	How many different snippets are remembered. When it is full, the memory starts again.
	/// </summary>
	private const int MaxRemembered = 512;

	private readonly Dictionary<string, DateTimeOffset> _written = new(StringComparer.Ordinal);

	/// <summary>
	/// 	Makes the entry for a snippet that is about to run.
	/// </summary>
	/// <param name="language">
	/// 	"csharp" or "ilogic".
	/// </param>
	/// <param name="documentName">
	/// 	The display name of the target document, or null when there is none.
	/// </param>
	/// <param name="code">
	/// 	The snippet.
	/// </param>
	/// <param name="clientName">
	/// 	The MCP client that sent it, or null.
	/// </param>
	/// <param name="now">
	/// 	The time of the entry.
	/// </param>
	/// <returns>
	/// 	The text to append.
	/// </returns>
	public string Entry(string language, string? documentName, string code, string? clientName, DateTimeOffset now)
	{
		string newLine = Environment.NewLine;
		string hash = Hash(code);
		string body;

		if (_written.TryGetValue(hash, out DateTimeOffset first))
		{
			body = $"(the same code as in the entry of {first:O} with this sha256)";
		}
		else
		{
			if (_written.Count >= MaxRemembered)
				_written.Clear();

			_written[hash] = now;
			body = code;
		}

		return
			$"{new string('=', 80)}{newLine}" +
			$"{now:O}  {language}  document='{OneLine(documentName ?? "(none)")}'  client='{OneLine(clientName ?? "(unknown)")}'  sha256={hash}{newLine}" +
			$"{new string('-', 80)}{newLine}" +
			$"{body}{newLine}";
	}

	/// <summary>
	/// 	Forgets every snippet, so the next entry of each holds its full code.
	/// </summary>
	/// <remarks>
	/// 	Called when a new audit file starts, so each file can be read without the files before it.
	/// </remarks>
	public void Forget() => _written.Clear();

	/// <summary>
	/// 	The SHA-256 of a snippet, as hexadecimal text.
	/// </summary>
	public static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

	/// <summary>
	/// 	Makes a value safe for the header line of an entry.
	/// </summary>
	/// <remarks>
	/// 	A document name and a client name come from outside the add-in.
	/// 	A line break in one could write a line that looks like a new entry.
	/// </remarks>
	public static string OneLine(string value) =>
		string.Concat(value.Select(static character => char.IsControl(character) ? ' ' : character));
}
