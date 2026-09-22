# Plugin Development Loop

Created: 2026-09-21

Status: **proposed.** Nothing here is built yet.
This document records how `inventor_eval_csharp` can drive a plugin under development inside a live Inventor
session, so an agent can change code, run it, read the model, and iterate without a CAD restart.

The reference case is `C:\repos\Cincinnati\StrobicConfigurator`, which already has the structure this needs.

## The problem

`.claude/CLAUDE.md` records the cost: rebuilding an assembly that Inventor has loaded needs Inventor closed.
Inventor's assembly load context is not collectible, and unloading an add-in through the Add-In Manager runs
`Deactivate` without releasing the file.

A plugin developed the normal way therefore costs one CAD restart per code change.
A restart per iteration is not a loop.

## Why StrobicConfigurator is the right reference

It is already split the way a development loop needs, for reasons that had nothing to do with this.

| Project | Role | Churn in a loop |
| --- | --- | --- |
| `Cincinnati.InventorPlugin` | Headless core. Every service, every model, no host. | High |
| `Cincinnati.InventorPlugin.UI` | Interactive add-in. Ribbon, commands, WPF, dialogs. | Low |
| `Cincinnati.InventorPlugin.DA` | Design Automation plugin. `PluginAutomation`, `PluginServer`. | Medium |
| `Cincinnati.InventorPlugin.Loader` | Isolation shim Inventor actually instantiates. | None |
| `Cincinnati.APS.LocalDebug` | Console harness that drives the DA plugin against a local Inventor. | None |

Three hosts share one core.
The core reaches Inventor through `InventorGlobals`, a static holder the host populates:

```csharp
InventorGlobals.InventorServer = applicationAddInSite.InventorServer;
InventorGlobals.SelectFile = DialogUtils.SelectFile;
InventorGlobals.GetActiveDocument = () => InventorUIGlobals.Application?.ActiveDocument;
```

That is the whole contract.
A snippet that can set those members can run any core service, with no reference to the core's types at compile
time.

`InventorServer` is reachable from the interactive application by a direct cast.
`Cincinnati.APS.LocalDebug` already does it, at `InventorDebugService.cs:221`, on an object it obtained from the
`Inventor.Application` ProgID:

```csharp
return (InventorServer?)_inventorInstance;
```

So inside a snippet, `(InventorServer)Application` is the same handle the add-in would have been given.

## Where a plugin normally lives, and how Inventor finds it

Inventor's own loading runs in three stages, and it is worth separating them, because the development loop
replaces all three with something else.

**1. Discovery.** At startup Inventor reads every `.addin` manifest it finds in its manifest folders:

| Folder | Scope |
| --- | --- |
| `%APPDATA%\Autodesk\Inventor <version>\Addins` | One user, one Inventor version |
| `%PROGRAMDATA%\Autodesk\Inventor <version>\Addins` | All users, one Inventor version |
| `%PROGRAMFILES%\Autodesk\Inventor <version>\Bin\Addins` | Autodesk's own add-ins |
| `%APPDATA%\Autodesk\ApplicationPlugins\<name>` | One user, every version, through `PackageContents.xml` |
| `%PROGRAMDATA%\Autodesk\ApplicationPlugins\<name>` | All users, every version |

**2. Activation.** The manifest carries a `ClassId`. Inventor creates that COM class, casts the result to
`ApplicationAddInServer`, and calls `Activate`. The class id, not the file name, is the identity.

**3. Binding.** The `<Assembly>` element names the DLL to load. It accepts either a bare file name, resolved next
to the manifest, or a full path to anywhere on disk.

That third stage is the part most people assume is fixed, and it is not.
**The plugin binaries do not have to sit in a plugin folder.** Only the manifest does.

This repository uses both forms, and the difference between them is the whole answer:

```
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -p:DeployAddIn=true
```

writes one small `.addin` file into `%APPDATA%\Autodesk\Inventor 2027\Addins`, with `<Assembly>` set to the full
path of `bin\Debug\...\InventorMcp.AddIn.dll`. Nothing is copied. Inventor loads the build output in place.

