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

Restart Inventor, then confirm the bridge started:

```
type %LOCALAPPDATA%\InventorMcp\addin.log
```

A line reading `Bridge started on pipe 'InventorMcp.Bridge' in process <id>` means it is working.

If it is missing, open **Tools > Add-Ins** in Inventor and check that **Inventor MCP Bridge** is loaded,
with **Load Automatically** ticked.

## Connect Claude

There is no connector to add and no port to open. Claude starts the server itself.

**Claude Code** reads `.mcp.json` from the repository root, so running Claude Code in this folder is enough.

**Claude Desktop** needs an entry in `claude_desktop_config.json` using the full path:

```json
{
	"mcpServers": {
		"autodesk-inventor": {
			"command": "C:\\repos\\_MyProjects\\autodesk-inventor-mcp\\Source\\InventorMcp.Server\\bin\\Debug\\InventorMcp.Server.exe",
			"args": []
		}
	}
}
```

## Inventor must be running

The bridge lives inside Inventor, so it exists only while Inventor is open with the add-in loaded.
Claude cannot start Inventor for you.

With Inventor closed, tools return a readable message rather than failing:

```
No Inventor session is hosting the MCP bridge.
Start Inventor and make sure the Inventor MCP Bridge add-in is loaded.
```

The MCP connection itself stays healthy, so you can close and reopen Inventor without restarting Claude.

Only one Inventor session can host the bridge, whatever its version. A second instance logs the conflict and does not
compete for it. Other Inventor versions cannot load the add-in at all.

## What you can ask for

| Area | Examples |
| --- | --- |
| Session | "What is Inventor working on?", "Which documents are open?" |
| Parameters | "List the parameters", "Set width to 6 in", "What does `width / 4 + 10 mm` evaluate to?" |
| Properties | "Show the iProperties", "Set the part number" |
| Health | "Is anything sick?", "Why did that feature fail?" |
| Activity | "What did my automation just do?" |
| Geometry | "Add four holes on the top face", "Fillet the vertical edges" |
| API help | "What arguments does `AddDrilledByDistanceExtent2` take?" |

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

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| "No Inventor session is hosting the MCP bridge" | Inventor is closed, or the add-in is not loaded |
| "Inventor rejected the call because it is busy" | A command or modal dialog is running. Finish it and retry. |
| A tool reports success but the model looks wrong | Ask for a health check. An API call can succeed while the feature cuts nothing. |
| Changes to the add-in do not take effect | Inventor must be closed to rebuild it. Unloading the add-in is not enough. |

## Logs

| Path | Contents |
| --- | --- |
| `%LOCALAPPDATA%\InventorMcp\addin.log` | Add-in lifecycle and failures |
| `%LOCALAPPDATA%\InventorMcp\server-<date>.log` | MCP server activity |
| `%LOCALAPPDATA%\InventorMcp\executed-code.log` | Every snippet run against your session |
