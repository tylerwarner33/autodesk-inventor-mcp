---
name: Add-in Isolation
description: Why the add-in runs in its own AssemblyLoadContext, and how the loader shim does it on Inventor 2025 and 2026
triggers:
  - Editing InventorMcp.AddIn.Loader, the .addin manifest template, or the bundle layout
  - Changing package references or Private settings in InventorMcp.AddIn.csproj
  - Editing the Roslyn scripting host behind inventor_eval_csharp
  - A globals cast failure, a duplicate assembly, or a load context question
globs:
  - "Source/InventorMcp.AddIn.Loader/**"
  - "**/*.addin.template"
  - "Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj"
---

## Assembly isolation is load bearing

The manifest sets `UseInventorAssemblyContext` to `0`, which loads the add-in in its own `AssemblyLoadContext`.
The element names whether to use *Inventor's* context, so `0` is the isolated mode.

This is absorbing a real conflict, not a precaution.
Measured in a live session: the process holds 273 assemblies across 19 load contexts, and
`Microsoft.CodeAnalysis` is loaded twice, 4.13.0.0 in the default context because iLogic is built on Roslyn,
and 4.14.0.0 in this add-in's context.

Consequences:

- `Autodesk.Inventor.Interop` must stay `Private=false`, so Inventor's types resolve from Inventor while the
	add-in's own packages resolve from its output folder.
- Roslyn scripting needs `InteractiveAssemblyLoader.RegisterDependency` for the globals and interop assemblies.
	Without it Roslyn loads a second copy of the add-in into its own context and the globals object fails to cast.
	`EnterContextualReflection` does not help, because the scripting host does not consult it.
- **`RegisterDependency` is only a fallback.** Roslyn's load context asks the default context first and reaches
	registered dependencies only when that fails. So the add-in must **never** be loaded into the default context,
	or scripts bind to that copy and the globals cast fails. This was measured on Inventor 2025.

## Inventor 2025 and 2026 isolate the add-in through a loader shim

Those releases ignore `UseInventorAssemblyContext`. There, the manifest names `InventorMcp.AddIn.Loader`, the only
assembly loaded into the default context. It loads the add-in from an `App\` subfolder into an isolated context,
so exactly one copy of the add-in exists in the process.

Do not replace this with a self isolating add-in that reloads its own DLL. That leaves a copy in the default
context, and the rule above then breaks the execution tools. It was tried and failed.
See `Docs/Architecture.md`.

## Checking isolation

List `AssemblyLoadContext.All`. A type asked from inside a snippet reports the snippet's binding, not the add-in's.

iLogic ships Roslyn **4.6.0.0 on 2025** and 4.13.0.0 on 2026 and 2027.
iLogic loads it lazily, so run a rule before checking the load contexts, or the collision is never tested.
