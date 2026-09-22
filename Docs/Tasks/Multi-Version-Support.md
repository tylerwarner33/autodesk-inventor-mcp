# Multi Version Support

Created: 2026-09-22

Status: **built and verified live on Inventor 2025, 2026 and 2027.**
The bridge now builds for Inventor 2025, 2026 and 2027 from one repository, one installed bundle, and one MCP
server. What is proven and what is not is recorded under "As built".

Scope is **2025 and later**. See "Why 2023 and 2024 are out of scope" for the reasoning.

## Why only the add-in is version bound

`Architecture.md` records the decision to keep every tool in the server process, so that changing a tool costs no
Inventor restart. That decision pays a second time here.

| Project | Version bound | Why |
| --- | --- | --- |
| `InventorMcp.Server` | No | Never references interop. Talks a pipe. Stays `net10.0`. |
| `InventorMcp.Contracts` | No, once retargeted | Pure data transfer objects. |
| `InventorMcp.AddIn` | **Yes** | Loads into Inventor's process, against Inventor's interop. |

So supporting three releases means building one thin assembly three times.
It does not mean three servers, three tool surfaces, or three sets of tests.

## What is pinned to 2027 today

| Where | Value |
| --- | --- |
| `Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj:5` | `net10.0-windows` |
| `Directory.Build.props:33` | `AutodeskVersion` is `2027`, which also selects `Libs\Inventor\2027` |
| `Source/InventorMcp.AddIn/InventorMcp.AddIn.addin.template:19` | `SupportedSoftwareVersionGreaterThan` of `30..` |
| `Source/InventorMcp.AddIn/InventorMcp.AddIn.addin.template:29` | `UseInventorAssemblyContext`, honoured only by 2027 |
| `Directory.Build.props:7` | `TargetFramework` of `net10.0`, inherited by `InventorMcp.Contracts` |

The Addins folder is already parameterised through `$(AutodeskVersion)`, so it needs no change.

## The version matrix

| Inventor | Software version | Host CLR | Target framework | Isolation mechanism |
| --- | --- | --- | --- | --- |
| 2025 | 29.0 | .NET 8, moving to .NET 10 | `net8.0-windows` | `InventorMcp.AddIn.Loader` |
| 2026 | 30.0 | .NET 8, moving to .NET 10 | `net8.0-windows` | `InventorMcp.AddIn.Loader` |
| 2027 | 31.0 | .NET 10 | `net10.0-windows` | Inventor's own, through the manifest |

An add-in loads into Inventor's already running CLR.
The target framework is therefore a hard requirement, not a preference.
A `net10.0` assembly cannot load into Inventor 2025 today.

### The target framework dimension is temporary

Autodesk is moving Inventor 2025 and 2026 to .NET 10 later in 2026.
When that lands, every supported release hosts the same runtime and the whole column collapses to
`net10.0-windows`.

Two consequences worth designing for now:

- **Do not reach for `netstandard2.0` anywhere.** It only earns its place when `net48` is a target, and it never
	is here. The floor is `net8.0` today and `net10.0` afterwards.
- **The isolation dimension does not collapse with it.** `UseInventorAssemblyContext` is an Inventor 2027
	feature, not a runtime feature, so 2025 and 2026 will still ignore it after they move to .NET 10.
	The loader from change 4 stays necessary for as long as those releases are supported.

So the version matrix shrinks from three differences to two, and the one that disappears is the cheap one.

## Why 2023 and 2024 are out of scope

Both run on .NET Framework 4.8, which has no `AssemblyLoadContext`.
The reference sample at `C:\repos\_MyProjects\autodesk-inventor-assembly-load-context` falls back to
`ResolveHelper`, which redirects `AppDomain.AssemblyResolve` for the duration of a call.

That is enough for an add-in whose dependency conflict is a logging package.
It is not enough for this add-in.

`.claude/CLAUDE.md` records the measurement: `Microsoft.CodeAnalysis` is loaded twice in a live session, 4.13.0.0
in the default context because iLogic is built on Roslyn, and 4.14.0.0 in this add-in's context.
Isolation is what keeps them apart. One `AppDomain` has one binding identity per assembly name, so on `net48`
a single Roslyn version wins the whole process, and the losing side is either iLogic or the execution tools.

Supporting 2023 and 2024 is therefore not a build change. It is a question about whether the execution tools can
work at all there, and it should be measured before it is promised.

## Change 1 - the version becomes a build dimension

The reference sample drives this from `$(Configuration.Contains('2025'))` because Visual Studio needs a dropdown.
This repository builds from the command line, so a property reads better:

```
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -p:AutodeskVersion=2025 -p:DeployAddIn=true
```

One `Choose` block in `Directory.Build.props` maps `AutodeskVersion` to everything that follows from it:

| Property | 2025 | 2026 | 2027 |
| --- | --- | --- | --- |
| `TargetFramework` | `net8.0-windows` | `net8.0-windows` | `net10.0-windows` |
| `InventorSoftwareVersion` | `29` | `30` | `31` |
| `InventorInteropDir` | `Libs\Inventor\2025` | `Libs\Inventor\2026` | `Libs\Inventor\2027` |
| `UseAddInLoader` | `true` | `true` | |

`InventorMcp.AddIn.csproj` then drops its hard coded `TargetFramework` and reads the property.

### The output path must carry the version

`Directory.Build.props:20` sets `AppendTargetFrameworkToOutputPath` to `false`, so the add-in builds to a flat
`bin\Debug`. That is correct for the server, where it keeps `.mcp.json` from having to name a target framework.

For the add-in it becomes a defect the moment there is more than one version.
All three builds would write to the same folder and overwrite each other, and the last one built would be the one
every deployed manifest points at, whatever version that manifest claims to support.

The symptom is bad: the manifest is filtered correctly by Inventor, the add-in loads, and then fails inside
`Activate` with a type load error, because the assembly is built for a different runtime than the one that loaded
it. Nothing about that failure names the real cause.

So `InventorMcp.AddIn.csproj` overrides it:

```xml
<!-- Three Inventor versions build from this one project, so the output path must separate them.
     The server keeps the flat path, because .mcp.json names it. -->
<OutputPath>$(ProjectDir)bin\$(Configuration)\$(AutodeskVersion)\</OutputPath>
```

That is also what lets the development install work for several versions at once.
Each version's manifest points at its own `bin\Debug\<version>` folder, so 2025 and 2027 can both be installed
from source at the same time without either build disturbing the other.

## Change 2 - vendor the interop per version

`Libs\Inventor\2025\` and `Libs\Inventor\2026\` beside the existing `2027\`, each holding
`Autodesk.Inventor.Interop.dll` and its `.xml`.

This matters twice, and the second one is easy to miss.
The add-in compiles against the DLL, and **`inventor_api_lookup` reads the XML**.
A lookup answered from the 2027 documentation will confidently name members that do not exist in 2025,
which is the worst possible failure for a tool whose whole purpose is to stop the model guessing.

The lookup tool therefore has to select its documentation file from the connected session's version,
not from a constant.

## Change 3 - drop Contracts to net8.0, for now

`InventorMcp.Contracts` currently inherits `net10.0` and so cannot be referenced by a `net8.0-windows` add-in.

Set it to `net8.0` explicitly. A `net10.0` server references a `net8.0` library without complaint, so one
target framework covers the server and all three add-in builds, with no multi targeting.

**Not `netstandard2.0`.** That only earns its place when `net48` is a target, and it never is here.
It would cost the modern BCL surface for a compatibility tier this repository does not support.

This is the one change on the list with a scheduled end.
When Inventor 2025 and 2026 move to .NET 10, put `InventorMcp.Contracts` back on `net10.0` and delete the
explicit target framework, so it inherits from `Directory.Build.props` again.
Leave a comment saying so, because a lone `net8.0` with no explanation reads as an accident later.

## Change 4 - isolation below 2027, through a loader shim

Inventor 2027 reads `UseInventorAssemblyContext` from the manifest and does the isolation itself.
Inventor 2025 and 2026 ignore the element, so something else has to do it.

### What was tried first, and why it failed

The first build used the self isolating pattern from the reference sample at
`C:\repos\_MyProjects\autodesk-inventor-assembly-load-context`: the add-in's server derived from an
`IsolatedApplicationAddInServer` base, which reloaded the same DLL into an `AssemblyLoadContext` and forwarded
`Activate` to the second copy.

It loaded, and the bridge started, on the first try against Inventor 2025.
Then `inventor_eval_csharp` failed:

```
[A] InventorScriptGlobals from context "InventorMcp.AddIn" cannot be cast to
[B] InventorScriptGlobals from context "Default"
```

The cause is in Roslyn, confirmed from its source. The scripting host's load context asks the default context
first and consults `InteractiveAssemblyLoader.RegisterDependency` only when the default context cannot resolve
the name:

```csharp
protected override Assembly? Load(AssemblyName assemblyName) => null;
Resolving += (_, assemblyName) => _loader.ResolveAssembly(...);
```

The self isolating pattern keeps a forwarding copy of the add-in in the default context, so that copy won.
On 2027 the default context never holds the add-in, which is why the same code works there.

So the pattern is unsafe for any add-in that is resolved **by name**, and a Roslyn script host is exactly that.
The reference sample never hits it, because nothing in it resolves the add-in by name.

### What replaced it

The pattern `C:\repos\Cincinnati\StrobicConfigurator` uses, and whose remarks name this exact invariant:
**exactly one copy of the add-in assembly in the process.**

A separate `InventorMcp.AddIn.Loader` project is the only assembly Inventor 2025 and 2026 load into the default
context. It holds three types and references nothing but the BCL and the interop:

| Type | Job |
| --- | --- |
| `IsolatedAddInServerProxy` | What the manifest names. Forwards every `ApplicationAddInServer` call. |
| `AddInLoadContext` | Loads the add-in from `App\` into an isolated context. |
| `StartupLog` | Records a bootstrap failure, which Inventor would otherwise swallow. |

The proxy carries the add-in's class id. Only one manifest is live in a given process, so the two never meet.
It reaches the isolated add-in through a direct `ApplicationAddInServer` cast, which works because the interop
resolves from the default context on both sides.

The add-in sits in an `App\` subfolder, one level below the loader, so the default context cannot resolve it
even by accident. A build target deletes anything else at the loader's level for the same reason.

```
Contents\2025\                         Contents\2027\
	InventorMcp.AddIn.Loader.dll  <- manifest     InventorMcp.AddIn.dll  <- manifest
	App\                                          Microsoft.CodeAnalysis*.dll
		InventorMcp.AddIn.dll                       ...
		Microsoft.CodeAnalysis*.dll
