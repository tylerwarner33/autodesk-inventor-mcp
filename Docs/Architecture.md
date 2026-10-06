# Architecture

Why this tool is built the way it is.
For installing and using it, see `Setup-and-Usage-Guide.md`.

## Shape

```
MCP client (Claude Code, Claude Desktop, Visual Studio, Visual Studio Code)
    | stdio, through dotnet tool exec from a local feed
InventorMcp.Server            net10.0    every MCP tool lives here
    | named pipe  InventorMcp.Bridge.<year>  one for each release, restricted to the current Windows user
InventorMcp.AddIn             net8.0-windows (2025, 2026) or net10.0-windows (2027)
    |                                    thin bridge: pipe listener, dispatch, main thread marshaling
    | in-process COM on Inventor's main thread
Inventor.exe 2025, 2026 or 2027
```

| Project | Purpose |
| --- | --- |
| `InventorMcp.Contracts` | The wire contract. Holds no Inventor interop, so the server builds without Inventor installed. |
| `InventorMcp.AddIn` | Runs inside Inventor. Owns the pipe listener and main thread dispatch only. |
| `InventorMcp.AddIn.Loader` | Inventor 2025 and 2026 only. The one assembly loaded into the default context; loads the add-in from `App\` in isolation. |
| `InventorMcp.Server` | Owns every tool. |
| `Libs/Inventor/<version>` | The vendored interop assembly per release, so a machine without Inventor can still build. |

## Decisions

### Two processes rather than one

Inventor's API is in-process COM, not a service, so something has to run inside `Inventor.exe`.
The question was whether the MCP server should run there too.

It does not, because **changing the add-in requires closing Inventor**.
The tool surface changes constantly; the bridge stabilised within a day.
Keeping the tools in a separate process means most work needs no CAD restart.

It also means the MCP connection survives Inventor closing and reopening.
Claude gets a readable "Inventor is not running" result instead of a dead server.

### A named pipe rather than a local HTTP port

The pipe is kernel backed, needs no port, and `PipeSecurity` restricts it to the account that started Inventor.
A loopback TCP port is reachable by every process and every logged on user unless a shared secret is added.

That matters here because the endpoint can execute arbitrary code inside the CAD process.

The pipe name is fixed for a release (`InventorMcp.Bridge.<year>`, ex. `InventorMcp.Bridge.2026`), so the server
connects with no discovery step. Inventor 2025, 2026 and 2027 can run at the same time, each with its own pipe.
A second Inventor of the same release cannot claim its pipe, and logs the conflict rather than competing.
The add-in reads the release from `Application.SoftwareVersion`, not from a build constant, so a wrong build input
cannot give a pipe name that does not match the process.

The add-in serves up to 16 connections (`MaxPipeInstances`), one for each server. Each MCP client starts its own
server and Claude Desktop starts two, so four clients already made six to seven servers. The ACL admits only the
current user, so the limit guards resources, not access. With every instance in use, the accept loop waits for a
connection to end before it creates another pipe, and it pauses 1 s after any failed accept.

The limit was four until 2026-09-23. Then, with four clients connected on Inventor 2025, creating a fifth instance
failed at once with "All pipe instances are busy", and the loop retried with no pause: 466,356 log lines in about
4.5 minutes, up to 2,000 a second, and a 40 MB `addin.log`. A fifth server meanwhile timed out and reported
`inventor-not-running`, which told the model to start Inventor although it was running. After the fix, 16
connections each connected in about 40 ms, the 17th and 18th got a readable message, and the loop wrote no lines.

A server that cannot connect within 3 s keeps the code `inventor-not-running`, because `inventor_start` polls on
it while Inventor loads. When an Inventor process of the selected release is running, the message says so and tells
the model not to call `inventor_start`.

Which release a server uses (`ReleaseSelection`), in order:

1. The release the session chose with `inventor_use_release` or `inventor_start`.
2. `INVENTORMCP_RELEASE` in the `env` of the MCP entry.
3. The release pipes that exist now. One pipe is used. More than one gives `release-required`.

A chosen release never falls back to another release. An automatic session keeps the release it connected to: if that
release closes and a different one is then the only pipe, the call returns `release-required` and is not sent, because
the retry after a dropped pipe could otherwise repeat a write in the other release. Each Claude session has its own server process, so one session
cannot change the release of another. The release is not a parameter of every tool, because about 50 tools would
each carry it in every call. Not selected: a pipe for each process ID (it would also allow two Inventors of one
release, but needs a discovery step), and a broker process (a third process and a new failure point).

Changed on 2026-10-05 (protocol version 2): the pipe was one fixed name, `InventorMcp.Bridge`, so only one Inventor
on the machine could host the bridge. A new server with an old add-in finds only the old name, and returns
`bridge-outdated` and not `inventor-not-running`, so that the model does not start a second Inventor. Each release
writes its logs to its own folder (`<year>\addin.log`, `<year>\addin-startup.log`, `<year>\executed-code.log`),
because the lock in `BridgeLog` covers one process, and two Inventors appended to one file. A folder makes the release
obvious, and leaves the files at the top from before this change clearly separate. The server logs stay at the top,
because a server belongs to a session and not to a release, so each tool call writes a line with the release it used.

### stdio rather than HTTP to Claude

A single user developer tool, with the pipe already carrying the trust boundary.
stdio means Claude starts the server itself: no port, no token, no URL to keep in sync.

### Clients run the server as a .NET tool from a local feed

A client holds the server process open for its whole session, and Windows does not let a build overwrite a running
executable or a loaded DLL. When clients ran `bin\Debug\InventorMcp.Server.exe`, a rebuild failed with MSB3027 while
any client was open, and with two or three clients one nearly always was.

So nothing runs from `bin\Debug`. Three locations have three jobs:

```
Source\InventorMcp.Server\bin\Debug\            the build writes here; nothing runs here
%LOCALAPPDATA%\InventorMcp\Feed\                the build adds a package here; nothing runs here
%USERPROFILE%\.nuget\packages\inventormcp.server\<version>\   NuGet extracts here; the server runs here
```

The Debug build packs a tool package with a new version, `0.1.0-dev.<UTC yyyyMMddHHmmss>`, into the feed.
Every client runs `dotnet tool exec InventorMcp.Server --prerelease --source <feed> --yes`, which selects the highest
version in the feed and runs it from its own cache folder. This is the pattern `npx`, `uvx` and `dnx` give published
servers, and Microsoft's own route for .NET MCP servers. `LocalFeed.targets` holds the build side.

- A new version for each build, because NuGet does not extract a version it already has again.
	`PackageVersion` is set rather than `VersionSuffix`, because `Directory.Build.props` sets `Version`.
- Incremental through a stamp file, not `GeneratePackageOnBuild`, which packs on every build and so would add a
	package for a build with no change.
- Old versions are removed from the feed and the cache, keeping the newest five. A cache folder is renamed before it
	is deleted, and never deleted file by file, because a running server reads its `InventorApi` XML only when a
	lookup comes in. Windows refuses the rename while a server runs from the folder, so that folder waits for a later
	build. The new name starts with `_deleting-`, because a suffix such as `.deleting` would still parse as a version.
- `dotnet`, not `dnx`: `dnx` is `dnx.cmd`, and some clients start a `.cmd` file only through `cmd /c`.
- `--prerelease` because every development version is a prerelease, `--yes` because the client owns stdin and
	nobody can answer the prompt, and `DOTNET_NOLOGO=1` because the first `dotnet` call after an SDK install writes
	its banner to stdout, which corrupts the protocol stream.
- `--source` replaces every configured source for that call, so a package on nuget.org can never be picked by
	accident. The feed is on the local disk only. Sharing the package is a separate decision.
- The server targets `net10.0`, not `net10.0-windows`, because `PackAsTool` rejects a platform target framework
	(NETSDK1146). An assembly level `SupportedOSPlatform("windows")` satisfies CA1416 instead. It is what the SDK
	generates for `net10.0-windows`. `SupportedOSPlatformVersion` does not replace it, because on `net10.0` the SDK
	ignores that property.
- The `InventorApi` XML is marked `Pack="false"`. Without that, a content item goes into the package three times
	(`content/`, `contentFiles/` and `tools/net10.0/any/`). The copy beside the tool's DLL stays.
- `PackageType` is `McpServer`, the shape of the .NET MCP server template. `PackAsTool` adds `DotnetTool` itself.

`dotnet tool exec` starts the server as a child process inside a job object that kills its processes when the job
closes, so a client that kills only the process it started still stops the server, and no server keeps a pipe
connection. That makes `DetachedProcess` more important, see below.

Measured on 2026-09-23 with the real server on SDK 10.0.401:

- The package holds each `InventorApi\<release>.xml` once, under `tools/net10.0/any/`, and `inventor_api_lookup`
	answered from the cache folder.
- Stdout held nothing before the first MCP message, on a first extraction and with the version cached.
- From process start to the answer of a fourth request (`initialize`, `tools/list`, `inventor_api_lookup`,
	`inventor_session`) took 0.8 to 2.2 s on a first extraction and 0.8 s with the version cached.
- A second build with no change added no package.
- With one session on an old version and one version kept, the build kept that cache folder
	("a server runs from it") and removed it on the first build after the session ended.
- A new session ran the newest version while an older session kept its own.
- `taskkill /F` of the `dotnet tool exec` process stopped the server too.

Considered and not selected:

- **A launcher that runs build snapshots.** It needed a second project, Native AOT, a snapshot target, atomic folder
	renames, a hash check, a cleanup, and `ProcessStartInfo.KillOnParentExit`, which only .NET 11 has. NuGet already
	gives each part: the versioned cache folder is the snapshot, and "highest version" is "newest snapshot". Only the
	cleanup is ours.
- **Renaming locked files before the build.** Windows allows a rename of a running executable, but the rename of a
	loaded DLL is not proven, antivirus can block it, and renamed files collect in `bin`. No MSBuild task does it.
- **A hot swap proxy** that restarts the server inside a session and sends `tools/list_changed`. Claude Code,
	Claude Desktop and Cursor do not reliably act on `list_changed`, so only changes inside a tool carry over, and a
	reconnect gives that already. reloaderoo shows it can be done if it is needed later.
- **`dotnet watch`.** Each client would run its own watcher on the same `obj` folder, and a restart ends the session.

Every client is configured machine wide, in its own user file, and the repository holds no client configuration.
Repository files (`.mcp.json`, `.vscode/mcp.json`) applied only with this repository open, served only people who
build the server, and made Visual Studio Code and Claude Code see a second entry of the same name beside the user
entry. So they were removed on 2026-09-23. `README.md`, "Connecting a client", holds a "Using the server" setup
against a team feed and a "Developing the server" setup against the local feed, for each client.

A team feed names the same path on every machine, so its entries need no variable. The local feed is per user, and
the clients differ in how they name it:

| Client | File | Local feed path |
| --- | --- | --- |
| Claude Code | `~/.claude.json`, user scope | `${LOCALAPPDATA}/InventorMcp/Feed` |
| Visual Studio Code | `%APPDATA%\Code\User\mcp.json` | `${env:LOCALAPPDATA}\InventorMcp\Feed` |
| Visual Studio | `%USERPROFILE%\.mcp.json` | Absolute. It does not expand variables (measured 2026-09-23). |
| Claude Desktop | `claude_desktop_config.json` | Absolute. |

A team entry keeps `--source`, so a package of the same id on nuget.org, where the id is not reserved, is never used.

A server change now costs a reconnect in the client. The add-in is unaffected: Inventor loads it into a context
that is not collectible, so rebuilding it still needs Inventor closed.

### The modelling rules travel in the initialize response

The rules that stop wrong geometry (the ViewCube face mapping, centimetres and radians, sketch relative extent
direction, `inventor_health` after a write, the time limit for each call, looking the API up first) used to reach only
a coding agent working in this repository, through its rule files (now `.agents/rules/`). A client used elsewhere,
ex. Claude Desktop, never reads them.

So the server sends them in the MCP `instructions` field (`ServerInstructions` in `Program.cs`), which every client in
scope reads. The text is an embedded resource, `ServerInstructions.md`, so it travels inside the DLL and the tool
package with no extra step, and `.agents/rules/inventor-modeling.md` points at it. It is loaded into every
conversation, so it holds only rules whose violation gives a wrong model or a lost session.

Visual Studio 2026 18.7 and later asks the user to trust the server again when its instructions change.

Measured on 2026-09-23 with "Put a 10 mm hole through the center of the top face", in clients that never read
the repository's agent instructions: every client called `inventor_orientation` first, so the instructions arrive. Following the rest varies
by model. Claude Desktop (Opus 5.5) caught its own wrong direction. Sonnet 5 in Visual Studio at first read
`DriverLost` on a hole that cut nothing as benign. GPT-5.3-Codex never called `inventor_health`.

So text alone is not enough where a model must interpret a result. `inventor_health` now explains `DriverLost` in
the feature's message, because Inventor records no failure text and an unexplained status invites a guess. With
that, Sonnet 5 corrected the direction itself. A model that never calls the tool is not reached by either, and a
server check after every write would be the only remedy. It is not built, because current models call the tool.

Visual Studio Code's `dev.watch` restarts a server on a file change, but only from a workspace file with a path
relative to the workspace. From the user file it never fired, with `${workspaceFolder}` or an absolute path, and the
documentation does not say either way. Machine wide entries therefore have no `dev`, and Visual Studio Code is
restarted by hand like the other clients.

### The add-in stays thin

The add-in holds the pipe listener, main thread marshaling, an event recorder, and a table of primitive handlers.
Everything else is in the server. This is a direct consequence of the restart cost.

Since the C# execution tool exists, a new capability is usually a canned snippet composed in the server rather than
a new bridge operation, so it costs no restart at all.

### Execution rather than a tool per operation

A tool for each modelling operation would be an endless catalogue that never covers the Inventor API.
One C# execution tool reaches every member, and the API lookup tool supplies the names.

That is why the tool surface is small and ends with `inventor_eval_csharp` rather than growing indefinitely.

A few tools are canned snippets the server composes and sends through the same execution operation:
`inventor_orientation`, `inventor_run_plugin` and `inventor_drawing_layout`. They add a named, documented entry
point for a job worth repeating, and cost no add-in rebuild. Their argument values reach the snippet only as C#
literals built by `CSharpLiteral`, never as pasted text, because a composed snippet is compiled inside Inventor.

### Script helpers travel with the snippet

The helpers for `inventor_eval_csharp` (documents, iLogic, units, user parameters, a time guard) are in
`Source/InventorMcp.Server/ScriptPrelude.csx`, not in the add-in's script globals. The server puts them before a
snippet that calls one of them, so a helper change needs no add-in rebuild. A snippet that calls none is sent as it
is. The snippet's leading `using` directives go first, and `#line` keeps its line numbers.

