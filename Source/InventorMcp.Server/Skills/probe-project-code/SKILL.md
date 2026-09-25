---
name: probe-project-code
description: How to call a project's own .NET code against the live Inventor session, when a snippet cannot reference that project's assemblies. Read it when you must test or probe a class of the user's add-in or plugin inside Inventor.
---

# Probe project code

`inventor_eval_csharp` compiles a snippet with the Inventor API and the base class library only. It cannot reference
the assemblies of the user's project. To call that code in the live session:

1. **Write a scratch entry point** in the project, or in a small project that references it: a public static method
   that takes an `InventorServer` or `Application` parameter and plain values, calls the code you want to probe, and
   returns a string or writes a log file. Keep it out of the product code, ex. in a file the user can delete.
2. **Build it.** Check that the DLL time stamp changed.
3. **Call it** with `inventor_run_plugin`: `buildOutputDirectory`, `assemblyFileName`, `typeName` and `methodName`,
   and the plain arguments by name. The tool loads a copy of the build, so the build is not locked, and each call uses
   the newest build.
4. **Read the result and the log.** Change, build and call again. Inventor stays open.
5. **Remove the scratch entry point** when you are done, or tell the user where it is.

See the `plugin-loop-cycle` skill for the rules that the called code must keep (no dialogs, no change to the
process's current folder, and a log it disposes).