```

### What this does to the add-in

The add-in is back to a plain `ApplicationAddInServer`, identical source on every release.
All version specific isolation lives in the loader, and there is no `#if` for it anywhere.

`UseAddInLoader` in `Directory.Build.props` marks the releases that need it. For those releases the add-in builds
into `bin\<configuration>\<version>\App\`, the loader is staged beside `App\`, and both manifests name the loader.

2027 is unchanged from its verified state. Its manifest names the add-in, and Inventor isolates it.

The loader is legacy release support by design. When 2025 and 2026 support ends, delete the project and the two
`UseAddInLoader` rows; the add-in does not change. The .NET 10 move does not remove it, because
`UseInventorAssemblyContext` is an Inventor 2027 feature, not a runtime one.

The manifest keeps `UseInventorAssemblyContext` for every release. 2025 and 2026 ignore it harmlessly.

## Change 5 - one bundle in the version independent folder

**Yes, this works, and it is how Autodesk gates its own add-ins.**

Verified on this machine, in `C:\ProgramData\Autodesk\ApplicationPlugins`:

- Inventor scans `ApplicationPlugins` for `.addin` manifests. `PackageContents.xml` is not required.
	`AttributeHelper` ships a manifest and a DLL with no `PackageContents.xml` at all.
- `<Assembly>` accepts a relative subpath. The Vault bundles use `.\Contents\InventorVault.dll`.
- **`SupportedSoftwareVersionEqualTo` gates a manifest to exactly one release.**
	Manifests installed here carry `29..`, `30..` and `31..`, which are Inventor 2025, 2026 and 2027.

That gives a single version independent bundle holding all three builds:

```
%APPDATA%\Autodesk\ApplicationPlugins\InventorMcp.AddIn\
	InventorMcp.AddIn.2025.addin      SupportedSoftwareVersionEqualTo 29..
	InventorMcp.AddIn.2026.addin      SupportedSoftwareVersionEqualTo 30..
	InventorMcp.AddIn.2027.addin      SupportedSoftwareVersionEqualTo 31..
	Contents\
		2025\   net8.0-windows build, its own Roslyn, its own deps.json
		2026\   net8.0-windows build
		2027\   net10.0-windows build