```
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -c Release -p:DeployBundle=true
```

writes a complete bundle instead, manifest and binaries together, which is what a user installs.

`Cincinnati.InventorPlugin.UI` ships the bundle form, and its layout is deliberate:

```
%APPDATA%\Autodesk\ApplicationPlugins\Cincinnati.InventorPlugin.UI\
	Cincinnati.InventorPlugin.UI.addin      <Assembly> names the loader, not the plugin
	Cincinnati.InventorPlugin.Loader.dll    the only assembly Inventor itself loads
	App\
		Cincinnati.InventorPlugin.UI.dll    the real add-in, one folder down on purpose
		Cincinnati.InventorPlugin.dll
		...every dependency
```

The `App\` subfolder is not tidiness. The default load context probes the folder holding the assembly it loaded,
so anything beside the loader could be resolved into the default context by accident.
One folder down is out of reach, which is why the build target actively deletes stale DLLs from the bundle root.

## Where the plugin lives during the development loop

**Nowhere new. It stays in `bin\`.**

The loop installs nothing, registers nothing, and writes nothing to any Inventor folder.
There is no manifest for the plugin under development, no class id, and no entry in the Add-In Manager.
Inventor is never told the plugin exists.

The only thing Inventor has loaded is the MCP bridge, through its own ordinary manifest.
From there the chain is: bridge loads Roslyn, Roslyn compiles a snippet, and the snippet copies the plugin's
build output to a temporary folder and loads it.

| | Inventor's loader | The development loop |
| --- | --- | --- |
| Where the binaries live | A plugin folder, or wherever `<Assembly>` points | `bin\`, untouched; a temporary copy is what loads |
| What is registered | A `.addin` manifest and a class id | Nothing |
| What triggers the load | Inventor startup, or the Add-In Manager | An `inventor_eval_csharp` call, at any moment |
| Who creates the object | COM activation by class id | `Activator.CreateInstance` by type name |
| Entry point | `ApplicationAddInServer.Activate` only | Any public member of any type |
| Load context | Default, or one isolated context for the process | A fresh collectible context per iteration |
| Unload | Never. The file stays locked for the process lifetime | Best effort, and no file lock at any point |
| Cost of a code change | Close Inventor, rebuild, reopen | Rebuild. The next snippet picks it up |

The two modes coexist without interfering, because the loop never reads the installed bundle and the bundle never
reads `bin\`. A shipped add-in can stay active while the loop runs, at the cost of interleaved logs.

## What the existing Loader does, and what it does not do

`Cincinnati.InventorPlugin.Loader` already solves assembly isolation.
`AddinLoadContext` keeps the plugin and its dependencies out of the context Inventor and other add-ins share,
and lets `Autodesk.Inventor.Interop` unify to the default context so COM casts still work across the boundary.
`IsolatedAddinServerProxy` is what the `.addin` manifests name, so the real add-in assembly is never pulled into
the default context.

It exists because **Inventor had no isolation of its own before 2027**.
Inventor 2027 added the `UseInventorAssemblyContext` manifest element, which this repository's add-in sets to `0`
to get the isolated mode. Inventor 2026 and earlier ignore the element, so a plugin that must run on them has to
build its own isolation, which is exactly what the Loader project is.

The two approaches are the same idea at different layers.
Inventor 2027 isolates the assembly named in `<Assembly>`.
The Loader pattern keeps `<Assembly>` pointing at a tiny shim and isolates everything behind it.
The shim form keeps working on every version, which is why it is worth keeping even after 2027.

Neither one is hot reload. `AddinLoadContext` misses it on two lines:

1. The context is created with `base(addinAssemblyName)` and no `isCollectible` flag, so it can never unload.
2. It calls `LoadFromAssemblyPath`, which holds an open file handle, so the build output stays locked.

Inventor 2027's own isolated mode misses it on the same two lines, and adds a third:
the context is created once, when Inventor starts, and there is no way to ask for a second one.

The development loop therefore needs a third loader, differing on exactly those points.
It replaces neither, and it never touches the installed `ApplicationPlugins` bundle.

## The development loader

The whole thing is a snippet. No add-in change, so no restart.

**Shadow copy, then load from the copy.** Copy the build output to a new folder for each iteration and load from
there. The source build output is then never locked, so the next `dotnet build` writes over it freely.

```csharp
string source = @"C:\repos\Cincinnati\StrobicConfigurator\Source\Desktop\Inventor\Cincinnati.InventorPlugin\bin\Debug\net8.0-windows";
string shadow = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "strobic-loop", System.Guid.NewGuid().ToString("n"));

