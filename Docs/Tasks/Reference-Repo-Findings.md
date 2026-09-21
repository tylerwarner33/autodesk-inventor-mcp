# Reference Repository Findings

Created: 2026-09-21

Status: **all findings applied on 2026-09-21**, except two noted below that were deliberately not taken.
Applying them is verified by build only; see the Verification section in `Inventor-MCP-Server-Plan.md` for what that does and does not prove.

| Finding | State |
| --- | --- |
| 1. Vendor the interop into `Libs` | Applied. Reference resolves from `Libs/Inventor/2027`, confirmed by `dotnet msbuild -getItem:Reference`. |
| 2. `UseInventorAssemblyContext` | Applied to the manifest template. Never loaded by Inventor yet. |
| 3. `SilentOperation` around writes | Applied through `SilentOperationScope` on set parameter, set property, and update. |
| 4. Parameter kinds and text parameters | Applied. Kind detected from the unit type; text quotes stripped; each parameter read in its own try block. |
| 5. `UnitsOfMeasure` conversion entry points | Applied as the `inventor_evaluate_expression` tool, plus `IsExpressionValid` before a numeric write. |
| 6. `StartAction` and `StartProgram` | Applied. F5 on the add-in project launches Inventor. |
| 7. `AppendTargetFrameworkToOutputPath` | Applied. `.mcp.json` no longer names a target framework. |
| 8. `AutodeskVersion` property | Applied. One property now drives the install path and the interop path. |
| 9. Release COM objects on deactivate | Applied to the application object and to both event wrappers. |
| 10. Bundle deployment | Applied as a second opt in target, `-p:DeployBundle=true`, alongside the development manifest. |
| 11. Repository conventions | Applied, except `Signed Installers/`. See below. |
| 12. Documentation conventions | Applied. Both task documents now carry a status line, and the plan carries a verification section. |

**Not taken, with reasons.**

- **`Signed Installers/` and a WiX v5 MSI.** The reference repositories ship products to customers. This is a local developer tool with no distribution decision made. An empty folder and a `Product.wxs` for an installer nobody has asked for would be cargo cult. Revisit if this is ever handed to someone else.
- **A release pipeline mirroring `Inventor-Release.yml`.** Those depend on the private `IMAGINiT.Pipelines.Templates` repository for signing and installer steps, which only make sense with the above. `Pipelines/Build.yml` instead proves the thing finding 1 unlocked: that the repository builds on an agent with no Inventor installed.

## Purpose

Survey of six existing Autodesk automation repositories for patterns worth adopting here.
Each finding names the repository it came from, what it changes in this repository, and why it matters.

## Repositories surveyed

| Repository | Relevance | What it is |
| --- | --- | --- |
| `_MyProjects/autodesk-inventor-assembly-load-context` | Highest | Reference sample for add-in dependency isolation across Inventor 2023 to 2027. Authoritative for finding 2. |
| `SprungStructures.RevitToInventor` | Highest | A real Inventor 2027 add-in built on `ApplicationAddInServer`, plus a Revit add-in |
| `Trinity.TrailerConfigurator` | Highest | Inventor plugin with parameter and occurrence utilities, desktop and Design Automation paths |
| `Cincinnati/StrobicConfigurator` | High | Inventor plugin, Design Automation bundle, local debug harness that attaches over COM |
| `Hy-Vee/HyVee.SAPFixtureCostUpdater` | Medium | Revit add-in with the mature build, deployment, and WiX installer conventions |
| `Meijer/BatchUpdateTool` | Low | Revit and AutoCAD batch tooling; repository conventions only |
| `MacDonaldMiller.BatchUpdater` | Low | Excel and data access tooling; repository conventions only |

## Findings that change this repository

Ordered by how much they matter. The first four are correctness or capability issues, not polish.

### 1. Vendor the interop assembly into `Libs/Inventor/2027`

**From:** `SprungStructures.RevitToInventor`, `Cincinnati/StrobicConfigurator`

Both repositories keep the Inventor interop assembly in the repository:

```
Libs/Inventor/2025/Autodesk.Inventor.Interop.dll
Libs/Inventor/2025/Autodesk.Inventor.Interop.xml
```

Sprung's comment states the reason plainly: "Included Library Files In Solution To Support Build In Azure Pipelines And MSBuild".

**What is wrong here now.**
`Directory.Build.props` points `InventorPublicAssembliesDir` at `C:\Program Files\Autodesk\Inventor 2027`.
The add-in therefore builds only on a machine with Inventor 2027 installed.
A build agent has no Inventor, so continuous integration cannot build this repository at all today.

