using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

internal static partial class InventorTool
{
	/// <summary>
	/// 	Loads a plugin's build output into a fresh collectible context and calls one of its methods.
	/// </summary>
	/// <remarks>
	/// 	Composed on top of the execution operation rather than added as a bridge operation, so it needs no add-in
	/// 	rebuild. The mechanism, and why each step is there, is documented in <c>Docs/Plugin-Development-Loop.md</c>.
	///
	/// 	The tokens in capitals are replaced with C# literals built by <see cref="CSharpLiteral" />, never with raw
	/// 	argument text.
	/// </remarks>
	private const string _runPluginSnippet = """
		#nullable enable

		using System.Globalization;
		using System.Reflection;
		using System.Runtime.Loader;

		// Resolves from the shadow copy first, so a dependency another add-in put in the default context never wins.
		// The interop is the one exception: it must unify with Inventor's copy, so InventorServer is one type.
		class ShadowLoadContext(string mainAssemblyPath) : AssemblyLoadContext("inventor-mcp-plugin-loop", isCollectible: true)
		{
			private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

			protected override Assembly? Load(AssemblyName assemblyName)
			{
				if (assemblyName.Name == "Autodesk.Inventor.Interop")
					return null;

				string? path = _resolver.ResolveAssemblyToPath(assemblyName);
				return path is null ? null : LoadFromAssemblyPath(path);
			}
		}

		static void CopyDirectory(string source, string target)
		{
			System.IO.Directory.CreateDirectory(target);
			foreach (string file in System.IO.Directory.GetFiles(source))
				System.IO.File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)));
			foreach (string directory in System.IO.Directory.GetDirectories(source))
				CopyDirectory(directory, System.IO.Path.Combine(target, System.IO.Path.GetFileName(directory)));
		}

		// Drawings reference assemblies and assemblies reference parts, so close in that order.
		// List again before each close: closing one document can close others, and Close on a closed one throws.
		void CloseDocumentsUnder(string folder)
		{
			foreach (DocumentTypeEnum type in new[] { DocumentTypeEnum.kDrawingDocumentObject, DocumentTypeEnum.kAssemblyDocumentObject, DocumentTypeEnum.kPartDocumentObject, DocumentTypeEnum.kPresentationDocumentObject })
			{
				while (Application.Documents.OfType<Document>().FirstOrDefault(document => document.DocumentType == type && document.FullFileName.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) is Document document)
				{
					try { document.Close(SkipSave: true); }
					catch (System.Runtime.InteropServices.COMException exception) { Log($"Could not close '{document.DisplayName}': {exception.Message}"); break; }
				}
			}
		}

		// A rolling log is given by its base path, ex. Serilog's 'log-.txt', which it writes as 'log-20260922.txt' and
		// 'log-20260922_001.txt', so every file the name expands to is watched. A matching file the call does not
		// write adds nothing, because only what the call added is read.
		static string[] LogFiles(string path)
		{
			string? directory = System.IO.Path.GetDirectoryName(path);
			if (directory is null || System.IO.Directory.Exists(directory) is false)
				return [];

			string pattern = System.IO.Path.GetFileNameWithoutExtension(path) + "*" + System.IO.Path.GetExtension(path);
			return [.. System.IO.Directory.GetFiles(directory, pattern).Order(StringComparer.OrdinalIgnoreCase)];
		}

		// Shared, because a logger in another add-in or context can still hold the file open.
		static System.IO.FileStream OpenShared(string file) =>
			new(file, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);

		// The length and the last bytes before the call. The same bytes at the same place afterwards mean the call
		// appended. Anything else means it rewrote the file, ex. a plugin that overwrites its log on every run.
		static (long Length, byte[] Tail) MarkLogFile(string file)
		{
			using System.IO.FileStream stream = OpenShared(file);
			byte[] tail = new byte[(int)Math.Min(stream.Length, 256)];
			stream.Seek(stream.Length - tail.Length, System.IO.SeekOrigin.Begin);
			stream.ReadExactly(tail);
			return (stream.Length, tail);
		}

		static (List<string> Lines, bool Rewritten) ReadAddedLines(string file, long lengthBefore, byte[] tailBefore)
		{
			using System.IO.FileStream stream = OpenShared(file);
			bool appended = stream.Length >= lengthBefore;
			if (appended && tailBefore.Length > 0)
			{
				byte[] tail = new byte[tailBefore.Length];
				stream.Seek(lengthBefore - tail.Length, System.IO.SeekOrigin.Begin);
				stream.ReadExactly(tail);
				appended = tail.AsSpan().SequenceEqual(tailBefore);
			}

			stream.Seek(appended ? lengthBefore : 0, System.IO.SeekOrigin.Begin);
			using System.IO.StreamReader reader = new(stream);
			List<string> lines = [];
			while (reader.ReadLine() is string line)
				lines.Add(line);

			return (lines, appended is false);
		}

		string buildOutputDirectory = __BUILD_OUTPUT__;
		string assemblyFileName = __ASSEMBLY_FILE__;
		string typeName = __TYPE_NAME__;
		string methodName = __METHOD_NAME__;
		string? closeDocumentsUnder = __CLOSE_UNDER__;
		bool keepDocumentsOpen = __KEEP_OPEN__;
		string? logFilePath = __LOG_FILE__;
		int logTailLines = __LOG_TAIL__;
		string[] warningMarkers = [__WARNING_MARKERS__];
		string[] errorMarkers = [__ERROR_MARKERS__];
		string? logPattern = __LOG_PATTERN__;
		int logMatchLines = __LOG_MATCH_LINES__;
		Dictionary<string, object?> arguments = new(StringComparer.Ordinal)
		{
		__ARGUMENTS__
		};
		Dictionary<string, string> shadowFiles = new(StringComparer.Ordinal)
		{
		__SHADOW_FILES__
		};

		// Matched with a trailing separator, so "C:\Cases\A" never matches a document in "C:\Cases\A2".
		if (closeDocumentsUnder is not null)
			closeDocumentsUnder += System.IO.Path.DirectorySeparatorChar;

		string assemblyPath = System.IO.Path.Combine(buildOutputDirectory, assemblyFileName);
		if (System.IO.File.Exists(assemblyPath) is false)
			throw new System.IO.FileNotFoundException($"No '{assemblyFileName}' in '{buildOutputDirectory}'. Build the project first.");

		// Refuse to discard work the user has open under the folder the plugin writes to.
		if (closeDocumentsUnder is not null)
		{
			List<Document> dirty = [.. Application.Documents.OfType<Document>().Where(document => document.Dirty && document.FullFileName.StartsWith(closeDocumentsUnder, StringComparison.OrdinalIgnoreCase))];
			if (dirty.Count > 0)
				throw new InvalidOperationException($"Refusing to run: these documents under '{closeDocumentsUnder}' have unsaved changes: {string.Join(", ", dirty.Select(document => document.DisplayName))}. Save or close them first.");

			CloseDocumentsUnder(closeDocumentsUnder);
		}

		// One folder per call, so the build output is never locked. Copies older than a day are removed, best effort:
		// a copy an earlier context still holds open is left for next time.
		string shadowRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "InventorMcp", "plugin-loop", System.IO.Path.GetFileNameWithoutExtension(assemblyFileName));
		foreach (string old in System.IO.Directory.Exists(shadowRoot) ? System.IO.Directory.GetDirectories(shadowRoot) : [])
		{
			try
			{
				if (System.IO.Directory.GetCreationTimeUtc(old) < DateTime.UtcNow.AddDays(-1))
					System.IO.Directory.Delete(old, recursive: true);
			}
			catch (Exception) { }
		}

		string shadow = System.IO.Path.Combine(shadowRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
		CopyDirectory(buildOutputDirectory, shadow);

		foreach ((string relativePath, string content) in shadowFiles)
		{
			string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(shadow, relativePath));
			if (target.StartsWith(shadow + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) is false)
				throw new ArgumentException($"Shadow file '{relativePath}' is outside the shadow copy.");

			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
			System.IO.File.WriteAllText(target, content);
			Log($"Wrote '{relativePath}' into the shadow copy.");
		}

		ShadowLoadContext context = new(System.IO.Path.Combine(shadow, assemblyFileName));
		InventorServer server = (InventorServer)Application;

		// Named binding: InventorServer and Application are filled in, every other parameter comes from 'arguments' by
		// name or from its default. Position never matters, so two bool parameters cannot be swapped by mistake.
		bool TryBind(ParameterInfo[] parameters, bool useArguments, out object?[] values, out string problem)
		{
			values = new object?[parameters.Length];
			problem = "";

			for (int index = 0; index < parameters.Length; index++)
			{
				ParameterInfo parameter = parameters[index];
				Type type = parameter.ParameterType;

				// Matched by name, not by Type identity: a plugin built with embedded interop types carries its own
				// copy of each interface, which COM treats as the same type but .NET does not.
				if (type.FullName == "Inventor.InventorServer")
					values[index] = server;
				else if (type.FullName == "Inventor.Application")
					values[index] = Application;
				else if (useArguments && arguments.TryGetValue(parameter.Name!, out object? value))
				{
					try
					{
						Type target = Nullable.GetUnderlyingType(type) ?? type;
						values[index] = value is null ? null
							: target.IsInstanceOfType(value) ? value
							: target.IsEnum && value is string text ? Enum.Parse(target, text, ignoreCase: true)
							: Convert.ChangeType(value, target, CultureInfo.InvariantCulture);

						if (values[index] is null && type.IsValueType && Nullable.GetUnderlyingType(type) is null)
						{
							problem = $"'{parameter.Name}' is a {type.Name} and cannot be null";
							return false;
						}
					}
					catch (Exception exception)
					{
						problem = $"'{parameter.Name}' cannot take {value} as a {type.Name}: {exception.Message}";
						return false;
					}
				}
				else if (parameter.HasDefaultValue)
					values[index] = parameter.DefaultValue;
				else
				{
					problem = $"'{parameter.Name}' ({type.Name}) has no argument and no default";
					return false;
				}
			}

			return true;
		}

		string Signature(MethodBase method) =>
			$"{method.Name}({string.Join(", ", method.GetParameters().Select(parameter => $"{parameter.ParameterType.Name} {parameter.Name}{(parameter.HasDefaultValue ? " = " + (parameter.DefaultValue ?? "null") : "")}"))})";

		Dictionary<string, (long Length, byte[] Tail)> logMarks = new(StringComparer.OrdinalIgnoreCase);
		if (logFilePath is not null)
		{
			try
			{
				foreach (string file in LogFiles(logFilePath))
					logMarks[file] = MarkLogFile(file);
			}
			catch (Exception exception)
			{
				Log($"Could not mark log '{logFilePath}' before the call, so its whole content is read afterwards: {exception.Message}");
			}
		}

		System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
		try
		{
			Assembly assembly = context.LoadFromAssemblyPath(System.IO.Path.Combine(shadow, assemblyFileName));
			Type type = assembly.GetType(typeName, throwOnError: false)
				?? throw new InvalidOperationException($"No type '{typeName}' in '{assemblyFileName}'. Public types: {string.Join(", ", assembly.GetExportedTypes().Select(exported => exported.FullName).Take(40))}");

			List<MethodInfo> candidates = [.. type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Where(method => method.Name == methodName)];
			if (candidates.Count == 0)
				throw new InvalidOperationException($"'{typeName}' has no public method '{methodName}'.");

			List<(MethodInfo Method, object?[] Values)> bindable = [];
			List<string> rejections = [];
			foreach (MethodInfo candidate in candidates)
			{
				ParameterInfo[] parameters = candidate.GetParameters();
				string[] unknown = [.. arguments.Keys.Where(name => parameters.All(parameter => parameter.Name != name))];

				if (unknown.Length > 0)
					rejections.Add($"{Signature(candidate)}: no parameter named {string.Join(", ", unknown)}");
				else if (TryBind(parameters, useArguments: true, out object?[] values, out string problem))
					bindable.Add((candidate, values));
				else
					rejections.Add($"{Signature(candidate)}: {problem}");
			}

			if (bindable.Count != 1)
				throw new InvalidOperationException(bindable.Count == 0
					? $"No '{methodName}' overload matches the arguments. {string.Join(" | ", rejections)}"
					: $"More than one '{methodName}' overload matches: {string.Join(" | ", bindable.Select(item => Signature(item.Method)))}. Pass arguments that tell them apart.");

			(MethodInfo method, object?[] methodValues) = bindable[0];

			object? instance = null;
			if (method.IsStatic is false)
			{
				// Constructors are bound without the named arguments, which belong to the method.
				ConstructorInfo? constructor = null;
				object?[] constructorValues = [];
				foreach (ConstructorInfo candidate in type.GetConstructors())
				{
					if (TryBind(candidate.GetParameters(), useArguments: false, out object?[] values, out _))
					{
						constructor = candidate;
						constructorValues = values;
						break;
					}
				}

				instance = (constructor ?? throw new InvalidOperationException($"'{typeName}' has no public constructor that takes only an InventorServer, an Application or defaults.")).Invoke(constructorValues);
			}

			Log($"Calling {typeName}.{Signature(method)}");
			object? result = method.Invoke(instance, methodValues);
			Log($"Returned {(result is null ? "nothing" : $"{result.GetType().Name}: {result}")} after {stopwatch.Elapsed.TotalSeconds:F1} s.");
		}
		catch (TargetInvocationException exception) when (exception.InnerException is not null)
		{
			Log($"Threw after {stopwatch.Elapsed.TotalSeconds:F1} s.");
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
		}
		finally
		{
			context.Unload();

			// Only the lines this call wrote, so the counts describe this run and not every run a shared log holds.
			// A failure here is reported, never thrown, so it cannot replace the plugin's own exception.
			if (logFilePath is not null)
			{
				try
				{
					List<string> lines = [];
					List<string> sources = [];
					foreach (string file in LogFiles(logFilePath))
					{
						logMarks.TryGetValue(file, out (long Length, byte[] Tail) mark);
						(List<string> added, bool rewritten) = ReadAddedLines(file, mark.Length, mark.Tail ?? []);
						if (added.Count == 0 && rewritten is false)
							continue;

						lines.AddRange(added);
						sources.Add($"'{System.IO.Path.GetFileName(file)}' {added.Count}{(rewritten ? ", rewritten" : "")}");
					}

					if (sources.Count == 0)
						Log($"Log '{logFilePath}': nothing written during the call.");
					else
					{
						int warnings = lines.Count(line => warningMarkers.Any(marker => line.Contains(marker, StringComparison.Ordinal)));
						int errors = lines.Count(line => errorMarkers.Any(marker => line.Contains(marker, StringComparison.Ordinal)));
						Log($"Log '{logFilePath}': {lines.Count} lines written during the call ({string.Join("; ", sources)}), {warnings} with a warning marker, {errors} with an error marker.");

						// The marked lines first, because a tail alone hides a warning written early in a long run.
						List<string> marked = [.. lines.Where(line => warningMarkers.Concat(errorMarkers).Any(marker => line.Contains(marker, StringComparison.Ordinal)))];
						if (marked.Count > 0)
						{
							Log($"Lines with a warning or error marker, first {Math.Min(logMatchLines, marked.Count)} of {marked.Count}:");
							foreach (string line in marked.Take(logMatchLines))
								Log(line.Length > 400 ? line[..400] + " ..." : line);
						}

						if (logPattern is not null)
						{
							System.Text.RegularExpressions.Regex pattern = new(logPattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
							List<string> matched = [.. lines.Where(line => pattern.IsMatch(line))];
							Log($"Lines that match '{logPattern}', first {Math.Min(logMatchLines, matched.Count)} of {matched.Count}:");
							foreach (string line in matched.Take(logMatchLines))
								Log(line.Length > 400 ? line[..400] + " ..." : line);
						}

						Log($"Last {Math.Min(logTailLines, lines.Count)} lines:");
						foreach (string line in lines.TakeLast(logTailLines))
							Log(line.Length > 400 ? line[..400] + " ..." : line);
					}
				}
				catch (Exception exception)
				{
					Log($"Could not read log '{logFilePath}': {exception.Message}");
				}
			}

			if (closeDocumentsUnder is not null)
			{
				List<Document> left = [.. Application.Documents.OfType<Document>().Where(document => document.FullFileName.StartsWith(closeDocumentsUnder, StringComparison.OrdinalIgnoreCase))];
				if (keepDocumentsOpen)
					Log($"Left open for inspection: {(left.Count == 0 ? "nothing" : string.Join(", ", left.Where(document => document.DocumentType is DocumentTypeEnum.kDrawingDocumentObject or DocumentTypeEnum.kAssemblyDocumentObject).Select(document => document.FullFileName)))}");
				else
					CloseDocumentsUnder(closeDocumentsUnder);
			}
		}

		return "done";
		""";