System.IO.Directory.CreateDirectory(shadow);
foreach (string file in System.IO.Directory.GetFiles(source))
	System.IO.File.Copy(file, System.IO.Path.Combine(shadow, System.IO.Path.GetFileName(file)));
```

`System.IO.File` and `System.IO.Path` must be fully qualified.
The `Inventor` namespace is imported into every snippet, and `Inventor.File` and `Inventor.Path` shadow the
`System.IO` types.

**Resolve dependencies from the shadow copy, and only from there.**
This is the same rule the shipping loader follows, for the same reason.
Returning `null` falls back to the default context, which is what must happen for `Autodesk.Inventor.Interop`,
so that `InventorServer` is one type on both sides.

```csharp
System.Runtime.Loader.AssemblyLoadContext context = new("strobic-loop", isCollectible: true);
System.Runtime.Loader.AssemblyDependencyResolver resolver = new(System.IO.Path.Combine(shadow, "Cincinnati.InventorPlugin.dll"));

context.Resolving += (loadContext, assemblyName) =>
{
	string? path = resolver.ResolveAssemblyToPath(assemblyName);
	return path is null ? null : loadContext.LoadFromAssemblyPath(path);
};

System.Reflection.Assembly core = context.LoadFromAssemblyPath(System.IO.Path.Combine(shadow, "Cincinnati.InventorPlugin.dll"));
```

**Populate the contract and invoke by reflection.**

```csharp
Type globals = core.GetType("Cincinnati.InventorPlugin.InventorGlobals")!;
globals.GetProperty("InventorServer")!.SetValue(null, (InventorServer)Application);

Type service = core.GetType("Cincinnati.InventorPlugin.Services.AssemblyBuildService")!;
object instance = Activator.CreateInstance(service)!;
object? report = service.GetMethod("Build")!.Invoke(instance, [ /* arguments */ ]);

