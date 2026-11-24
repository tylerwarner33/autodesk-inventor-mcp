# Architecture

Why this tool is built the way it is.
For installing and using it, see `Setup-and-Usage-Guide.md`.

## Shape

```mermaid
flowchart LR
    client["<b>MCP client</b><br/>Claude Code, Claude Desktop,<br/>Visual Studio, VS Code"]
    server["<b>InventorMcp.Server</b><br/>every MCP tool"]
    addin["<b>InventorMcp.AddIn</b><br/>thin bridge"]
    inventor["<b>Inventor.exe</b><br/>2025, 2026, 2027"]

    client -- stdio --> server
    server -- "named pipe<br/>InventorMcp.Bridge.v3.#lt;year#gt;.#lt;process#gt;" --> addin
    addin -- "COM, main thread" --> inventor
```

The client starts the server (`net10.0`) through `dotnet tool exec` from a local feed. Each Inventor process has its
own pipe, open only to the current Windows user. The add-in targets `net8.0-windows` on 2025 and 2026, and
`net10.0-windows` on 2027. It holds only the pipe listener, the dispatch, and the marshaling to Inventor's main thread.

Two protocols are on the two links:

- **Client to server:** the [Model Context Protocol](https://modelcontextprotocol.io/specification/latest) (MCP),
  through the C# MCP SDK. Each MCP message is a [JSON-RPC 2.0](https://www.jsonrpc.org/specification) message (see
  the MCP [base protocol](https://modelcontextprotocol.io/specification/latest/basic)).
- **Server to add-in:** MCP too, as a second and separate session, through the same SDK. Each bridge operation is a
  custom MCP method (ex. `parameters.evaluate`), and the version is pinned and is in the pipe name (see "The bridge
  speaks MCP"). The client's session does not go into Inventor: the tools stay in the server (see "Two processes
  rather than one").

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

The pipe name has the protocol version, the release and the process id:
`InventorMcp.Bridge.v<protocol>.<year>.<process id>`, ex. `InventorMcp.Bridge.v3.2026.12345`. Inventor 2025, 2026 and
2027 can run at the same time, and so can two Inventor processes of one release: each has its own pipe. The server
learns the protocol version, the release and the process from the pipe list, with no connect and no first message.
A first message is not possible anyway, because a dialog at the start of Inventor can block it (see "The server waits
for Inventor, not the model"). The add-in reads the release from `Application.SoftwareVersion`, not from a build
constant, so a wrong build input cannot give a pipe name that does not match the process.

Three decisions about the pipe, from `Docs/Research/Task-Analysis.md`, "Bridge pipe options", and probes on
Windows 11 with .NET 10:

- **A pipe for each process, not one for each release.** Before, two processes of one release served one name: the
  second process added instances with no error (the ACL gives the user `CreateNewInstance`), a client got the instance
  that had listened for the longest time, and `GetNamedPipeServerProcessId` gave the first process for both. So the
  servers were spread over both Inventors in an order that nobody controlled, and the host check and the dialog watch
  could look at the wrong process. A name with the process id removes the case. The discovery step that this needs was
  already paid, because the server lists the pipes to find the releases.
- **`FirstPipeInstance` on the first instance.** It makes the create fail when the name exists, so a program that took
  the name of a process cannot become its listener. It protects only the call that has it. A different Windows user
  cannot hold the name, because it has the id of this process. After an unload and a load of the add-in in one
  process, a connection of the old server can still hold the name, so the claim is tried 5 times, 1 s apart, before
  the server stops.
- **A deny entry for the `NETWORK` SID (S-1-5-2).** .NET never sets `PIPE_REJECT_REMOTE_CLIENTS`, and
  `PipeOptions.CurrentUserOnly` cannot be used with an explicit `PipeSecurity`. A network logon has this SID in its
  token and an interactive logon has not. A client from another computer needs the credentials of the user, so this is
  a second barrier and not the first. A server on this computer that runs with a network logon (ex. through SSH or
  WinRM) is refused too, and its message says so.

The add-in serves up to 16 connections (`MaxPipeInstances`), one for each server. Each MCP client starts its own
server and Claude Desktop starts two, so four clients already made six to seven servers. The ACL admits only the
current user, so the limit guards resources, not access. With every instance in use, the accept loop waits for a
connection to end before it creates another pipe, and it pauses 1 s after any failed accept.

The limit was four until 2026-09-23. Then, with four clients connected on Inventor 2025, creating a fifth instance
failed at once with "All pipe instances are busy", and the loop retried with no pause: 466,356 log lines in about
4.5 minutes, up to 2,000 a second, and a 40 MB `addin.log`. A fifth server meanwhile timed out and reported
`inventor-not-running`, which told the model to start Inventor although it was running. After the fix, 16
connections each connected in about 40 ms, the 17th and 18th got a readable message, and the loop wrote no lines.

A connect that times out gives the same `TimeoutException` for a pipe with every instance in use and for a pipe that
does not exist. So `ConnectionDiagnosis` examines the machine after the 3 s wait, not before, because the pipe can
appear during the wait. It reads the pipe list (never `File.Exists` on a pipe name: it connects, and uses an
instance), the Inventor processes of the release, and the manifest of the add-in:

| Pipe of the release | Inventor of the release | Cause | Code |
| --- | --- | --- | --- |
| Exists | Any | Every connection is in use | `bridge-connections-in-use` |
| No | None | Inventor does not run | `inventor-not-running` |
| No | Runs, no manifest | The add-in is not deployed | `inventor-not-running` |
| No | Runs, newest process under 120 s old | The add-in is loading | `inventor-not-running` |
| No | Runs, each process over 120 s old | The add-in is not loaded | `inventor-not-running` |

Only a full pipe has its own code, because a wait cannot free a connection, and a log can count the case. The other
causes keep `inventor-not-running`, because `inventor_start` and `inventor_session` with `waitSeconds` poll on it while
Inventor loads. Each message tells the model the next step, and none but "Inventor does not run" lets it call
`inventor_start`. The 120 s separates "loading" from "not loaded": the measured starts took 34 s to 92 s. A process
with no start time counts as old. A pipe whose ACL refuses the account (ex. the pipe of a different Windows user)
throws `UnauthorizedAccessException` at once, and the result is `untrusted-bridge-host`.

When a connected pipe drops, the client reads the process id before the close (the close clears it), and the log says
if the process still runs (the add-in stopped serving, or the connection broke) or not (Inventor closed or crashed).
The retry does not change.

Which release a server uses (`ReleaseSelection`), in order:

1. The release the session chose with `inventor_use_release` or `inventor_start`.
2. `INVENTORMCP_RELEASE` in the `env` of the MCP entry.
3. The release pipes that exist now. One release is used. More than one gives `release-required`.

Within a release, one process is used. Two processes of the release give `instance-required` with the id, the start
time and the main window title of each process, because a user knows an Inventor by its window and not by its id.
`inventor_use_release` with a `processId` chooses one. `inventor_session` lists the processes.

A chosen release never falls back to another release. An automatic session keeps the release it connected to: if that
release closes and a different one is then the only pipe, the call returns `release-required` and is not sent. A
session also keeps the process it connected to while that process hosts a bridge. When the process is gone, the retry
after a dropped pipe returns `instance-required` and is not sent, because it sends the same request again, and it must
not repeat a write in other documents. A new call goes on to the only process of the release that is left, ex. after
the user restarted Inventor, and asks only when more than one is left. A lock for every call made each restart of
Inventor a question to the user, with one Inventor to choose. After the connect, the process id in the name is
compared with `GetNamedPipeServerProcessId`, and a
different id refuses the pipe with `untrusted-bridge-host`. Each Claude session has its own server process, so one
session cannot change the release of another. The release is not a parameter of every tool, because about 50 tools
would each carry it in every call. Not selected: a broker process (a third process and a new failure point).

Only the pipes in the Windows session of the server count, because an Inventor of another session cannot be the one
that this user means.

A name of another protocol version gives a message and not a connect. A pipe of protocol 2 or 1 (`InventorMcp.Bridge.<year>`
or `InventorMcp.Bridge`) gives `bridge-outdated`, and a name with a higher version gives `version-mismatch`, which tells
the user to restart the MCP client so it starts the new server. A session that started before the update keeps its old
server, which looks for the old name, finds none and reports `inventor-not-running`; a restart of the client corrects it.
A second pipe with the old name that answers "restart the client" was not selected: more add-in code for a case that
ends with the next restart.

Changed on 2026-11-19 (protocol version 3): the messages on the pipe are MCP, in place of the line protocol of this
repository (see "The bridge speaks MCP"). An add-in built before that does not complete the handshake, and the server
reports `bridge-outdated`.

Changed on 2026-11-16 (protocol version 3): the pipe name has the protocol version and the process id, as described
above. A new add-in is needed for each release, and Inventor must restart to load it. Two processes of one release
share the log folder of the release, so each `addin.log` line has the process id in brackets, and `RollingFile`
tries an append again (5 times, 20 ms apart) when another process has the file open.

Changed on 2026-10-05 (protocol version 2): the pipe was one fixed name, `InventorMcp.Bridge`, so only one Inventor
on the machine could host the bridge. A new server with an old add-in finds only the old name, and returns
`bridge-outdated` and not `inventor-not-running`, so that the model does not start a second Inventor. Each release
writes its logs to its own folder (`<year>\addin.log`, `<year>\addin-startup.log`, `<year>\executed-code.log`),
because the lock in `BridgeLog` covers one process, and two Inventors appended to one file. A folder makes the release
obvious, and leaves the files at the top from before this change clearly separate. The server logs stay at the top,
because a server belongs to a session and not to a release, so each tool call writes a line with the release it used.

No code uses `GetActiveObject` or `GetObject`. With several releases open, they return the last Inventor that
registered with COM, not the one that hosts the bridge pipe. Do not add them.

### The bridge speaks MCP

The add-in serves each pipe connection as an `McpServer` of the C# SDK (`ModelContextProtocol.Core`), and
`BridgeClient` is an `McpClient`. Each bridge operation is a custom JSON-RPC method, registered as an
`McpServerRequestHandler` (experimental in the SDK, `MCPEXP002`). The decision and the measurements are in
`Docs/Research/Task-Analysis.md`, "Bridge protocol decision and JSON-RPC migration", and `Docs/Research/Mcp-Over-Pipe-Spike.md`, with live
checks in Inventor 2025, 2026 and 2027.

Why not a protocol of this repository: MCP is JSON-RPC 2.0 already, the server uses the SDK already, and the SDK gives
the match of a response to its request, the cancel, and the errors with a code, which the old line protocol either
did by hand or did not have. The SDK assemblies load in the isolated context of the add-in (see "The add-in runs in
its own assembly load context"). On 2027, another add-in loads `ModelContextProtocol.Core` 1.2.0, and the add-in's
2.4.0 loads beside it with no conflict.

This does not reverse "Two processes rather than one": the client's MCP session ends in the server, and the pipe has a
second, internal session whose only methods are the bridge operations.

The rules on the pipe:

- **A pinned version.** Both sides set `ProtocolVersion` to `BridgeProtocol.McpVersion` (`2026-07-28`), and the add-in
  refuses any other. Without the pin, the version follows the two SDK versions that meet, and the add-in is deployed
  far less often than the server. Change the MCP version only when a newer one gives the bridge something that it
  needs, or when an SDK release stops supporting the pinned one. Then change `BridgeProtocol.McpVersion` and
  `BridgeProtocol.Version` in the same pull request (ex. pipe `v3` to `v4`): an add-in of the old version keeps the old
  pipe name, and the server reports `bridge-outdated`, so the two versions never drift apart. Watch the MCP
  specification (https://modelcontextprotocol.io/specification/latest) and the SDK release notes
  (https://github.com/modelcontextprotocol/csharp-sdk/releases) for the removal of the pinned version.
- **A result is an object.** The value of an operation is in its `value` member, and no value is an empty object
  (`BridgeResult`). An MCP result must be an object, and SDK 2.4.0 writes a null result with no `result` member, so its
  client never completes the call.
- **A failure has the bridge code.** JSON-RPC code `-32000`, with `code`, `message` and `detail` in `error.data`. The
  SDK client puts "Request failed (remote): " before `error.message`, so the server reads the message from
  `error.data.message`. Code `-32601` (a method that the add-in does not know) gives `unknown-operation`, with a
  message that tells the user to deploy the add-in again. A new operation does not change the protocol version.
- **No MCP method names.** `ping` is an MCP method, and the SDK answers it before a handler of the add-in can, so the
  bridge's ping is `bridge.ping`. A new operation must not be a name of MCP either (ex. `initialize`, or a name with a
  slash).
- **One request at a time for each connection.** The server sends one main thread call at a time (`_gate` of
  `BridgeClient`), and the add-in runs the requests of one connection one at a time, in order. Both are needed: the SDK
  runs the requests of one connection at the same time, and a cancel frees the gate of the server before the
  cancelled request ends in the add-in. Without the queue of the add-in, the next call could run nested in the modal
  loop of a dialog that the cancelled call opened (see "The server watches for dialogs from outside Inventor").
- **Two operations do not wait.** `bridge.ping` and `activity` never use the main thread
  (`BridgeOperations.RunsWithoutMainThread`). They skip the gate, the queue and the dialog watch, so `inventor_activity`
  answers while a long call runs. The add-in registers them through `RegisterWithoutMainThread`, from a static method
  with no Inventor object, and the dispatcher refuses an operation that is registered through the wrong method. The
  connect and the reconnect of `BridgeClient` have their own lock, because the gate no longer guards every use of the
  connection.
- **A cancel stops only a call that still waits.** The SDK sends the cancel of a tool call to the add-in, and a
  request that still waits in the queue or for the main thread then never runs. A request that already runs ends
  normally, and a cancel can reach the add-in after the main thread is free (2 of 8 runs under load in the spike), so a
  tool must read the model back after a cancelled write. A stop of the wait on `blocked-by-dialog` sends no cancel,
  because the call that opened the dialog already runs.
- **The handshake does not use the main thread.** It answers while Inventor starts and under a dialog. An add-in that
  does not complete it in 10 s speaks another protocol, and the server reports `bridge-outdated`.

The SDK behaviors that the add-in works around are listed in `Docs/Research/Mcp-Over-Pipe-Spike.md`, "SDK behavior to
check again on each SDK update". `BridgeEndToEndTests` tests each of them with the real listener and the real client,
so an SDK update that changes one fails there.

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

The Debug build packs a tool package with a new version, `<version>-dev.<UTC yyyyMMddHHmmss>`, into the feed.
The version is `<Version>` in `Directory.Build.props`, so the development version follows each release.
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

### Longer knowledge travels as skills, and a tool description names its skill

Text that a model needs only for some tasks (the snippet host, the tools for iLogic, drawings and files, and workflows
such as a safe template edit) is in `Source/InventorMcp.Server/Skills/<name>/SKILL.md`, in the Agent Skills format.
The Inventor API facts are not here: see "The Inventor API facts live in plugins" below. Each place that holds
knowledge has a different context cost:

| Place | Loaded | Holds |
| --- | --- | --- |
| `ServerInstructions.md` | In every session, by every client | Only rules whose violation gives a wrong model or a lost session, and one line that names the skills |
| A tool description | Claude Code loads a tool schema only when the tool is needed | A trap of that one tool, and the name of its skill |
| A skill | Only when the model reads it | Everything else |

So the `Parameter.Value` cast and "calls run one at a time" are in tool descriptions or a skill, not in the
instructions. The descriptions of `inventor_eval_csharp`, `inventor_ilogic_rule_set`, `inventor_test_copy`,
`inventor_run_plugin`, `inventor_drawing_layout` and `inventor_auto_balloon` name their skill.

The skills are embedded resources with a `LogicalName` that keeps the path, so they travel in the tool package with no
step to install for each client. Two routes serve the same bytes, which `SkillCatalog` reads once:

- `inventor_skill`, because every client supports tools.
- The Skills over MCP extension (`io.modelcontextprotocol/skills`), through `ModelContextProtocol.Extensions.Skills`
	from version 2.4.0 of the C# SDK. Each skill is at `skill://inventor/<name>/SKILL.md`. The prefix keeps the names
	apart from the skills of other servers. `McpServerSkillPackage.Create` builds each package from the embedded bytes
	and computes the SHA-256 manifest from them. The directory loaders of the SDK do not apply, because the server runs
	as a .NET tool and the skill folder is not on disk.

The extension API is experimental (`MCPEXP005`), so only `Services/SkillCatalog.cs` uses it, and `.editorconfig`
suppresses the diagnostic for that file only. A read of a `SKILL.md` through either route counts in
`inventor.skill.reads`. `inventor_skill` stays because no client of this server supports the extension on the client
matrix of 2026-11-09. `Docs/Tasks/Skills-Over-Mcp-Plan.md` holds the step that removes it.

`.agents/rules/inventor-interop.md` and `inventor-modeling.md` keep only the facts about this repository and point to
the skills, so no two copies drift apart.

The instructions tell a model to read `snippets` before its first snippet, because a list of the skills alone did not
make a client read one. Measured on 2026-09-25 in Claude Code sessions outside this repository, with the skill that
was then named `interop` and held the API traps: with only the list, a snippet task read no skill and took 3
snippets. With the instruction and a tool description that named the skill, it read `interop` and took 1 snippet.
Claude Desktop (Opus 5.5) also read `interop` from the tool description. Visual Studio (Copilot, Sonnet 5) gave the
correct answer with no visible skill read. See `Research/Usage-Findings-And-Knowledge-Delivery.md`, "Checks of the
skill delivery".

### The Inventor API facts live in plugins, and the server keeps its own steps

The topic skills `interop`, `modeling`, `drawings`, `ilogic` and `projects-and-files` held two kinds of text: facts of
the Inventor API, which are true for any code that drives Inventor, and steps with this server's tools. From version
2.0.0 the facts are in the plugins of the [autodesk-agent-skills](https://github.com/tylerwarner33/autodesk-agent-skills)
repository (marketplace `tylerwarner-autodesk`), and only the steps stay here:

| Was | Facts, in a plugin | Steps, in this server |
| --- | --- | --- |
| `interop` | `inventor-api-traps` (`inventor-api`) | `snippets` |
| `modeling` | `inventor-modeling` (`inventor-api`) | `verify-a-write` |
| `drawings` | `inventor-drawings` (`inventor-api`) | `drawing-checks` |
| `ilogic` | `inventor-ilogic` (`inventor-ilogic`) | `ilogic-tools` |
| `projects-and-files` | `inventor-projects-and-files` (`inventor-data`) | `file-tools` |

Why:

- The facts then work with any Inventor MCP server, an add-in, or a Design Automation plugin, and not only through this
	server. A test in the plugin repository fails when a plugin skill names a tool or a script helper of this server.
- Claude reads a plugin skill from the local disk, with no approval per skill, and on every Claude surface.

The cost, accepted with the change:

- A client without the plugins does not get the API facts. Claude plugins do not reach Visual Studio or Visual Studio
	Code with Copilot. The instructions and the `inventor_eval_csharp` description still name `inventor-api-traps`, so a
	client with the plugins reads it, and `snippets` keeps the server facts that every client needs.
- `inventor.skill.reads` counts only the skills that this server serves. A read of a plugin skill happens in the
	client, so the server cannot see it. The dashboard shows the new names from 2.0.0, and the old names before.
- The facts and the server can change at different times. A server skill names only the API facts that its steps need.

`SkillCatalogTests` lists the plugin skills that a server skill can name, and fails when a server skill names one
without its plugin.

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

Because most tools are snippets, the compile is on the path of almost every call. `ScriptCache` in the add-in
compiles a snippet on the thread of the bridge, so Inventor's main thread does only the run, and keeps the 64
compiled snippets that were used most recently, by their text. A compiled script is an assembly that Inventor cannot
unload, so without the cache each call of the same tool added one. A snippet with other argument values is other
text, and compiles again. A compiled snippet that runs again keeps the values of its own `static` fields.

### Script helpers travel with the snippet

The helpers for `inventor_eval_csharp` (documents, iLogic, units, user parameters, a time guard) are in
`Source/InventorMcp.Server/ScriptPrelude.csx`, not in the add-in's script globals. The server puts them before a
snippet that calls one of them, so a helper change needs no add-in rebuild. A snippet that calls none is sent as it
is. The snippet's leading `using` directives go first, and `#line` keeps its line numbers.

Measured on 2026-09-25 on Inventor 2026: `return 1;` took 43 to 49 ms to compile and run, and `return ToInches(2.54);`
with the prelude took 70 to 135 ms. The median snippet call in the usage research is 0.29 s, so the prelude stays
in the server. Move a helper to the globals only if a later measurement shows a cost that matters.

### Auto balloon is a composed snippet with its own placement rules

The Inventor API has no auto-balloon member, and Inventor's own Auto Balloon command needs the user to select the
view. So `inventor_auto_balloon` adds each balloon with `Sheet.Balloons.Add`, in one transaction (one Undo step).

- **It replaces every balloon of the view**, also one added by hand. The user asked for this. Balloons of other views
	stay.
- **It selects the side of each part by default**, from the part's outline. `sides` limits the sides. A side is used
	only when the space to the sheet border, or to the next view, is at least the offset plus the balloon radius,
	because the first live run put balloons outside the sheet.
- **It walks every sub-assembly, then removes repeats.** Inventor gives the item number only after `Balloons.Add`,
	so the tool adds the balloon, reads `ItemNumber`, deletes a repeat, and packs the other balloons again.
- **It keeps arrowheads clear of other parts.** On a straight curve it tries 0.5, 0.3, 0.7, 0.15 and 0.85 of the
	length, and takes the best point at least 0.1 in (the clearance of `inventor_drawing_layout`) from the other parts.
	When no point is clear, it names the part in `unclearArrowheads`.
- **It stops after 6 s and aborts the transaction**, because a snippet runs on Inventor's main thread. The view then
	has no change, and the result is `too-slow`.

The rules come from two production implementations and from live runs on Inventor 2025.4. See
`Research/Auto-Balloon.md`.

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

### The server waits for Inventor, not the model

`inventor_start` waits up to 45 s, and a start with a sign-in or recovery dialog takes longer. So `inventor_session`
takes `waitSeconds` (0 to 45), and polls the bridge every 2 s before its normal report. It never starts Inventor.
The wait uses the same connection as every other call, so it keeps the release of the session and refuses a pipe
host that is not this user's Inventor. It sends no request until the pipe connects, so a dialog at startup cannot
block it. It ends when the bridge answers, and never runs longer than `waitSeconds`: each connection attempt and each
pause gets only the time that is left. A client limit of 60 s for one call is common, so the limit of 45 s leaves
room for the session report after the wait.

Before this, the messages said "call inventor_session later" and "retry shortly", but no tool could wait. On
2026-10-07 a model then wrote its own wait loop in Git Bash, which cannot list named pipes, and the loop never saw
Inventor start. A pipe name in a shell listing is also a weaker signal than a connection: it does not prove that the
add-in accepts calls or that the pipe host is this user's Inventor. So the server instructions tell the model to wait
with `waitSeconds` and never with a shell loop.

Since clients run the server through `dotnet tool exec`, the server sits inside a job object that kills its processes
when the job closes. Explorer as the parent keeps Inventor out of that job too. Do not change `DetachedProcess`
without testing again that Inventor survives a kill of `dotnet tool exec`.

The release is resolved before anything starts. The tool offers only installed releases with a deployed manifest,
because Inventor without the add-in never opens the pipe. When several qualify it asks through MCP elicitation, which
2026-07-28 carries as a Multi Round-Trip Request: the tool throws `InputRequiredException`, the client retries with the
answer, and the retry repeats every check. The client's capabilities must be read from the request's server, because
2026-07-28 declares them per request. A client without elicitation gets `version-required` and the model asks instead.

Not yet seen to work in a client. On 2026-10-05, `inventor_start` with no version in Claude Code 2.1.289 returned the
input request in 32 ms (server log, "selection automatic"). The client showed no form and sent no retry, and the call
stayed open in the client for 1 hour. The next call, with the version given, started Inventor. So find whether the
client declared elicitation for that request before the check in `Docs/Tasks/Live-Verification.md` runs again.

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

The server does not search for a session. Each Inventor process has its own pipe name, and the server connects to the
pipe of the selected release and process (see "A named pipe rather than a local HTTP port"). The handshake reports the
release year.

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

### Telemetry is optional, and goes through a Collector

The server can export traces and metrics over OTLP, for questions that the file log answers only by hand: the time
and the failures of each tool, the main thread time of a snippet, the skill reads, the connections to each release,
and the dialogs. The runbook is `Docs/Telemetry.md`. The checks and the measurements are in
`Docs/Research/OpenTelemetry-Findings.md`.

1. **The OpenTelemetry .NET SDK, with the .NET APIs.** The code calls `ActivitySource` and `Meter`
	(`Telemetry/ServerTelemetry.cs`). Only `Telemetry/TelemetryRegistration.cs` knows about OpenTelemetry.
2. **The span of the MCP library for each tool call.** The library starts `tools/call <tool>`, and
	`Activity.Current` in the call tool filter is that span. The server adds its tags to it, and starts its own span
	only for each bridge round trip.
3. **OTLP only, to a Collector.** Each server knows one endpoint. A long running Collector receives the push, applies
	the second guard, and pushes OTLP on to Tempo and to Prometheus. Prometheus takes OTLP, not a scrape, because OTLP
	has the start time of each counter: each server is a short stdio process, and a scrape lost the first calls of each.
4. **Off by default, with the standard variables.** The SDK is registered only when `OTEL_EXPORTER_OTLP_ENDPOINT` is
	set and `OTEL_SDK_DISABLED` is not `true`. Claude Code passes `OTEL_*` values from an entry's own `env`. The server
	sets defaults for the export timeout (500 ms, so the process ends within 2 s with no Collector) and the metric
	interval (10 s, so a killed server loses little).
5. **Serilog stays.** The file log always exists. With telemetry on, each line ends with its trace ID.
6. **Nothing on stdout.** No console exporter.
7. **The repository holds config, not data.** `Tools/Telemetry` holds the configs, the dashboard and the start
	script. The programs and the data are in `%LOCALAPPDATA%\InventorMcp\Telemetry` or in container volumes.
8. **Two guards for privacy.** `AllowlistProcessor` removes each span tag that is not in `TelemetryTags.Allowed`, and
	the span status text, where the library puts the result of a failed call. A metric view keeps only the allowed
	tags. The Collector applies the same lists again, and a test fails when the lists differ. A value from outside the
	server (a bridge error code, an exception type, a dialog action, a tool or method name) is sent only when it is a
	known value. The library's span name is made again from known values.
9. **Trace context from a client is accepted.** The library reads `traceparent` from `_meta`. Claude Code sends none
	to a stdio server, so its calls start their own traces.
10. **Each source and meter is named.** The sources `InventorMcp.Server` and `Experimental.ModelContextProtocol`, and
	the meter `InventorMcp.Server` only, never `"*"`, so a source of another package cannot export a value that the
	allowlist did not plan for. The library's meter is left out: its metrics have the client's tool and method names,
	and a view cannot change a value. The library's name is one
	constant, and `LibraryTelemetryTests` fails when a library update changes a name or adds a tag.
11. **No sensitive attribute.** The server never turns on `gen_ai.tool.call.arguments`, `gen_ai.tool.call.result`
	or `gen_ai.prompt.variable.*`, and exports no log, because a log line can hold a dialog text or a path.

Not done: telemetry in the add-in (the server times each round trip), trace context over the pipe, a shared backend,
a log store, and alerts.

### The build enforces the code rules

A coding agent reads the output of `dotnet build`, not the rule files. A rule that the build enforces is obeyed
every time, because the agent sees the error and fixes it before a person reads the diff. A rule that is only in a
document is skipped when the agent's context is full. So each rule that a tool can check is a build error:

- `Directory.Build.props` sets `AnalysisLevel` to `latest`, `AnalysisMode` to `Recommended`, and
	`EnforceCodeStyleInBuild`. `TreatWarningsAsErrors` makes each warning an error.
- `.editorconfig` sets the severity of each rule. It gives a style rule the severity `warning` only when the code
	already obeys it, so the build stays clean and the rule stops new drift.
- `Directory.Packages.props` adds `Meziantou.Analyzer`, `Roslynator.Analyzers` and `SonarAnalyzer.CSharp` to every
	project as a `GlobalPackageReference`. That is build time only, so no analyzer assembly reaches an output folder,
	which matters for the add-in loader. `.editorconfig` makes every Roslynator rule a warning, because its rules are
	off or `info` until set, and a rule that a new version adds is then enforced too.
- `global.json` keeps the build on the .NET 10 SDK (`rollForward` `latestFeature`). `AnalysisLevel` `latest` takes its
	rules from the SDK that compiles, and with no `sdk` section that is the newest SDK on the machine. A runner image
	with the .NET 11 SDK then added CA2026 as a build error to code that had not changed. The `version` is the lowest
	SDK that can build, and `latestFeature` uses the newest .NET 10 SDK that is installed. Dependabot does not update
	the file: a patch update changes nothing, because `latestFeature` already uses it. Change `global.json` by hand in
	the pull request that moves the target framework.

Measured on 2026-10-18 with the code as it was then, as unique findings:

| Setting | Findings | Decision |
| --- | --- | --- |
| `AnalysisMode` `Recommended` | 40 | Used. All fixed or set per file with a reason. |
| `AnalysisMode` `All` | about 270 | Not used. Most are CA2007 (`ConfigureAwait`), CA1307, CA5392 and CA1031. |
| Every style rule at `warning` | 2,184 | Only the rules with 0 findings, after the fixes below. |
| `Meziantou.Analyzer` | 162 | Used, with 4 rules off or lowered. 42 findings fixed. |
| `SonarAnalyzer.CSharp` | 141 | Used, with 6 rules off. 105 are S1125. 18 findings fixed, 4 suppressed on the line. |

Measured on 2026-10-19, with every Roslynator rule a warning:

| Setting | Findings | Decision |
| --- | --- | --- |
| `Roslynator.Analyzers` | 139 | Used, with 7 rules off. 105 are RCS1049. 11 findings fixed. |

Rules that are wrong for this code, and why:

- CA2007 and MA0004 (`ConfigureAwait(false)`): the server has no `SynchronizationContext`, so the call does
	nothing there. In the add-in, the context can be Inventor's main thread, where the COM calls must run.
- CA1031 (a catch of `Exception`): every bridge handler and COM boundary must catch all, or one failure ends
	Inventor. It is only in `All`.
- IDE0100, RCS1049 and S1125: all report `x is false` as redundant.
- RCS1238 and S3358 (nested `?:`), RCS1217 (an interpolated string with no value in it), S3267 (a loop that LINQ
	could replace): each reports a form that this code uses.
- RCS1181: it makes a `//` comment on a private member into a doc comment. That comment says why, for a maintainer.
- RCS1161 (explicit enum values): no enum is stored or sent as a number.
- RCS1194 and S3871 (exception constructors, a public exception): each exception is internal, and is thrown and
	caught in its own assembly.
- MA0048 (one type for each file): `InventorTool` is one `partial` class over files named `InventorTool.<Part>.cs`.
- CA1725, RCS1168, S927 and CA1001 in `StandardAddInServer` and `IsolatedAddInServerProxy`: the interop names the
	parameters of `ApplicationAddInServer`, and Inventor ends the lifetime with `Deactivate`, not `Dispose`.
- S1118 in the tests: xUnit finds a collection definition by its class, which must not be static.

Each suppression on one statement has its reason on the line above: `GC.Collect` when the add-in unloads (S1215),
`DangerousGetHandle` for a Win32 call (S3869), and a log of a bridge error with no stack trace (S6667).

The analyzers found real defects: a cancellation registration that `MainThreadDispatcher.InvokeAsync` disposed as
it returned, before the work ran (MA0100), regular expressions with no match timeout (MA0009), parsing that used
the culture of the machine (CA1305, MA0011), and `StringBuilder` buffers in P/Invoke signatures (CA1838).
Sonar and Roslynator found smaller ones: a window owner read after a failed `GetWindowThreadProcessId` call, a
cancelled task that did not record its token (S8949), a registration disposed without a wait for its callback
(RCS1261), and an `InventorOperations` field that no code read (S1144).

Three `.editorconfig` options did not match the code, and now follow it. The code uses braces only for a statement on
more than one line (250 findings with `true`, 1 with `when_multiline`). It uses an expression body for a method on
one line (177 findings with `false`, 6 with `when_on_single_line`). IDE0022 stays a suggestion, so both bodies are
allowed. It keeps an `if` with an early return, and does not make it a `?:` (21 findings for IDE0046 with
`dotnet_style_prefer_conditional_expression_over_return = true`). The rule for private names did not change: a field
or a property that is not public starts with `_`. The code had 11 names that did not obey it, and they were renamed.

Measured on 2026-10-19, and done in the same change:

| Rule | Findings | Decision |
| --- | --- | --- |
| CA1848, CA1873 (`[LoggerMessage]`) | 21 | Fixed. Each class logs through generated methods in its `<Type>.Log.cs` file. |
| MA0137 (`Async` suffix) | 34 | Fixed. Every tool sets `Name` on `[McpServerTool]`, so no tool name changed. |
| IDE0290 (primary constructor) | 4 | Fixed. |
| IDE0058 (an unused value) | 10 | Fixed with `_ =`, as the code does elsewhere. |
| CA1859 (a concrete return type) | 2 | Fixed. The two operation methods return `List<T>`. |
| IDE0055 (formatting) | 2,358 | Used. Every finding was a line ending: `.editorconfig` asked for `crlf`, and a file that a tool wrote after the checkout kept `lf`, so the result depended on the machine. `.gitattributes` now sets `eol=lf` for every checkout and `.editorconfig` asks for `lf`, which left 0 findings. The rule does not check the space or the tab after `///`. |
| MA0051 (method over 60 lines) | 17 | A suggestion. Most of the long methods are tools that only a live Inventor session can test, so a split is done when a change to that method is tested anyway. |

Not used: NuGet lock files (`RestorePackagesWithLockFile`). The add-in's target framework changes with
`AutodeskVersion`, so one lock file cannot serve every release. Central package management already keeps each
version in one file. `xunit.analyzers` is not added, because `xunit.v3` brings it.

### Parameter writes are one Undo step

`inventor_set_parameters` and `inventor_set_parameter` run one composed snippet (`ComposeSetParametersSnippet`), and
the snippet groups its writes in one Inventor transaction, named `MCP: Set Parameters`. One Undo in Inventor reverts
the call, and the activity feed shows the name. Before, ten parameters gave ten steps or more, and a write that failed
halfway left the document half changed. The rules come from live checks, in `Docs/Research/Write-Transactions-Checks.md`:

- The step starts on the document that is written, not on the active document. A document with no window, or one
  that is not active, gets the step too.
- `document.Update()` and `runRuleAfter` are inside the step. A rule that runs inside joins it, and an `Update` after
  the step would make a second one. A nested transaction joins the outer one, so no check of the current transaction
  is needed.
- A batch with failed entries keeps the successes and ends the step, because the model repeats only the failed
  entries. An exception outside the loop (ex. from `Update`) aborts the step, and the document goes back.
  `Abort` fails when the step is not the current one, so a failure of `Abort` is logged and the first exception is
  thrown again.
- The restore of `RulesEnabled` is after the step, in a `finally` block. It is a setting of the session, and an abort
  does not restore it.
- `inventor_set_parameter` uses the same snippet in every case, so there is one write path, no add-in build, and the
  same checks (a numeric expression is parsed before it is written). Its result has the kind, expression, internal
  value, display value and units of the parameter. The add-in operation `parameters.set` was removed with bridge
  version 3.
- A model that wants one Undo step for its own snippet calls the prelude helper
  `InTransaction(document, "MCP: Name", () => { ... })`. There is no `transactionName` parameter on
  `inventor_eval_csharp`, because a wrapper around the code of the model breaks a snippet that declares a method or
  returns at the top level.
- Not wrapped: `inventor_auto_balloon` (it has its own transaction, and its abort after the time limit is part of its
  logic), tools that make one change (`inventor_set_property`, `inventor_update`), and tools that save, close or copy
  (a save inside a transaction is proven only for a file outside a Vault project, by a rule that `runRuleAfter` runs.
  See `Docs/Research/Write-Transactions-Checks.md`).

### Each tool finds its document the same way

A tool sends its `documentName` to the add-in, and the add-in resolves it (`ResolveDocument`, with the pure function
`DocumentNameMatch`). A composed snippet then uses the `Document` global through `ToolDocument()`, so no snippet has its
own resolver. Before, six places resolved a name, and three cases gave a wrong document with no message. The facts
come from live checks in Inventor 2026, in `.agents/rules/inventor-interop.md`, "Model states and the edit document".

1. **No name:** `ActiveEditDocument`, else `ActiveDocument`, else `no-active-document`. During an edit in place of a
   part in an assembly, the part. With no edit in place, the two are the same object.
2. **A name:** each open document whose full document name, path or display name equals it (no case) matches. The
   matches are grouped by full document name, because an open model state has two documents with one full document
   name: the factory and the member. The factory of a group wins, because Inventor edits a model state through it.
3. **One group** is the document. **More than one group** gives `ambiguous-document`, with the full document name of
   each in the message and in `detail`. A path whose file has two model states open is ambiguous, because a path
   alone opens the primary model state, which can be neither. **No match** gives `not-found`.

A tool that may open a file sends the snippet again on `not-found` when the name is a full path of a file that exists,
with a line that opens it with the rules off. A write does not open a file, because a change to a document that the
call opens is lost when it closes. `OpenOrReuseDocument` and `FindOpenDocument` of the prelude use the same rules, for
a snippet that finds a second document (ex. `compareWith` of `inventor_styles`, and the files of
`inventor_file_info`). `inventor_orientation` always reads the camera of the active view, which belongs to the top
document, also during an edit in place.

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
with the UI Automation COM API, clicks the button that `DialogSettings.json` sets for a known dialog type, keeps
waiting on a progress dialog set to `"Wait"` (up to `ProgressDialogWaitLimit`, 10 min), and stops the wait with
`blocked-by-dialog` for any other dialog. The settings are a file, not code, so each user
chooses the answers (ex. to the iLogic Security Alert) with no change to the policy, and a user file in
`%LOCALAPPDATA%\InventorMcp\` changes them with no build.
The server reads both files one time, at start. An agent that runs as the user can write the user file, so a looser
setting has no effect until a person restarts the server.
The server checks that the process at the other end of the pipe is this user's installed Inventor before it uses the
pipe, because a process of any user can create a pipe name first (`Bridge/PipeHost.cs`).
Dialog reads replace the text of a password field, so a dialog's secret never reaches the model.
It also checks before it sends a call, because the modal loop of an open dialog still runs the add-in's work, and a
new call would run nested inside the call that opened it. A progress dialog set to `"Wait"` holds the request at
this check until the dialog closes.
`inventor_dialogs` and `inventor_dialog_click` use the same service with no bridge call.
The UI Automation COM interop (`Interop.UIAutomationClient`) is used in place of `System.Windows.Automation`, so the
server needs no .NET Desktop Runtime. See `Research/Blocking-Dialog-Detection.md`.

#### How the server finds, reads and clicks a dialog

Live tests on Inventor 2025, 2026 and 2027 found each rule below. The measurements are in
`Research/Blocking-Dialog-Detection.md`.

- **Find windows with Win32, and read them with UI Automation.** `EnumWindows` finds each visible top-level window
	of the Inventor process. A UI Automation search from the desktop root does not find the dialog, because UI
	Automation puts an owned window below the main frame. A disabled main frame (`IsWindowEnabled`) is the block
	signal, because a modal dialog disables its owner.
- **Walk the tree by hand.** The UI Automation tree of a window also holds the windows that it owns. The walk stops
	at each element of a different top-level window, so the read of one dialog does not mix in the text and buttons
	of another.
- **Read each Win32 child window from its own handle too.** UI Automation sometimes does not return the DevExpress
	buttons of the iLogic error dialog, and it returns no button of the migration dialog. When UI Automation finds no
	button in a `#32770` dialog, each `Button` child window is a button with its control id, and each visible `Static`
	adds its text.
- **Read text from the patterns.** An edit or rich text control keeps its text in `ValuePattern` or `TextPattern`,
	not in `Name`.
- **Test visibility with Win32.** UI Automation `IsOffscreen` is wrong for WinForms controls: the hidden template
	buttons of the iLogic error dialog (`Apply`, `Extra`, `Second`, `Cancel`) show as on screen. The server uses
	`IsWindowVisible` for a control with a handle, and `IsOffscreen` only for a control with no handle (WPF).
- **Skip the buttons of title bars and scroll bars.** UI Automation reports `Close` and the scroll arrows as visible
	buttons. Without this skip, the buttons of a dialog never match its catalog entry.
- **Click a Win32 button with `WM_COMMAND`.** `BM_CLICK` from a different process did not close the migration
	dialog. `WM_COMMAND` with the button's control id, sent to the dialog through `SendMessageTimeout`, did. A UI
	Automation button gets `InvokePattern.Invoke`.
- **Limit each read and click to 2 s.** A dialog that does not answer must not stop the watchdog.

Before a click, the server reads the dialog again. It clicks only when the dialog is still open, has the same title,
and has exactly one visible, enabled button of that name.

The server gets the Inventor process ID from the pipe (`GetNamedPipeServerProcessId`), not from a bridge call,
because a bridge call also waits on the dialog. The MCP SDK matches each response to its request ID, so a late
response to a stopped call never becomes the answer to the next call.

Up to 16 servers can wait on one Inventor, and each sees the same dialog. Only the server that holds the named mutex
`Local\InventorMcp.Dialogs.<processId>` clicks. The others report that the dialog was closed by a different server or
the user.

Limits:

- If Inventor runs as administrator and the server does not, Windows UIPI can stop the click while the read works.
- A dialog of a different process (ex. Vault or a licensing service) is not in the Inventor process. The main frame
	is disabled, but the dialog list is empty.
- Some third-party grids and virtualized lists give little data to UI Automation.
- `Interop.UIAutomationClient` has had no release since 2020. It holds only COM interface declarations, so it has no
	code to fix. If it leaves nuget.org, generate the same assembly from `UIAutomationCore.dll` with `tlbimp`, or use the
	WPF framework reference, which needs the .NET Desktop Runtime.

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

- Every snippet is appended to an audit log before it runs, with a timestamp, the target document, the client and the
	SHA-256 of the code. The add-in writes the code in full the first time in each file, and only the SHA-256 after
	that. The add-in decides this from the code that it runs, so a caller cannot hide code from the log.
- The audit log and `addin.log` start a new file at 10 MB, and the 5 newest full files stay.
- Execution is refused on a document with unsaved changes unless explicitly overridden.
- Roslyn loads on first use only, so a read-only session never pays for it inside Inventor's process.
- Each tool has a title and the hints `readOnlyHint`, `destructiveHint` and `openWorldHint`. Without a hint, the MCP
	specification reads a tool as destructive and open to the world, so a client asks more than necessary for a read,
	and cannot tell a read from a write. `ToolPipelineTests` compares the tool list with a snapshot, because a name, a
	hint or a parameter is the public API of the server.
	A tool that writes also sets `idempotentHint`, and `EveryToolSaysWhatItCanChange` enforces it. A parameter write is
	not idempotent, because a write can run iLogic rules again. `inventor_ilogic_rule_get` is not read-only, because
	`outputFolder` makes it write files, and a client can run a read-only tool with no approval.
- A result that reports a failure has `isError`. The tools still return a failure as a normal payload with a code and
	a message, so a model can read it. `ToolPipeline.MarkFailure` sets the flag in one place, for a top level `error`
	and for code that did not succeed. Four pairs of tool and code answer the question of the tool, so they are not
	failures: `inventor-not-running` from `inventor_session` and `inventor_dialogs`, and
	`inventor-starting-or-no-bridge` and `version-required` from `inventor_start`. A batch with some failed items is not
	a failure either, because a flag on a partial result invites a repeat of the whole batch, and a parameter write is
	not idempotent. `inventor_set_parameters` adds `error: "no-parameter-set"` only when no item succeeded.
- `INVENTORMCP_TOOLS=read-only` removes each tool that has no `readOnlyHint`, apart from the two that select and start
	a session. `INVENTORMCP_CONFIRM_RISKY_CALLS=true` asks the user, through elicitation, before a call that removes
	the guard for unsaved changes, answers a dialog or closes documents. The question shows the tool name and fixed
	text only, never an argument. Both are read one time at start, and a value that is not known selects the stricter
	setting. They are a limit on the model, not a sandbox.
- The text of an execution result is cut in the server, at 40,000 characters for the whole JSON text of the result,
	and at 10,000 for a result that failed. The text keeps its start (70 %) and its end (30 %), because a snippet
	writes its summary last, with one marker line between them. A line longer than 30 % is cut to it, so the last line
	keeps its end. Claude Code moves a text result above 50,000 characters to a file, and keeps only the first and the
	last 5,000 characters of an error result above about 11,000, so both limits are below those numbers. No number was
	found for the other clients.
	The limit counts the characters after JSON escaping, because the client measures that text. The diagnostics and
	the exception message each keep at most a quarter of the limit, and a part of the limit stays free for the member
	names and the long execution warning. The tools serialize their results with the relaxed encoder
	(`ToolPipeline.ResultSerializerOptions`): the default encoder writes a quote as `"`, so a JSON return value
	became up to six times longer in the text than the string that was counted.
	A result that was cut has `outputTruncated`, `returnValueTruncated` and `fullTextFile`, because cut JSON does not
	parse. `ExecutionResult` is shared with the add-in, so these members are on a server type.
	The audit log keeps the snippet and one result line, not the output. So the cut text goes to
	`%LOCALAPPDATA%\InventorMcp\Results` first, and the marker names the file: a second run would repeat a write.
	The server deletes a file after 14 days, the same age as the server logs. `inventor_assembly_tree` uses the same
	40,000 characters. The list tools have no paging, because no measured result of them was over a client limit.
- `inventor_test_copy` sends its script to Windows PowerShell through standard input, so no script file exists that
	another process can change before it runs.

A snippet that throws is reported as a result rather than failing the call, and compiler errors come back as
diagnostics with line and column, before anything reaches the model.