Measured on 2026-09-25 on Inventor 2026: `return 1;` took 43 to 49 ms to compile and run, and `return ToInches(2.54);`
with the prelude took 70 to 135 ms. The median snippet call in the usage research is 0.29 s, so the prelude stays
in the server. Move a helper to the globals only if a later measurement shows a cost that matters.

### Plugins under development load from a copy

`inventor_run_plugin` copies a plugin's build output and loads the copy into a new collectible context on every call,
so the build output is never locked and a code change needs no Inventor restart. See `Plugin-Development-Loop.md`.

### The server may start Inventor, with Explorer as its parent

`inventor_start` starts Inventor, only on an explicit call and never as a side effect of another tool.
Starting Inventor is heavy and visible, so the tool description and the `inventor-not-running` message both tell the
model to ask the user first.

The server is a child of the MCP client. On Windows a process does not die with its parent, but two things can kill
one: a job object with `KILL_ON_JOB_CLOSE`, and a tree kill such as `taskkill /T`, which walks parent process IDs.
Measured on 2026-09-22, every process checked was in a job, including Claude Code, the server, and Inventor started
from Explorer. The innermost job under Claude Code had `BREAKAWAY_OK` and `SILENT_BREAKAWAY_OK` and no
`KILL_ON_JOB_CLOSE`, but outer jobs cannot be queried, and other clients may differ.

