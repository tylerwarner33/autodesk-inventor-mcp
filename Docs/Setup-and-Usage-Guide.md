# Setup and Usage Guide

How to install the Inventor MCP server and use it from Claude.

## What it does

It lets Claude see and drive a running Autodesk Inventor session, 2025 through 2027: read the open documents, parameters,
iProperties and feature health, watch what an automation loop is doing as it runs, and change or create geometry.

It is built for developing Inventor plugins and automation, where the useful thing is having Claude look at the same
live session you are working in.

## Requirements

- Autodesk Inventor 2025, 2026 or 2027
- .NET 10 SDK

Inventor is needed to run, but not to build. The interop assemblies are kept in `Libs/Inventor/<version>`.

## Install

```
dotnet build InventorMcp.slnx
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -p:AutodeskVersion=2027 -p:DeployAddIn=true
```

The second command registers the add-in with Inventor by writing a manifest to
`%APPDATA%\Autodesk\Inventor 2027\Addins`. Change `AutodeskVersion` to install for 2025 or 2026.
Run it once for each release you use. Building a release does not install it.

Restart Inventor, then confirm the bridge started:

```
type %LOCALAPPDATA%\InventorMcp\2027\addin.log
```

Use the year of the release. A line reading `Bridge started on pipe 'InventorMcp.Bridge.2027' in process <id>` means
it is working.

If it is missing, open **Tools > Add-Ins** in Inventor and check that **Inventor MCP Bridge** is loaded,
with **Load Automatically** ticked.

## Connect a client

There is no connector to add and no port to open. The client starts the server itself.
Claude Code, Claude Desktop, and GitHub Copilot Chat in Visual Studio and in Visual Studio Code can all use it.

Add the server once to each client's own user configuration. It is then available in every folder, repository and
solution. Each client starts it from a package feed with `dotnet tool exec`.

There are two setups, and `README.md`, "Connecting a client", has the full entry for each client in both:

- **Using the server** starts it from a team feed. You need no copy of the repository.
- **Developing the server** starts it from `%LOCALAPPDATA%\InventorMcp\Feed`, which each Debug build fills.
	Build once before you connect a client. After a build that changes the server, restart the server in the client.

| Client | Where the entry goes | After a change |
| --- | --- | --- |
| Claude Code | `claude mcp add-json ... -s user` | `/mcp`, then Reconnect |
| Claude Desktop | Settings, Developer, Edit Config | Quit from the tray and start again |
| Visual Studio 2022 17.14 or later, or 2026 | `%USERPROFILE%\.mcp.json` | Restart from the CodeLens in that file |
| Visual Studio Code | "MCP: Open User Configuration" | Restart from "MCP: List Servers" |

In Visual Studio and Visual Studio Code, use Copilot Chat in **Agent** mode, trust the server when asked, and turn
its tools on in the tools picker. Visual Studio needs an absolute feed path, because it does not expand variables.

Every client gets the same modelling rules from the server, so each one asks Inventor which face is "top" before
it draws on it.

Several clients can be connected at once (up to 16 server connections, and Claude Desktop uses two). Their calls
queue on Inventor's main thread.

## Inventor must be running

The bridge lives inside Inventor, so it exists only while Inventor is open with the add-in loaded.

With Inventor closed, tools return a readable message rather than failing:

```
No Inventor session is hosting the MCP bridge. Ask the user whether to start Inventor, then call inventor_start.
If Inventor is already open, make sure the Inventor MCP Bridge add-in is loaded.
```

Claude can start Inventor for you. Say "start Inventor", or agree when Claude asks.

- If one release has the add-in, it starts with no question.
- If several have, Claude Code shows a form where you pick, ex. "Autodesk Inventor 2025".
	Other clients ask you in the conversation instead.
- If that release is already running, nothing starts. A different release that runs does not stop the start.

Inventor starts as if you had started it from the Start menu, so closing Claude does not close it.
If a sign-in or recovery dialog appears, answer it. Claude waits with `inventor_session` and connects once Inventor is
ready.

The MCP connection itself stays healthy, so you can close and reopen Inventor without restarting Claude.

Each release hosts its own bridge, so Inventor 2025, 2026 and 2027 can run at the same time. A second Inventor of
the same release logs the conflict and does not compete for the bridge of the first.
`inventor_session` reports which release is connected.

### Two releases at the same time

1. Start both Inventors. Say "start Inventor 2025" in one Claude session and "start Inventor 2027" in the other.
2. Each session now uses the release that it started. Nothing else is required.

A session never reaches another release by accident. If its Inventor closes, the tools say so, and name the release.
To change the release of a session, ask Claude to use another one (`inventor_use_release`).
To fix a release for a project, add `INVENTORMCP_RELEASE` (ex. `2025`) to the `env` of the MCP entry in the project
scope. If more than one release runs and the session has not chosen one, Claude asks you which to use.

Two sessions on one release share its documents and its active document, and Inventor handles their calls one at a
time. If Claude says that the bridge is outdated, an add-in from before this feature is loaded. Close Inventor,
build and deploy the add-in again for each release, and start Inventor again.

## What you can ask for