	[McpServerTool(Name = "inventor_run_plugin")]
	[Description("""
		Runs a method from a plugin's build output inside the live Inventor session, with no Inventor restart per code
		change. The build output is copied to a fresh folder and loaded into a new collectible context on every call, so
		'dotnet build' can overwrite it while Inventor runs, and each call runs the newest build with clean static state.

		Arguments bind by parameter NAME, never by position. A parameter typed InventorServer or Application is filled
		in with the running session. Every other parameter takes the value named in 'arguments', or its default. An
		unknown name, a missing required value, or more than one matching overload is refused with the signatures.
		Instance methods are called on an object built with a constructor taking only an InventorServer, an Application
		or defaults.

		Use closeDocumentsUnder with the folder the plugin writes to: the call refuses to run while a document there
		has unsaved changes, closes the rest before running, and closes what the run opened afterwards unless
		keepDocumentsOpen is set. Use shadowFiles to replace a file in the copy, ex. an appsettings.json with trial
		values, without a build. Read the 'plugin-loop-cycle' skill (inventor_skill) before a first run.
		""")]
	public static Task<object> RunPlugin(
		BridgeClient bridge,
		[Description("Folder holding the build output, ex. the project's bin\\Debug. It is copied, never loaded in place.")] string buildOutputDirectory,
		[Description("File name of the assembly to load from that folder, ex. 'MyPlugin.dll'.")] string assemblyFileName,
		[Description("Full name of the type holding the method, ex. 'MyPlugin.McpServerLoop'.")] string typeName,
		[Description("Name of the public method to call.")] string methodName,
		[Description("Method arguments by parameter name, as JSON strings, numbers, booleans or null. Omit InventorServer and Application parameters.")] Dictionary<string, JsonElement>? arguments = null,
		[Description("Files to write into the shadow copy before loading, by path relative to the copy, ex. {\"appsettings.json\": \"...\"}.")] Dictionary<string, string>? shadowFiles = null,
		[Description("Folder the plugin writes documents to. Guards unsaved work there and closes the run's documents afterwards.")] string? closeDocumentsUnder = null,
		[Description("Leave the documents the run opened under closeDocumentsUnder open, to inspect or measure them.")] bool keepDocumentsOpen = false,
		[Description("A log the plugin writes, as a full file path or a rolling log's base path (ex. Serilog's '%LOCALAPPDATA%\\...\\Logs\\log-.txt', which matches 'log-*.txt'). Environment variables are expanded. Only the lines written during this call are counted and returned, so a log that holds every run works.")] string? logFilePath = null,
		[Description("How many of the lines written during the call to return, from the end. Default 40.")] int logTailLines = 40,
		[Description("Text that marks a warning line, matched case sensitively. Default: WARN, [WRN].")] string[]? warningMarkers = null,
		[Description("Text that marks an error line, matched case sensitively. Default: ERROR, FATAL, [ERR], [FTL].")] string[]? errorMarkers = null,
		[Description("A .NET regular expression. The lines written during the call that match it are returned too, ex. 'Balloon|Leader'.")] string? logPattern = null,
		[Description("How many marked lines, and how many lines that match logPattern, to return. Default 50.")] int logMatchLines = 50,
		CancellationToken cancellationToken = default)
	{
		string code;

		try
		{
			code = ComposeRunPluginSnippet(buildOutputDirectory, assemblyFileName, typeName, methodName, arguments, shadowFiles, closeDocumentsUnder, keepDocumentsOpen, logFilePath, logTailLines, warningMarkers, errorMarkers, logPattern, logMatchLines);
		}
		catch (ArgumentException exception)
		{
			return Task.FromResult<object>(new { error = "invalid-arguments", message = exception.Message });
		}

		return SafeAsync(() => bridge.InvokeAsync<Contracts.Models.ExecutionResult>(
			BridgeOperations.EvalCSharp,
			// The unsaved guard protects the active document from the snippet. This snippet never touches the active
			// document, and guards the plugin's own output folder itself, so the general guard would only get in the way.
			new ExecuteRequest(code, DocumentName: null, AllowUnsavedChanges: true),
			cancellationToken),
			// A plugin run cannot be split, so a warning to split it would only be noise.
			warnOnLongExecution: false);
	}