So `DetachedProcess` starts Inventor with `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` set to the shell's Explorer.
Inventor takes Explorer's job and token and has Explorer as its parent, the same as a Start menu launch, whatever the
client does. The server still holds the process handle, so it has the process ID and exit code. No handle is
inherited, because the server's standard output carries the MCP protocol, and the environment is built fresh for
the user rather than copied from the client. If the parent attribute fails, the tool starts nothing.

Rejected: `Process.Start` and `CREATE_BREAKAWAY_FROM_JOB` both leave Inventor a child of the server, so a tree kill
reaches it. `explorer.exe "<path>"` escapes both but returns no process ID or exit code.

Verified on 2026-09-22: Inventor 2025 started this way had Explorer as its parent, survived the server's normal exit,
and survived `taskkill /T /F` of the server.

Since clients run the server through `dotnet tool exec`, the server sits inside a job object that kills its processes
when the job closes. Explorer as the parent keeps Inventor out of that job too. Do not change `DetachedProcess`
without testing again that Inventor survives a kill of `dotnet tool exec`.

The release is resolved before anything starts. The tool offers only installed releases with a deployed manifest,
because Inventor without the add-in never opens the pipe. When several qualify it asks through MCP elicitation, which
2026-07-28 carries as a Multi Round-Trip Request: the tool throws `InputRequiredException`, the client retries with the
answer, and the retry repeats every check. The client's capabilities must be read from the request's server, because
2026-07-28 declares them per request. A client without elicitation gets `version-required` and the model asks instead.