Log(report);
```

`InventorGlobals` is static, so each context gets its own copy of it.
That is a benefit rather than a cost: every iteration starts with clean static state, and iteration 9 cannot
inherit a stale handle from iteration 3.

## Why no restart is needed

Three separate things have to be true, and each one is handled above.

| Requirement | How it is met |
| --- | --- |
| The build output must not be locked | It is copied, and only the copy is loaded |
| The new code must win over the old code | A new context per iteration, each with its own copy of every type |
| Inventor's own types must stay single | The resolver returns `null` for the interop assembly, so it unifies to the default context |

Inventor itself never loads the plugin in this mode.
It loads the MCP add-in once, the MCP add-in loads Roslyn, and Roslyn compiles a snippet that loads the plugin.
Inventor's non-collectible context holds none of the code that churns.

## Unloading, and what pins a context

`isCollectible: true` makes `context.Unload()` legal, but unload is best effort.
The context survives while anything roots it.

The known roots in this codebase:

- A COM runtime callable wrapper parked in a static, which `InventorGlobals.InventorServer` is exactly.
	Set it back to `null` before unloading.
- An Inventor event subscription taken by the core, which would also leave iteration 3's handler firing during
	iteration 9. That is worse than a leak.
- A live WPF or WinForms object, which is why the loop should drive `Cincinnati.InventorPlugin` rather than
	`Cincinnati.InventorPlugin.UI`.

If a context will not unload, let it leak. A few tens of megabytes per session is cheaper than a CAD restart.
Leaked event handlers are not cheap, so audit those.

## The loop

1. **Look the API up.** `inventor_api_lookup` confirms members exist, and works with Inventor closed.
2. **Edit the core.** `Cincinnati.InventorPlugin` only, in the normal way.
3. **Build the core alone.**
	`dotnet build Source/Desktop/Inventor/Cincinnati.InventorPlugin/Cincinnati.InventorPlugin.csproj`
4. **Load and run** through `inventor_eval_csharp`, as above.
5. **Verify against the model, not the return value.**
	- `inventor_health` after any write, for sick features
	- a geometry count inside the snippet when a feature is meant to cut material
	- `inventor_parameters` when the result is parametric
	- `inventor_activity` for the ordered transaction narration, which shows what the plugin really did
6. **Reset.** Open a copy of a baseline document, run, check, then close without saving.
	A dirty fixture makes two runs disagree for reasons that are not the code.
7. Repeat from step 2.

## Driving the Design Automation path locally

`PluginAutomation` takes an `InventorServer` in its constructor and reads its arguments from configuration,
so the same loop reaches it:

```csharp
Type automation = core.GetType("Cincinnati.InventorPlugin.DA.PluginAutomation")!;
object plugin = Activator.CreateInstance(automation, [(InventorServer)Application])!;
automation.GetMethod("Run")!.Invoke(plugin, [null]);
```

This removes the APS round trip from the inner loop, which is the slowest feedback path in the repository.
`Cincinnati.APS.LocalDebug` already exists for the same purpose, and the two are complementary:
LocalDebug reproduces the headless host, while this reproduces the code path with a live model to inspect.

It is not a substitute for a real work item.
Design Automation has no `ActiveDocument`, a different working directory, and an input file set the engine
stages. Those differences are exactly what LocalDebug and a real work item are for.

## What this loop cannot cover

- Ribbon definition and button placement, in `RibbonCustomizationService`.
- `Activate` and `Deactivate` lifecycle, and anything that depends on real add-in registration.
- The `IsolatedAddinServerProxy` path itself, which is only exercised when Inventor instantiates it by class id.
- Manifest discovery and the bundle layout. The loop never reads a `.addin` file, so a wrong `<Assembly>` value,
	a stale DLL at the bundle root, or a missing `App\` folder all pass the loop and fail on install.
- Any interactive dialog. A modal dialog owns Inventor's message pump, and the bridge posts its work to that
	pump, so an unanswered dialog stalls every tool except the activity feed.
	The core's `FileSelector` and `FolderSelector` delegates make this avoidable: the loop supplies a stub that
	returns a fixed path and never shows anything.

Those need one manual pass with the bundle installed.
Because the hosts are thin and the core carries the work, that pass is short and rare.

## Friction to expect

- **The unsaved changes guard.** Once the plugin writes, the document is dirty, and every later
	`inventor_eval_csharp` call needs `allowUnsavedChanges: true`.
	That is correct over a throwaway fixture, but it removes a safety net, so keep the fixture disposable.
- **Two copies of the core in one process** if the shipping add-in is also active.
	They sit in different contexts with separate statics, so they do not collide, but the logs interleave.
	Deactivating the shipping add-in during a core loop removes the confusion.
- **Roslyn is already loaded twice** in the process, 4.13 for iLogic and 4.14 for this add-in.
	A plugin that brings its own copy adds a third, which the resolver must keep inside the shadow folder.

## Possible server side tools

Neither needs an add-in rebuild, so both follow the pattern `inventor_orientation` already uses:
a canned snippet composed in the server.

- `inventor_load_assembly` - shadow copy a build output, load it into a fresh collectible context, and report
	the exported types with their public entry points.
- `inventor_invoke` - call a named type and method in the last loaded assembly, and return the result together
	with the post call health report.

Both are worth building only after the raw snippet form has been run by hand a few times against
StrobicConfigurator, so the canned version encodes something that is known to work.