**Action.**
Copy `Autodesk.Inventor.Interop.dll` and `Autodesk.Inventor.Interop.xml` into `Libs/Inventor/2027/` and point the `HintPath` there.
The `.xml` file also becomes the source for the planned `inventor_api_lookup` tool, so vendoring it serves two purposes.

Check the Autodesk licence terms before committing the assembly to a repository that may become public.

### 2. `UseInventorAssemblyContext` decides whether Roslyn can be loaded safely

**From:** `_MyProjects/autodesk-inventor-assembly-load-context` and its write up at
`https://tylerwarner.dev/assemblyloadcontext-for-inventor-2027-addins`.
Corroborated by `SprungStructures.InventorImporter.addin` and by Inventor 2027's own shipped add-ins.

Inventor ships fixed versions of common assemblies.
An add-in that references a different version of the same package gets a binding conflict.
Autodesk added built in isolation in Inventor 2027 so an add-in can load in its own `AssemblyLoadContext`.

The manifest element controls it:

```xml
<!-- Use separate AssemblyLoadContext from Inventor to prevent library version conflicts. -->
<UseInventorAssemblyContext>0</UseInventorAssemblyContext>
```

| Value | Meaning |
| --- | --- |
| `0` | Load the add-in in its own `AssemblyLoadContext` |
| `1` or omitted | Share Inventor's context |

The value reads backwards at first glance.
It names whether to *use Inventor's* context, so `0` is the isolated mode.
Inventor 2027 ships two add-ins of its own that set `0`, and one is named `IsolatedInventorAddin.bundle`.
Earlier releases ignore the element, so it is safe to leave in place.

**This is cheaper than first assessed.**
On Inventor 2027 the manifest element is the whole change.
The `AddinLoadContext`, `IsolatedApplicationAddInServer`, and `ResolveHelper` classes in the sample exist only for Inventor 2023 to 2026, which have no built in support.
This repository targets 2027 only, so none of that code is needed.

**Why this matters more here than in any of the reference repositories.**
Build order step 7 loads `Microsoft.CodeAnalysis.CSharp.Scripting` into Inventor.exe.
Roslyn brings `System.Collections.Immutable`, `System.Reflection.Metadata`, and several `Microsoft.CodeAnalysis` assemblies with it.
Inventor 2027 runs on .NET 10 and loads other vendors' add-ins into the same process, so a version clash is a live risk rather than a theoretical one.
Without isolation the failure mode is an assembly load exception inside somebody else's add-in, which is very hard to attribute back to this one.

**The interop reference must stay `Private=False`.**
`IsolatedApplicationAddInServer` states this as a requirement, and the sample's README explains why:
the interop assembly must always load from Inventor, while the add-in's own package dependencies load beside the add-in in the isolated context.
Shipping a second copy of the interop would give two managed identities for the same COM types.
`InventorMcp.AddIn.csproj` already sets `<Private>false</Private>`, so this repository is correct on that point.
It is now a load bearing setting rather than a tidiness one.

**Action.**
Add the element to `InventorMcp.AddIn.addin.template`. That is a one line change and should happen now, not at step 7.

**Open question to test, not assume.**
Roslyn scripting compiles to an assembly and loads it at runtime.
Which context that assembly lands in decides whether a script can see the Inventor interop types and the add-in's own types.
`AssemblyLoadContext.EnterContextualReflection` is the documented mechanism for steering runtime loads to a chosen context.
Whether it is needed here should be settled by a real `inventor_eval_csharp` call under isolation early in step 7, not designed around in advance.

### 3. `Application.SilentOperation` prevents the dispatcher stalling on a modal dialog

**From:** `Cincinnati/StrobicConfigurator` (`InventorDebugService.ConfigureForHeadlessOperation`), `Trinity.TrailerConfigurator` (every build command)

Trinity wraps each build command in the same pair:

```csharp
InventorUIGlobals.Application!.SilentOperation = true;
// ... build work ...
InventorUIGlobals.Application!.SilentOperation = false;
```

Strobic documents why at length.
The short version: a hidden window still raises modal dialogs, and a modal dialog with nobody to answer it blocks the run.

**Why this matters here.**
`MainThreadDispatcher` posts work to Inventor's message pump and waits.
A modal dialog owns that pump.
Every queued request then waits until a human clicks the dialog, and the `inventor_activity` feed is the only tool that keeps working, because it reads managed memory.
A style conflict or a locked design view representation is enough to trigger this.