| Area | Examples |
| --- | --- |
| Session | "Start Inventor", "What is Inventor working on?", "Which documents are open?" |
| Parameters | "List the parameters", "Set width to 6 in", "What does `width / 4 + 10 mm` evaluate to?" |
| Properties | "Show the iProperties", "Set the part number" |
| Health | "Is anything sick?", "Why did that feature fail?" |
| Activity | "What did my automation just do?" |
| Geometry | "Add four holes on the top face", "Fillet the vertical edges" |
| API help | "What arguments does `AddDrilledByDistanceExtent2` take?" |
| Plugin development | "Run my plugin's build against these payloads", "Measure the balloons on that drawing" |

Geometry and anything else the named tools do not cover is done by Claude writing and running a C# snippet
against the Inventor API.

## Naming faces

Say "the top face" or "the front face" and Claude will resolve it against the **ViewCube**, including when the
ViewCube has been redefined for that document. Note that Inventor's vertical axis is Y, not Z.

## Units

Inventor stores lengths in centimetres and angles in radians whatever the document displays, so a parameter showing
`2 in` is stored as `5.08`. Results report both forms, so you never have to work out which one you are looking at.

## Watching an automation run

`inventor_activity` is a running log of what Inventor did, taken from its own events. Each committed transaction
carries the command name, so a loop reads back as `Create Sketch on a Face`, `Create Hole Feature`,
`Create Fillet Feature`, and so on.

It is a pull, not a push, so it keeps working while Inventor is busy and never floods the conversation.
Ask "what has happened since?" and Claude reads only what is new.

## Safety

Claude can change your model. Two things bound that:

- **Code execution is refused on a document with unsaved changes** unless explicitly overridden, because unsaved
	work cannot be recovered. Save before asking for geometry.
- **Every executed snippet is logged** to `%LOCALAPPDATA%\InventorMcp\executed-code.log`, with a timestamp and the
	document it targeted, so there is a record independent of the conversation.

Writes also suppress Inventor's dialogs for their duration, so a prompt cannot silently stall the session.
Inventor answers a suppressed dialog with its default, which is a real behaviour change rather than a cosmetic one.

Some dialogs still open (ex. the iLogic error dialog). The server finds them while a call waits:

- It closes an information dialog that has only `OK` (an iLogic error or a message box), and gives Claude its text.
- It leaves every other dialog open, ex. "Save changes?". Claude then tells you what the dialog says, and clicks a
	button only after you chose it.

To close no dialog automatically, add `INVENTORMCP_AUTOCLOSE_DIALOGS` with the value `false` to the `env` of the
server entry in your client configuration.

A save of a file from an earlier release shows **Data Format Has Changed**. It stays open by default, because `OK`
saves the files in a format that earlier releases cannot open. To let the server click `OK`, add
`INVENTORMCP_ACCEPT_MIGRATION_DIALOG` with the value `true` to the same `env`. Leave it off when your files go to an
older Inventor, ex. a Design Automation engine.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| "No Inventor session is hosting the MCP bridge" | Inventor is closed, or the add-in is not loaded |
| "Autodesk Inventor 2025 is not hosting the MCP bridge" | The release this session uses is closed. Start it, or ask Claude to use another release. |
| `release-required` | More than one release runs. Tell Claude which one to use. |
| `bridge-outdated` | The add-in is from before protocol version 2. Close Inventor, redeploy the add-in, and start it again. |
| The add-in is missing from **Tools > Add-Ins** | The manifest was never deployed for that release. Run the install command with its `AutodeskVersion`. |
| `<year>\addin.log` has no entry for today on 2025 or 2026 | The loader failed before the add-in started. Read `<year>\addin-startup.log`. |
| "Inventor rejected the call because it is busy" | A command or modal dialog is running. Finish it and retry. |
| "Inventor is blocked by a modal dialog" | A dialog waits for an answer. Read it on the screen, or ask Claude what it says, and answer it. |
| A tool reports success but the model looks wrong | Ask for a health check. An API call can succeed while the feature cuts nothing. |
| Changes to the add-in do not take effect | Inventor must be closed to rebuild it. Unloading the add-in is not enough. |
| The client cannot start the server | The feed is empty. Run `dotnet build` once. |
| Changes to the server do not take effect | The session still runs the old version. Restart the server in the client. |
| Copilot shows no MCP tools | Copilot Chat is in Ask mode, or the organisation policy "MCP servers in Copilot" is off. |

## Logs

| Path | Contents |
| --- | --- |
| `%LOCALAPPDATA%\InventorMcp\<year>\addin.log` | Add-in lifecycle and failures of that release |
| `%LOCALAPPDATA%\InventorMcp\<year>\addin-startup.log` | Loader failures on 2025 and 2026, before `addin.log` exists |
| `%LOCALAPPDATA%\InventorMcp\server-<date>.log` | MCP server activity |
| `%LOCALAPPDATA%\InventorMcp\<year>\executed-code.log` | Every snippet run against your session |
| `%LOCALAPPDATA%\InventorMcp\addin.log`, `executed-code.log` | From before the folder for each release |
| `%APPDATA%\Claude\logs\mcp-server-autodesk-inventor.log` | Claude Desktop: a server that failed to start |