After the start the tool only connects to the pipe and sends no request, because a request can block on a sign-in or
recovery dialog, and a cancelled read would leave the stream out of step.

### The interop assembly is vendored

`Autodesk.Inventor.Interop.dll` and its documentation live in `Libs/Inventor/<version>` rather than being referenced
from Program Files, so the repository builds on a machine with no Inventor installed.
The same documentation file backs the `inventor_api_lookup` tool, which selects the file that matches the connected
session's release. A lookup answered from the 2027 file would name members that do not exist in 2025.

### Only the add-in is version bound

The server never references interop and the contract is pure data, so supporting 2025, 2026 and 2027 means building
one thin assembly three times, not three servers. `AutodeskVersion` in `Directory.Build.props` drives everything
that follows from the release:

| Inventor | Software version | Target framework | Isolation |
| --- | --- | --- | --- |
| 2025 | 29 | `net8.0-windows` | `InventorMcp.AddIn.Loader` |
| 2026 | 30 | `net8.0-windows` | `InventorMcp.AddIn.Loader` |
| 2027 | 31 | `net10.0-windows` | Inventor's own, through `UseInventorAssemblyContext` |

An add-in loads into Inventor's already running CLR, so the target framework is a hard requirement.

The add-in builds to `bin\<configuration>\<version>`. With a shared output path the three builds overwrite each
other, and a manifest then loads a build made for a different runtime, which fails inside `Activate` with a type
load error that does not name the cause.

