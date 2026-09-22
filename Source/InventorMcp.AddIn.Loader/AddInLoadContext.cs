using System.Reflection;
using System.Runtime.Loader;

namespace InventorMcp.AddIn.Loader;

/// <summary>
/// 	Isolated dependency container for the add-in on Inventor releases that cannot isolate it themselves.
/// </summary>
/// <remarks>
/// 	The add-in and its dependencies load here, and only here, so exactly one copy exists in the process.
/// 	A second copy in the default context would win any lookup by name, which is how Roslyn resolves a script's
/// 	globals type, and the globals object would then fail to cast.
/// 	<see cref="Load" /> returns null for anything the add-in's deps.json does not name, so the interop falls back
/// 	to the default context and COM types keep one managed identity.
/// 	References:
/// 	<a href="https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support">
/// 	Microsoft/Tutorials/Plugins
/// 	</a>
/// </remarks>
internal sealed class AddInLoadContext : AssemblyLoadContext
{
	/// <summary>
	/// 	Subfolder holding the add-in, one level below this loader so the default context cannot resolve it.
	/// </summary>
	public const string AddInFolderName = "App";

	/// <summary>
	/// 	Simple name of the add-in assembly inside <see cref="AddInFolderName" />.
	/// </summary>
	public const string AddInAssemblyName = "InventorMcp.AddIn";

	private static AddInLoadContext? _instance;

	private readonly AssemblyDependencyResolver _resolver;
	private readonly Assembly _addInAssembly;

	private AddInLoadContext(string addInAssemblyPath) : base(AddInAssemblyName)
	{
		_resolver = new AssemblyDependencyResolver(addInAssemblyPath);
		_addInAssembly = LoadFromAssemblyPath(addInAssemblyPath);
	}

	/// <summary>
	/// 	Creates an instance of an add-in type inside the isolated context.
	/// </summary>
	/// <param name="typeFullName">
	/// 	Namespace qualified name of the type, ex. "InventorMcp.AddIn.StandardAddInServer".
	/// </param>
	/// <returns>
	/// 	The instance created in the isolated context.
	/// </returns>
	public static object CreateIsolatedInstance(string typeFullName)
	{
		_instance ??= Create();

		return _instance._addInAssembly.CreateInstance(typeFullName)
			?? throw new TypeLoadException($"Type '{typeFullName}' was not found in '{_instance._addInAssembly.Location}'.");
	}

	/// <summary>
	/// 	Resolves a managed dependency from the add-in's deps.json, or defers to the default context.
	/// </summary>
	/// <param name="assemblyName">
	/// 	The assembly being resolved.
	/// </param>
	/// <returns>
	/// 	The loaded assembly, or null to fall back to the default context.
	/// </returns>
	protected override Assembly? Load(AssemblyName assemblyName)
	{
		string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);

		return assemblyPath is not null
			? LoadFromAssemblyPath(assemblyPath)
			: null;
	}

	/// <summary>
	/// 	Resolves a native dependency from the add-in's deps.json, or defers to the default context.
	/// </summary>
	/// <param name="unmanagedDllName">
	/// 	The native library being resolved.
	/// </param>
	/// <returns>
	/// 	A handle to the loaded library, or <see cref="nint.Zero" /> to fall back to the default context.
	/// </returns>
	protected override nint LoadUnmanagedDll(string unmanagedDllName)
	{
		string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);

		return libraryPath is not null
			? LoadUnmanagedDllFromPath(libraryPath)
			: nint.Zero;
	}

	private static AddInLoadContext Create()
	{
		string loaderDirectory = Path.GetDirectoryName(typeof(AddInLoadContext).Assembly.Location)!;
		string addInAssemblyPath = Path.Combine(loaderDirectory, AddInFolderName, $"{AddInAssemblyName}.dll");

		if (File.Exists(addInAssemblyPath) is false)
		{
			throw new FileNotFoundException(
				$"The add-in was not found at '{addInAssemblyPath}'. The '{AddInFolderName}' subfolder must hold the add-in's full build output.",
				addInAssemblyPath);
		}

		return new AddInLoadContext(addInAssemblyPath);
	}
}
