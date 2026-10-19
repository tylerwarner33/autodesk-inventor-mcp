---
name: Build and Test
description: Build commands per Inventor release, log locations, and how to drive the stdio server by hand
triggers:
  - Running dotnet build, deploying the add-in, or building the bundle
  - Verifying a change compiles or marking a coding task complete
  - An analyzer or style error in the build (ex. CA1305, MA0009, IDE1006)
  - A file lock during a build
  - Reading logs or driving InventorMcp.Server with a test client
---

## Build and test

```
dotnet build InventorMcp.slnx
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -p:AutodeskVersion=2025 -p:DeployAddIn=true
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -c Release -p:AutodeskVersion=2025 -p:DeployBundle=true
```

`AutodeskVersion` selects the Inventor release and defaults to 2027. Supported: 2025, 2026, 2027.
The add-in builds to `bin\<configuration>\<version>`, so the three releases never overwrite each other.
Run the bundle command once per release: each adds its own manifest and `Contents` subfolder to one
version independent bundle, and Inventor loads only the manifest matching its own version.
See `Docs/Architecture.md`.

## Analyzer and style errors

The build runs the .NET analyzers (`AnalysisMode` `Recommended`), `Meziantou.Analyzer`, `Roslynator.Analyzers`,
`SonarAnalyzer.CSharp`, and the style rules that `.editorconfig` gives the severity `warning`.
`TreatWarningsAsErrors` makes each one an error.

- Fix the code. The error message has a link to the rule.
- Suppress a rule only when it is wrong for this code. Put the reason on the line above. Put a suppression for one
	file in a section for that file in `.editorconfig`, not in `[*.cs]`. Put a suppression for one statement in a
	`#pragma warning disable` and `restore` pair around that statement only.
- An empty `catch` block needs a comment in it that says why the failure is safe to ignore (S108).
- Log through a `[LoggerMessage]` method in the class's `<Type>.Log.cs` file, not through `ILogger.LogWarning` and
	the like (CA1848).
- A method that returns a task ends in `Async` (MA0137). A tool's name comes from `Name` on `[McpServerTool]`, so the
	method name can change freely.
- Every text file uses LF. `.gitattributes` sets it for each checkout, and IDE0055 fails a `.cs` file with CRLF.
	Do not convert a file by hand. Stage it (`git add`), delete it, and run `git checkout -- <file>`: git writes it
	from the index with LF, and the staged changes stay.
- Give a new style rule the severity `warning` only when the code obeys it. Build first with
	`-p:TreatWarningsAsErrors=false` to count the findings.
- Build the add-in for 2025 too after a change to the add-in. It targets .NET 8, so a .NET 9 or later API breaks only
	that build (ex. `System.Threading.Lock`, which `GlobalUsings.cs` maps to `object` there).

The decisions and the measurements are in `Docs/Architecture.md`, "The build enforces the code rules".

## Continuous integration

`.github/workflows/build.yml` runs on each push to `master` and each pull request, on a hosted Windows runner with no
Inventor. It builds the solution in Release, runs the `Unit` tests, and builds the add-in for each supported release.
The runner has no interactive desktop, so the `Desktop` tests run only on a developer machine.

## Tests

```
dotnet test InventorMcp.slnx
dotnet test InventorMcp.slnx --filter-not-trait Level=Desktop
```

`Tests/InventorMcp.Server.Tests` has three levels, selected by the trait `Level`:

- `Unit` needs nothing.
- `Desktop` needs an interactive desktop, and shows real dialogs from `InventorMcp.TestDialogs.exe`. A build agent
	that runs as a Windows service has no desktop, so use the second command there.
- `Live` needs Inventor with the add-in loaded, and runs only with `INVENTORMCP_LIVE_TESTS=1`. The tests create and
	close their own parts, and open and close dialogs. Do not use Inventor while they run.

The live level, in PowerShell: `$env:INVENTORMCP_LIVE_TESTS = '1'; dotnet test InventorMcp.slnx --filter-trait Level=Live`.
In `cmd`, quote the assignment: `set "INVENTORMCP_LIVE_TESTS=1" && dotnet test ...`.