One bundle holds every release. Each manifest carries `SupportedSoftwareVersionEqualTo`, so every installed Inventor
reads all three and loads only its own. `PackageContents.xml` is not needed: Inventor discovers `.addin` manifests
under `ApplicationPlugins` directly, as Autodesk's own Vault bundles do.

The server does not search for a session. The first Inventor to start owns the fixed pipe name, and the handshake
reports its release year. Running two releases side by side would need a versioned pipe alias; build that only when
a real need appears.

**Inventor 2023 and 2024 are out of scope.** They run on .NET Framework 4.8, which has no `AssemblyLoadContext`.
One `AppDomain` binds one version of each assembly name, so iLogic's Roslyn and the execution tools' Roslyn cannot
both load. Measure whether the execution tools can work there at all before promising support.

#### When Inventor 2025 and 2026 move to .NET 10

Autodesk plans this for later in 2026. Confirm the host runtime first, rather than going by the release note:

```csharp
Log(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
```

Then:

1. Delete the `AddInTargetFramework` rows from the `Choose` block in `Directory.Build.props`.
2. Delete the explicit `net8.0` from `InventorMcp.Contracts.csproj`, so it inherits `net10.0` again.
3. Delete the `Lock` alias in `GlobalUsings.cs`, which maps `System.Threading.Lock` to `object` below .NET 9.