	/// <summary>
	/// 	Fills the run-plugin snippet's tokens with C# literals.
	/// </summary>
	/// <returns>
	/// 	The snippet to execute.
	/// </returns>
	/// <exception cref="ArgumentException">
	/// 	An argument is not a JSON scalar, or a required value is empty.
	/// </exception>
	private static string ComposeRunPluginSnippet(
		string buildOutputDirectory,
		string assemblyFileName,
		string typeName,
		string methodName,
		Dictionary<string, JsonElement>? arguments,
		Dictionary<string, string>? shadowFiles,
		string? closeDocumentsUnder,
		bool keepDocumentsOpen,
		string? logFilePath,
		int logTailLines,
		string[]? warningMarkers,
		string[]? errorMarkers,
		string? logPattern,
		int logMatchLines)
	{
		foreach ((string name, string value) in new[] { ("buildOutputDirectory", buildOutputDirectory), ("assemblyFileName", assemblyFileName), ("typeName", typeName), ("methodName", methodName) })
		{
			if (string.IsNullOrWhiteSpace(value))
				throw new ArgumentException($"'{name}' is required.");
		}

		// Expanded here, so a call can name a per user folder portably, ex. '%LOCALAPPDATA%\...\Logs\log-.txt'.
		// The server and Inventor run as the same user, so both see the same value.
		if (logFilePath is not null)
			logFilePath = System.Environment.ExpandEnvironmentVariables(logFilePath);

		if (logFilePath is not null && System.IO.Path.IsPathFullyQualified(logFilePath) is false)
			throw new ArgumentException($"'logFilePath' must be a full path, not '{logFilePath}'.");

		// Both spellings by default: plain loggers write WARN and ERROR, Serilog's default template writes [WRN] and [ERR].
		warningMarkers ??= ["WARN", "[WRN]"];
		errorMarkers ??= ["ERROR", "FATAL", "[ERR]", "[FTL]"];
		if (warningMarkers.Concat(errorMarkers).Any(string.IsNullOrEmpty))
			throw new ArgumentException("A warning or error marker cannot be empty, because it would match every line.");

		if (string.IsNullOrEmpty(logPattern) is false)
		{
			try
			{
				_ = new System.Text.RegularExpressions.Regex(logPattern);
			}
			catch (ArgumentException exception)
			{
				throw new ArgumentException($"'logPattern' is not a valid regular expression: {exception.Message}");
			}
		}

		StringBuilder argumentEntries = new();
		foreach ((string name, JsonElement value) in arguments ?? [])
			argumentEntries.AppendLine(CultureInfo.InvariantCulture, $"\t[{CSharpLiteral.String(name)}] = {CSharpLiteral.FromJson(value)},");

		StringBuilder shadowFileEntries = new();
		foreach ((string path, string content) in shadowFiles ?? [])
			shadowFileEntries.AppendLine(CultureInfo.InvariantCulture, $"\t[{CSharpLiteral.String(path)}] = {CSharpLiteral.String(content)},");

		return _runPluginSnippet
			.Replace("__BUILD_OUTPUT__", CSharpLiteral.String(buildOutputDirectory.TrimEnd('\\', '/')), StringComparison.Ordinal)
			.Replace("__ASSEMBLY_FILE__", CSharpLiteral.String(assemblyFileName), StringComparison.Ordinal)
			.Replace("__TYPE_NAME__", CSharpLiteral.String(typeName), StringComparison.Ordinal)
			.Replace("__METHOD_NAME__", CSharpLiteral.String(methodName), StringComparison.Ordinal)
			.Replace("__CLOSE_UNDER__", CSharpLiteral.String(closeDocumentsUnder?.TrimEnd('\\', '/')), StringComparison.Ordinal)
			.Replace("__KEEP_OPEN__", keepDocumentsOpen ? "true" : "false", StringComparison.Ordinal)
			.Replace("__LOG_FILE__", CSharpLiteral.String(logFilePath), StringComparison.Ordinal)
			.Replace("__LOG_TAIL__", Math.Clamp(logTailLines, 0, 500).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
			.Replace("__WARNING_MARKERS__", string.Join(", ", warningMarkers.Select(CSharpLiteral.String)), StringComparison.Ordinal)
			.Replace("__ERROR_MARKERS__", string.Join(", ", errorMarkers.Select(CSharpLiteral.String)), StringComparison.Ordinal)
			.Replace("__LOG_PATTERN__", CSharpLiteral.String(string.IsNullOrEmpty(logPattern) ? null : logPattern), StringComparison.Ordinal)
			.Replace("__LOG_MATCH_LINES__", Math.Clamp(logMatchLines, 0, 500).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
			.Replace("__ARGUMENTS__", argumentEntries.ToString(), StringComparison.Ordinal)
			.Replace("__SHADOW_FILES__", shadowFileEntries.ToString(), StringComparison.Ordinal);
	}
}