**Action.**
Set `SilentOperation` around write operations, not globally.
Strobic's own remarks warn that a suppressed dialog takes its default answer silently, which is a real behaviour change.
Reading state should not suppress the user's dialogs; `inventor_set_parameter`, `inventor_update`, and the step 7 execution tools should.
Restore the previous value in a `finally` block rather than assuming it was false.

### 4. Parameters are not all expression driven, and text parameters read back quoted

**From:** `Trinity.TrailerConfigurator` (`ParameterUtils.UpdateUserParameter`), `Cincinnati/StrobicConfigurator` (`ParameterUtils.TryReadTextParameterValue`)

Trinity branches on the kind of parameter before writing it:

- boolean and count parameters are set through `Value`
- text parameters are set through `Value` as a string
- expression parameters are set through `Expression`
- numeric parameters are set through `Value` after `UnitsOfMeasure.ConvertUnits(value, fromUnits, UnitsTypeEnum.kDatabaseLengthUnits)`

Strobic reads a text parameter with `parameter.Expression?.Trim().Trim('"').Trim()`, and its task document records that this was verified against a real text parameter.
A text parameter's expression comes back wrapped in quotation marks.

**What is wrong here now.**

- `InventorOperations.SetParameter` always assigns `parameter.Expression`. Against a boolean or text parameter that is the wrong accessor.
- `InventorOperations.Describe` reads `parameter._Value` unconditionally. A text parameter has no numeric value, so this throws and the whole parameter list fails rather than one entry.
- `Describe` returns `Expression` raw, so a text parameter reports `"Bottom"` with the quotation marks included.

**Action.**
Branch on `ModelValueType` or catch per parameter so one bad entry cannot fail the list.
Strip the surrounding quotation marks from a text parameter's expression and report the kind in `ParameterInfo`, so the model knows which accessor a write will use.

### 5. `UnitsOfMeasure` has better conversion entry points than the one in use

**From:** `Trinity.TrailerConfigurator`

Two methods appear throughout the Trinity plugin and are a better fit than `GetStringFromValue`:

- `ConvertUnits(value, fromUnitsType, toUnitsType)` converts between unit types directly, ex. into `kDatabaseLengthUnits`
- `GetValueFromExpression(expression, unitsType)` parses an expression string into a number

`GetValueFromExpression` is worth adding to the bridge as its own operation.
It would let the model check what `"Width / 2 + 10 mm"` evaluates to before writing it, which is exactly the loop being debugged.

### 6. Debug the add-in by launching Inventor from the project

**From:** both Inventor add-in projects, and the Revit one

```xml
<OutputType>Library</OutputType>
<StartAction>Program</StartAction>
<StartProgram>$(ProgramFiles)\Autodesk\Inventor $(AutodeskVersion)\Bin\Inventor.exe</StartProgram>
```

This makes F5 on the add-in project start Inventor with the debugger attached.
Given that nothing in this repository has been run against Inventor yet, this is the single change that most shortens the first debugging session.

### 7. `AppendTargetFrameworkToOutputPath` stabilises the `.mcp.json` command path

**From:** `Cincinnati/StrobicConfigurator`, `Hy-Vee/HyVee.SAPFixtureCostUpdater`

Both set `<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>`.

`.mcp.json` currently hard codes:

```
Source\InventorMcp.Server\bin\Debug\net10.0\InventorMcp.Server.exe
```

That path breaks on any target framework change.
With the property set it becomes `bin\Debug\`, which is stable.

### 8. An `AutodeskVersion` property instead of a hard coded path

**From:** every Autodesk repository surveyed

All of them declare `<AutodeskVersion>2025</AutodeskVersion>` in `Directory.Build.props` and compose paths from it.
This repository hard codes `Inventor 2027` in two places.
One property makes the eventual move to Inventor 2028 a one line change, and it reads better in the deploy target's message.

### 9. Release COM objects explicitly on deactivate

**From:** `SprungStructures`, `Trinity`, `Cincinnati`

All three call `Marshal.ReleaseComObject` on the Inventor application before `GC.Collect()`:

```csharp
Marshal.ReleaseComObject(inventorApplication);
GC.Collect();
GC.WaitForPendingFinalizers();
```

`StandardAddInServer.Deactivate` already collects but only nulls `_inventor`.
`ActivityRecorder` also holds `ApplicationEvents` and `TransactionEvents` runtime callable wrappers, which should be released the same way after the handlers are detached.
Leaving them can keep Inventor's process alive after the user closes it.

### 10. Deployment as a bundle, not a loose manifest

**From:** `_MyProjects/autodesk-inventor-assembly-load-context`, `SprungStructures`, `Cincinnati`, `Hy-Vee`

The isolation sample documents the four candidate install locations in one place, which is the clearest statement of the options:

| Location | Scope |
| --- | --- |
| `%ProgramData%\Autodesk\Inventor <version>\Addins` | Per machine, version dependent, Inventor 2023 and earlier |
| `%ProgramFiles%\Autodesk\Inventor <version>\Bin\Addins` | Per machine, version dependent, Inventor 2024 and later |
| `%AppData%\Autodesk\Inventor <version>\Addins` | Per user, version dependent. What both samples use. |
| `%AppData%\Autodesk\ApplicationPlugins` | Per user, version independent |

This repository already deploys to the per user, version dependent folder, which matches the samples.

A bundle also needs a `PackageContents.xml` beside `Contents\`, whose `ComponentEntry ModuleName` points at the `.addin` file and whose `RuntimeRequirements Platform` must be `Inventor`.

Sprung deploys an `ApplicationPlugins` style bundle:

```
%AppData%\Autodesk\Inventor 2027\Addins\<Project>.bundle\
	PackageContents.xml
	Contents\<the built output>