`Tests/InventorMcp.TestDialogs` is the fixture of the desktop level. `InventorMcp.TestDialogs.exe <mode>
[--close-after <ms>]` shows one modal dialog over a main window, and writes `READY <main> <dialog>` when it is
visible. The modes: `none`, `ok`, `yesno`, `okcancel`, `ilogic-like` (hidden template buttons), `wpf`, `unknown-ok`,
`advisor` and `advisor-folder` (the iLogic Security Advisor with each radio button selected), `password` (a
	user name field and a password field), and `hang` (a dialog
that does not answer).

xunit.v3 runs on Microsoft.Testing.Platform, which the root `global.json` selects. A filter that selects no test
ends with exit code 8.
If a live test fails, a dialog can stay open in Inventor. Read it with `inventor_dialogs`. Several failed tests stack
their dialogs, and only the innermost one is enabled.

## The server runs from a local feed, never from bin

The Debug build of `InventorMcp.Server` adds a tool package to `%LOCALAPPDATA%\InventorMcp\Feed` with a new
version, `0.1.0-dev.<UTC yyyyMMddHHmmss>`. Every client starts `dotnet tool exec InventorMcp.Server --prerelease
--source <feed> --yes`, which runs the newest version from its own NuGet cache folder. So a client never locks
`bin\Debug`, and a server rebuild succeeds with clients connected. `LocalFeed.targets` holds all of it.

- `-p:PackToLocalFeed=false` turns the pack off. It is off for Release by default.
- The target is incremental through `obj\Debug\LocalFeed.stamp`. A build with no change adds no package.
	Delete the stamp to force a new package.
- The same target keeps the newest `LocalFeedVersionsKept` (default 5) versions in the feed and in
	`%USERPROFILE%\.nuget\packages\inventormcp.server`. A cache folder is renamed to `_deleting-<version>` before it is
	deleted. Windows refuses the rename while a server runs from it, so a folder in use stays until a later build.
- A running session keeps its version. Restart the server in the client to run a new build.
- The repository holds no client configuration. Each client has one machine wide entry, listed in `README.md`,
	"Connecting a client". An entry anywhere that still runs `bin\Debug\InventorMcp.Server.exe` locks the build again.
- The build touches `obj\Debug\LocalFeed.stamp` only after the package exists. A Visual Studio Code workspace file
	can watch it with `dev.watch` (relative path only) to restart the server on each build.

The server targets `net10.0`, not `net10.0-windows`, because `PackAsTool` rejects a platform target framework.
An assembly level `SupportedOSPlatform("windows")` keeps CA1416 satisfied.

## Changing the add-in costs an Inventor restart

Rebuilding `InventorMcp.AddIn` requires **Inventor closed**.
Unloading the add-in through the Add-In Manager runs `Deactivate` but does not release the assembly,
because Inventor's assembly load context is not collectible, so the build still fails with a file lock.

## Logs

| Path | Contents |
| --- | --- |
| `%LOCALAPPDATA%\InventorMcp\<year>\addin.log` | Add-in lifecycle and handler failures of that release |
| `%LOCALAPPDATA%\InventorMcp\<year>\addin-startup.log` | Loader failures on 2025 and 2026, before `addin.log` exists |
| `%LOCALAPPDATA%\InventorMcp\<year>\executed-code.log` | Every snippet run through the execution tools, and each dialog the server clicked |
| `%LOCALAPPDATA%\InventorMcp\server-<date>.log` | MCP server activity, one file for each server process. Each tool call has a line with the release it used. |
| `%LOCALAPPDATA%\InventorMcp\addin.log`, `executed-code.log` | From before the folder for each release, or written before the release was known |
| `%APPDATA%\Claude\logs\mcp-server-autodesk-inventor.log` | Claude Desktop: a server that failed to start |

## Driving the server by hand

The server is stdio, so a test client must **hold standard input open**.
Closing it immediately makes the transport reach end of input before the host starts: handlers run, but responses
go nowhere. The log gives it away, with "transport completed reading messages" appearing before "Application started".

Drive it the way a client does, through `dotnet tool exec` with `DOTNET_NOLOGO=1`. Stdout then holds nothing before
the first MCP message. Running `bin\Debug\InventorMcp.Server.exe` directly also works, but that process locks the
build output again, and one whose stdin never closes blocks every rebuild until it is stopped.

## A closed Inventor session must not wedge the server

Disposing the pipe's `StreamWriter` flushes, and that throws on a dead pipe,
so `BridgeClient.CloseAsync` tolerates `IOException`. Without that, the reconnect never runs.