```

Each manifest points at its own subfolder, ex. `.\Contents\2025\InventorMcp.AddIn.dll`.
Every installed Inventor reads all three manifests, and each one loads exactly the manifest that matches its
software version. The other two are filtered out before any assembly is touched.

Each class id must stay the same across the three manifests, because it identifies the add-in to the user and to
the Add-In Manager, and only one manifest is ever live in a given process.

`DeployBundle` changes from writing one `Contents` folder to looping the version list.
`DeployAddIn`, the development install, stays per version and keeps writing a single manifest into
`%APPDATA%\Autodesk\Inventor <version>\Addins` that points straight at the build output.

## Change 6 - build every version, deploy what is installed

These are two different questions and they want two different answers.

**Building needs no Inventor.** The interop is vendored under `Libs`, which is the reason it is vendored.
So the build always produces all three, on a developer machine and on a build agent alike:

```
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -p:AutodeskVersion=2025
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -p:AutodeskVersion=2026
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -p:AutodeskVersion=2027
```

A `Directory.Build.targets` entry, or a small `dotnet msbuild` target, can drive the loop from one command.

**Deployment is where detection belongs.** The development install writes into a version specific Addins folder,
so it should skip a version that is not installed:

```xml
<InventorInstalled Condition="Exists('$(ProgramFiles)\Autodesk\Inventor $(AutodeskVersion)\Bin\Inventor.exe')">true</InventorInstalled>
```

The bundle install does not need detection at all.
An unused `Contents\2025` folder costs disk space and nothing else, because no installed Inventor reads a
manifest gated to a release it is not.

Detecting installs is therefore a convenience for the development loop, not a correctness requirement.
This machine currently has 2024, 2025, 2026 and 2027 installed, so the skip is worth having.

## Change 7 - how the server finds the active session

The honest answer is that it does not need to search, because of how the pipe already works.

**One Inventor session owns the pipe.** `InventorMcp.Bridge` is a fixed name, and `Architecture.md` records that
a second session logs the conflict rather than competing. Whichever Inventor started first is the one the server
reaches, whatever version it is.

So the only change needed is the **handshake**: the add-in reports its Inventor version when the server connects,
and the server holds it for the session. `inventor_session` already returns `SoftwareVersion`, so the data exists.
What it enables is real:

- `inventor_api_lookup` selects the matching documentation file rather than always reading 2027.
- A canned snippet that depends on a member added after 2025 can refuse with a readable message instead of
	failing inside Roslyn.
- The activity feed and health reports can note the version in their output, so a transcript says which release
	produced it.

**Side by side needs more, and should be opt in.** If 2025 and 2027 both run, the first to start owns
`InventorMcp.Bridge` and the second is unreachable. To choose deliberately, have each add-in also listen on a
versioned alias, ex. `InventorMcp.Bridge.2025`, and give the server a configuration value naming the version to
prefer. The server tries the alias when one is configured and falls back to the fixed name.

That is worth building only when a real need appears.
Having four Inventor versions installed is common. Running two at once is not.

## As built

| Change | Where |
| --- | --- |
| `AutodeskVersion` drives target framework, interop, manifest version and loader use | `Directory.Build.props` |
| Unsupported release fails with a readable error, not an SDK message | `Directory.Build.props` |
| Version qualified output path, with `App\` below the loader on 2025 and 2026 | `InventorMcp.AddIn.csproj` |
| `net8.0` contract, marked for removal | `InventorMcp.Contracts.csproj` |
| Loader shim, proxy, load context and startup failure log | `InventorMcp.AddIn.Loader` |
| `SupportedSoftwareVersionEqualTo` token | `InventorMcp.AddIn.addin.template` |
| Three manifest bundle layout | `InventorMcp.AddIn.csproj` |
| Release year on the handshake, captured on every session call, cleared on disconnect | `SessionInfo`, `BridgeClient` |
| Per release API documentation, selected by the connected session | `ApiReferenceService`, `InventorMcp.Server.csproj` |

Verified in a deployed bundle: three manifests gated to 29, 30 and 31. The 2025 and 2026 manifests name the loader,
which sits alone at its level with the add-in and Roslyn in `App\`. The 2027 manifest names the add-in directly.
No interop assembly appears anywhere.

### Verified against live sessions

**Inventor 2027**, after the refactor. The add-in loads with nothing in `addin-startup.log`.
`inventor_session` reports `releaseYear` 2027. `inventor_api_lookup` answers, and `inventor_eval_csharp` runs
on .NET 10.0.12. Listing every load context shows the isolation intact:

```
[Default]  Autodesk.Inventor.Interop 31.0, Microsoft.CodeAnalysis 4.13.0.0
[]         InventorMcp.AddIn, InventorMcp.Contracts, Microsoft.CodeAnalysis 4.14.0.0
```

The unnamed context is the one Inventor 2027 creates from `UseInventorAssemblyContext`.

**Inventor 2025**, with the loader shim. Verified in full.

- The bridge started with nothing in `addin-startup.log`.
- `inventor_session` reported `releaseYear` 2025, reached by reconnecting from an earlier session, which also
	proves the `CloseAsync` fix below.
- `inventor_api_lookup` answered, and `inventor_eval_csharp` ran on .NET 8.0.30.
- After an `inventor_run_ilogic` call forced iLogic to load its own Roslyn, the load contexts read:

```
[Default]            Autodesk.Inventor.Interop 29.3, InventorMcp.AddIn.Loader, Microsoft.CodeAnalysis 4.6.0.0
[InventorMcp.AddIn]  InventorMcp.AddIn, InventorMcp.Contracts, Microsoft.CodeAnalysis 4.14.0.0
```

Only the loader is in `Default`, and the add-in exists once, in the loader's context.
**iLogic on 2025 ships Roslyn 4.6**, a wider gap from the add-in's 4.14 than 2027's 4.13, and both script hosts
ran in the same process. iLogic loads its Roslyn lazily, so a fresh session shows no 4.6 until a rule has run;
a check that skips that step does not test the collision at all.

The first, self isolating build had already loaded on 2025 and answered the session and lookup calls, before
`inventor_eval_csharp` failed with the cast described under change 4. That failure is what led to the loader.

### Found along the way

**`System.Threading.Lock` is .NET 9 and later.** `BridgeLog` and `ActivityRecorder` both use it, so the 2025 and
2026 builds failed to compile. A single global alias in `GlobalUsings.cs` maps `Lock` to `object` below .NET 9,
which keeps the conditional out of the code that uses it. It disappears with the .NET 10 move.

**`PackageContents.xml` had to go.** It named `./Contents/InventorMcp.AddIn.addin`, a path the three manifest
layout no longer has, and one such file cannot describe three releases deployed by three separate build runs.
Inventor discovers `.addin` manifests under `ApplicationPlugins` directly, which Autodesk's own Vault bundles and
`AttributeHelper` both rely on, so the file was deleted rather than rewritten.

**The server could not survive an Inventor session closing.** This bug predates this work. Switching releases was
the first thing to close a session under a live server. The reconnect path called `BridgeClient.CloseAsync`, whose
`StreamWriter` dispose flushes, and a flush on a dead pipe throws. So the cleanup failed and the reconnect never
ran. Each dispose now tolerates `IOException`, and `ReleaseYear` clears so the API lookup cannot keep answering
for the previous release.

**A snippet's view of an assembly is not the add-in's view.** `typeof(SyntaxTree)` inside a snippet reported the
default context. It had bound to iLogic's Roslyn 4.13, not the add-in's 4.14. To check isolation, list
`AssemblyLoadContext.All`. Do not ask a type from inside the script.

**Inventor 2026**, with the loader shim. Verified in full, the same checks as 2025.
The session reported `releaseYear` 2026, the lookup answered, and `inventor_eval_csharp` ran on .NET 8.0.30.
After an iLogic rule ran:

```
[Default]            Autodesk.Inventor.Interop 30.20, InventorMcp.AddIn.Loader, Microsoft.CodeAnalysis 4.13.0.0
[InventorMcp.AddIn]  InventorMcp.AddIn, InventorMcp.Contracts, Microsoft.CodeAnalysis 4.14.0.0
```

iLogic's Roslyn per release, as measured:

| Inventor | iLogic Roslyn | Add-in Roslyn |
| --- | --- | --- |
| 2025 | 4.6.0.0 | 4.14.0.0 |
| 2026 | 4.13.0.0 | 4.14.0.0 |
| 2027 | 4.13.0.0 | 4.14.0.0 |

The development install is opt in per release. 2026 first failed to load only because its manifest had never been
deployed; building a release does not install it.

## Follow up, when 2025 and 2026 move to .NET 10

A short, self contained cleanup. Nothing here blocks the work above.

1. Delete the `TargetFramework` rows from the `Choose` block. Every release is `net10.0-windows`.
2. Delete the explicit `net8.0` from `InventorMcp.Contracts` so it inherits from `Directory.Build.props` again.
3. Keep everything else. The interop still differs per release, and 2025 and 2026 still ignore
	`UseInventorAssemblyContext`, so the vendored `Libs` folders and the loader both stay.

Confirm the host runtime before doing this rather than going by the release note.
`inventor_eval_csharp` answers it directly:

```csharp
Log(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
```

## What this does not cover

- Inventor 2023 and 2024, for the reason above.
- Interop API differences between releases. A member added in 2026 needs an `#if` or a runtime check, and
	`inventor_api_lookup` against the right XML is what finds them.
- The vendored interop for 2025 and 2026 still has to be copied from a machine that has those releases installed,
	so the first step of change 2 is a manual copy out of
	`%PROGRAMFILES%\Autodesk\Inventor <version>\Bin\Public Assemblies`.