```

Their post build target copies the whole output directory rather than pointing a manifest at the build folder.

This repository's `DeployAddInManifest` target instead writes a manifest that points straight at `bin\Debug`.
That is better for development, because a rebuild needs no redeployment, and it is the right default for now.
The bundle layout is what an installer should produce later.
Both patterns are worth keeping, selected by configuration.

### 11. Repository conventions this repository is missing

**From:** all six repositories

| Convention | Present in | Notes |
| --- | --- | --- |
| `.gitattributes` | all six | This repository has none. `git add` emitted a line ending warning for every file added. |
| `TreatWarningsAsErrors` | Strobic, Hy-Vee | Worth enabling now, while the code base is small. |
| `DebugType embedded` | Strobic, Hy-Vee | Puts symbols in the assembly, which matters for an add-in loaded from a build folder. |
| `PathMap` and `DeterministicSourcePaths` | Strobic | Release builds only. |
| `NuGetAuditMode all` | Strobic | Audits transitive packages, which is where Roslyn's dependencies will arrive. |
| `Version` property | Strobic, Trinity | No version is declared here at all. |
| `Pipelines/*-Release.yml` | all six | Azure Pipelines definitions. Blocked until finding 1 is applied, since an agent cannot build against Program Files. |
| `Signed Installers/` | four repositories | For the WiX v5 MSI path, if this is ever distributed. |

### 12. Documentation conventions

**From:** `Cincinnati/StrobicConfigurator`, `Meijer/BatchUpdateTool`, `Trinity.TrailerConfigurator`

`Docs/Tasks/` already matches the house convention.
Two habits in the Strobic task documents are worth copying:

- a **Status** line at the top stating what is implemented, what is verified, and the date of verification
- a **Verification** section naming the runs that proved it, including the ones that failed and what they corrected

The plan document here already records a status per build order step.
It should gain a verification section once anything runs against Inventor.

## Patterns deliberately not adopted

- **Dependency injection inside the add-in.** Sprung and Trinity build a full `ServiceCollection` in `Activate`. This add-in is meant to stay thin, and a container inside Inventor's process is another set of assemblies to isolate. The server already uses one.
- **Ribbon customisation.** Every reference add-in adds ribbon buttons. This bridge has no user interface by design; its only surface is the pipe.
- **WPF and MVVM.** Same reason.
- **Design Automation packaging.** Strobic and Trinity both publish an app bundle to Design Automation. This server is inherently local, because it exists to watch an interactive session.

## Suggested order of work

Before step 7 of the build plan:

1. Add `UseInventorAssemblyContext` to the manifest template (finding 2). One line, and it gates the Roslyn work.
2. Vendor the interop into `Libs/Inventor/2027` and repoint the `HintPath` (finding 1)
3. Fix the parameter accessor and text parameter handling (finding 4)
4. Add `StartAction` and `StartProgram` for debugging (finding 6)
5. Add `.gitattributes`, `AutodeskVersion`, `AppendTargetFrameworkToOutputPath`, and `Version` (findings 7, 8, 11)

During step 7:

6. Prove a Roslyn script can reach the Inventor interop under isolation before building the tool around it (finding 2, open question)
7. Wrap write and execution operations in `SilentOperation` with a `finally` restore (finding 3)
8. Release the COM wrappers on deactivate (finding 9)

Later:

9. `GetValueFromExpression` as a bridge operation (finding 5)
10. Bundle deployment and a release pipeline (findings 10, 11)
