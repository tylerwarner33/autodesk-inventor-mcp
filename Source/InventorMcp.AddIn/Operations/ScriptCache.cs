using System.Collections.Immutable;

using Inventor;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;

namespace InventorMcp.AddIn.Operations;

/// <summary>
/// 	Compiles snippets, and keeps the compiled snippets that were used most recently.
/// </summary>
/// <remarks>
/// 	Most tools of the server are snippets with the same text on each call.
/// 	Without this class, Inventor compiled each one again on its main thread, and loaded one more assembly that it
/// 	can never unload.
/// 	A snippet is compiled on the thread of the bridge, not on Inventor's main thread, so Inventor stays responsive
/// 	during the compile. Only the run needs the main thread.
/// 	A compiled snippet that runs again keeps the values of its own <c>static</c> fields from the run before.
/// 	See <c>.agents/rules/addin-isolation.md</c> for why the loader gets the add-in and the interop assembly.
/// </remarks>
internal sealed class ScriptCache
{
	/// <summary>
	/// 	How many compiled snippets stay in the cache.
	/// </summary>
	private const int Capacity = 64;

	/// <summary>
	/// 	Namespaces every snippet gets without asking.
	/// </summary>
	private static readonly string[] _scriptImports =
	[
		"System",
		"System.Collections.Generic",
		"System.Linq",
		"Inventor"
	];

	private readonly Lock _entriesGate = new();
	private readonly Lock _compileGate = new();
	private readonly Dictionary<string, LinkedListNode<(string Code, CompiledScript Script)>> _entries = new(StringComparer.Ordinal);
	private readonly LinkedList<(string Code, CompiledScript Script)> _recent = new();

	// Roslyn is loaded on first use only. A session that runs no snippet never pays for it inside Inventor's process.
	private ScriptOptions? _options;
	private InteractiveAssemblyLoader? _loader;

	/// <summary>
	/// 	Gives the compiled snippet for code, from the cache or from a new compile.
	/// </summary>
	/// <remarks>
	/// 	Never throws, so the caller can write the audit entry and report the failure as a result.
	/// 	Call it from a thread that is not Inventor's main thread.
	/// </remarks>
	/// <param name="code">
	/// 	The snippet.
	/// </param>
	/// <returns>
	/// 	The compiled snippet, or why it did not compile.
	/// </returns>
	public CompiledScript GetOrCompile(string code)
	{
		lock (_entriesGate)
		{
			if (_entries.TryGetValue(code, out LinkedListNode<(string Code, CompiledScript Script)>? found))
			{
				_recent.Remove(found);
				_recent.AddFirst(found);

				return found.Value.Script;
			}
		}

		CompiledScript compiled;

		// One compile at a time, because the compiles share one assembly loader.
		lock (_compileGate)
			compiled = Compile(code);

		// A snippet that did not compile is not kept: the next one is almost always different text.
		if (compiled.Runner is null)
			return compiled;

		lock (_entriesGate)
		{
			if (_entries.ContainsKey(code) is false)
			{
				_entries[code] = _recent.AddFirst((code, compiled));

				if (_recent.Count > Capacity)
				{
					_ = _entries.Remove(_recent.Last!.Value.Code);
					_recent.RemoveLast();
				}
			}
		}

		return compiled;
	}

	private CompiledScript Compile(string code)
	{
		try
		{
			// Roslyn's scripting host owns its own assembly loader and, left alone, loads a second copy of this
			// add-in from the same path into its own context. The globals object then fails to cast, because the
			// two InventorScriptGlobals types have different identities.
			// Registering the already loaded assemblies makes Roslyn reuse them instead, but only as a fallback:
			// Roslyn asks the default context first, so this add-in must never be loaded there.
			// EnterContextualReflection does not help here: the scripting host does not consult it.
			// One loader for the life of the add-in, because a compiled snippet in the cache needs its loader.
			if (_loader is null)
			{
				InteractiveAssemblyLoader loader = new();
				loader.RegisterDependency(typeof(InventorScriptGlobals).Assembly);
				loader.RegisterDependency(typeof(Application).Assembly);
				_loader = loader;
			}

			// Built once. Resolving metadata references is slow and the set never changes.
			_options ??= ScriptOptions.Default
				.AddReferences(
					typeof(Application).Assembly,
					typeof(InventorScriptGlobals).Assembly)
				.AddImports(_scriptImports);

			Script<object> script = CSharpScript.Create<object>(code, _options, typeof(InventorScriptGlobals), _loader);
			ImmutableArray<Diagnostic> diagnostics = script.Compile();

			List<string> messages = [.. diagnostics
				.Where(static diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
				.Select(static diagnostic => diagnostic.ToString())];

			return diagnostics.Any(static diagnostic => diagnostic.Severity is DiagnosticSeverity.Error)
				? new CompiledScript(null, messages, "CompilationError", "The snippet did not compile.")
				: new CompiledScript(script.CreateDelegate(), messages, null, null);
		}
		catch (CompilationErrorException exception)
		{
			return new CompiledScript(
				null,
				[.. exception.Diagnostics.Select(static diagnostic => diagnostic.ToString())],
				nameof(CompilationErrorException),
				exception.Message);
		}
		catch (Exception exception)
		{
			// A failure of the compiler is a result for the caller, the same as a snippet that throws.
			return new CompiledScript(null, [], exception.GetType().FullName, exception.Message);
		}
	}
}

/// <summary>
/// 	A snippet after the compile.
/// </summary>
/// <param name="Runner">
/// 	Runs the snippet with its globals, or null when it did not compile.
/// </param>
/// <param name="Diagnostics">
/// 	The errors and warnings of the compiler.
/// </param>
/// <param name="FailureType">
/// 	The type name of the failure, or null when the snippet compiled.
/// </param>
/// <param name="FailureMessage">
/// 	The message of the failure, or null.
/// </param>
internal sealed record CompiledScript(
	ScriptRunner<object>? Runner,
	IReadOnlyList<string> Diagnostics,
	string? FailureType,
	string? FailureMessage);