Keep the vendored interop per release and keep the loader.
`UseInventorAssemblyContext` is an Inventor 2027 feature, not a runtime feature, so 2025 and 2026 still ignore it.

## Behaviours worth knowing

### The activity feed is a pull, not a push

The add-in subscribes to Inventor's own events and appends to a bounded ring buffer of 2000 entries.
Claude drains it on request from a sequence cursor.

Pushing notifications would flood the model during a tight automation loop, which is exactly when the feed matters.
Because the buffer is plain memory rather than a call into Inventor, draining it keeps working while Inventor is busy.

`Transaction.DisplayName` carries the command name behind each operation, which is what turns the feed into a
readable narration: `Create Sketch on a Face`, `Create Hole Feature`, `Edit Dimension Value`.

### Writes suppress Inventor's dialogs

A modal dialog owns Inventor's message pump, and the bridge posts its work to that pump, so an unanswered dialog
would stall every tool except the activity feed.

Write operations therefore set `SilentOperation` and restore it afterwards.
Inventor answers a suppressed dialog with its default, which is a real behaviour change, so reads never suppress.

### The server watches for dialogs from outside Inventor

`SilentOperation` does not stop every dialog (ex. the iLogic error dialog of `RunRule`). A call that opens a modal
dialog is also the call that waits on it, so only a process outside Inventor can see the dialog. The server does:
`BridgeClient` checks the main frame 3 s into a wait and then every 2 s, finds the dialogs with Win32, reads them
with the UI Automation COM API, clicks the button that `DialogSettings.json` sets for a known dialog type, and
stops the wait with `blocked-by-dialog` for any other dialog. The settings are a file, not code, so each user
chooses the answers (ex. to the iLogic Security Alert) with no change to the policy, and a user file in
`%LOCALAPPDATA%\InventorMcp\` changes them with no build.
The server reads both files one time, at start. An agent that runs as the user can write the user file, so a looser
setting has no effect until a person restarts the server.
The server checks that the process at the other end of the pipe is this user's installed Inventor before it uses the
pipe, because a process of any user can create a pipe name first (`Bridge/PipeHost.cs`).
It also checks before it sends a call, because the modal loop of an open dialog still runs the add-in's work, and a
new call would run nested inside the call that opened it.
`inventor_dialogs` and `inventor_dialog_click` use the same service with no bridge call.
The UI Automation COM interop (`Interop.UIAutomationClient`) is used in place of `System.Windows.Automation`, so the
server needs no .NET Desktop Runtime. See `Research/Blocking-Dialog-Detection.md`.

### The add-in runs in its own assembly load context

The manifest sets `UseInventorAssemblyContext` to `0`, which isolates the add-in's dependencies.

This is not precautionary. iLogic is built on Roslyn, so Inventor loads 4.6 on 2025 and 4.13 on 2026 and 2027,
while this add-in's scripting brings 4.14. Both are loaded at once in separate contexts.
Isolation is what keeps them from having to agree.

Inventor 2025 and 2026 ignore that element, so `InventorMcp.AddIn.Loader` does the same job there. It must leave no
copy of the add-in in the default context, because Roslyn resolves a script's globals type from the default context
first.

A self isolating add-in, which reloads its own DLL into a second context, was tried first and failed for exactly
that reason: its forwarding copy stays in the default context, so `inventor_eval_csharp` failed with
`InventorScriptGlobals from context "InventorMcp.AddIn" cannot be cast to InventorScriptGlobals from context "Default"`.
The loader instead sits alone at its level and loads the add-in from an `App\` subfolder, so exactly one copy of the
add-in exists in the process.

### Units

Inventor stores lengths in centimetres and angles in radians whatever a document displays.
Results report both the internal value and the display form, so a caller never has to work out which it received.

## Safety

Code execution inside a CAD process is the most dangerous capability here, and it is bounded:

- Every snippet is appended to an audit log with a timestamp and the target document.
- Execution is refused on a document with unsaved changes unless explicitly overridden.
- Roslyn loads on first use only, so a read-only session never pays for it inside Inventor's process.

A snippet that throws is reported as a result rather than failing the call, and compiler errors come back as
diagnostics with line and column, before anything reaches the model.
