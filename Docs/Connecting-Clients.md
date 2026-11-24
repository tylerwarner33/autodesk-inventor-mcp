# Connecting clients

How to add the Inventor MCP server to Claude Code, Claude Desktop, Visual Studio and Visual Studio Code.

The server is a stdio MCP server, so the client starts it. There is nothing to leave running and no port.
Claude Code, Claude Desktop, and GitHub Copilot Chat in Visual Studio and in Visual Studio Code can all use it.

Each client is configured **machine wide**, in its own user configuration file, so the server is available in every
folder, repository and solution. The repository holds no client configuration.

## Pick a setup

| | Using the server | Developing the server |
| --- | --- | --- |
| Who | Anyone who uses the tools, ex. a team member | Anyone who changes the server in this repository |
| Feed | A team feed: a file share or an internal NuGet feed | `%LOCALAPPDATA%\InventorMcp\Feed`, filled by each Debug build |
| Versions | Releases (ex. `0.1.0`), so no `--prerelease` | `<version>-dev.<UTC time>` (ex. `1.0.0-dev.20261119181710`), so `--prerelease` |
| New version | When a release is published to the feed | After every build that changes the server |
| Needs the repository | No | Yes |

Both setups need the Inventor add-in installed for each release in use. See `Docs/Setup-and-Usage-Guide.md`, "Install".

<details>
<summary><b>The team feed, and where each client keeps its configuration</b></summary>

No team feed and no release pipeline exist yet. The "Using the server" examples show where the team feed goes.
`<team feed>` is its path or URL, ex. `\\server\share\InventorMcp` or
`https://pkgs.dev.azure.com/<organization>/_packaging/<feed>/nuget/v3/index.json`. A feed that needs a sign-in also
needs a NuGet credential provider on each machine.
Write a UNC path with doubled backslashes inside JSON, ex. `\\\\server\\share\\InventorMcp`.

The team feed is the same text on every machine, so each "Using the server" entry needs no variable.
The local feed differs per user, and the four clients differ in how they name a per user folder:

| Client | Configuration file | Local feed in the development setup |
| --- | --- | --- |
| Claude Code | `~/.claude.json`, user scope | `${LOCALAPPDATA}/InventorMcp/Feed` |
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` | Absolute path. No variables. |
| Visual Studio | `%USERPROFILE%\.mcp.json` | Absolute path. It passes `${env:...}` through as text. |
| Visual Studio Code | `%APPDATA%\Code\User\mcp.json` | `${env:LOCALAPPDATA}\\InventorMcp\\Feed` |

In the examples, replace `<you>` with your Windows user name.

</details>

## Set up one client

Open the section of the client that you use. Each section has the entry for both setups.

<details>
<summary><b>Claude Code</b></summary>

Add the server at user scope from any terminal. It is then available in every folder.

**Using the server:**

```
claude mcp add-json autodesk-inventor-mcp-server -s user '{"type":"stdio","command":"dotnet","args":["tool","exec","InventorMcp.Server","--source","<team feed>","--yes"],"env":{"DOTNET_NOLOGO":"1","DOTNET_CLI_TELEMETRY_OPTOUT":"1"}}'
```

**Developing the server:**

```
claude mcp add-json autodesk-inventor-mcp-server -s user '{"type":"stdio","command":"dotnet","args":["tool","exec","InventorMcp.Server","--prerelease","--source","${LOCALAPPDATA}/InventorMcp/Feed","--yes"],"env":{"DOTNET_NOLOGO":"1","DOTNET_CLI_TELEMETRY_OPTOUT":"1"}}'
```

- To change an entry, run `claude mcp remove autodesk-inventor-mcp-server -s user` first, then add it again.
- In Git Bash, `claude mcp add-json` rejected JSON with escaped backslashes. Forward slashes in a path worked.
- Check it with `claude mcp list`, which starts each server and reports Connected or Failed.
- After a server change, run `/mcp` and Reconnect. A change inside an existing tool then appears. A new or changed
	tool can need a new session.
- An entry of the same name at another scope (ex. a project `.mcp.json`) must be the same text, or `/mcp` reports
	"Conflicting scopes".
- An entry that still runs `bin\Debug\InventorMcp.Server.exe` locks the build output. Replace it.

</details>

<details>
<summary><b>Claude Desktop</b></summary>

Open Settings, Developer, Edit Config. That opens the correct file, also for a Microsoft Store installation, which
keeps it in a different folder. Add the entry to `mcpServers`.

**Using the server:**

```json
{
	"mcpServers": {
		"autodesk-inventor": {
			"command": "dotnet",
			"args": [
				"tool", "exec", "InventorMcp.Server",
				"--source", "<team feed>",
				"--yes"
			],
			"env": {
				"DOTNET_NOLOGO": "1",
				"DOTNET_CLI_TELEMETRY_OPTOUT": "1"
			}
		}
	}
}
```

**Developing the server:**

```json
{
	"mcpServers": {
		"autodesk-inventor": {
			"command": "dotnet",
			"args": [
				"tool", "exec", "InventorMcp.Server",
				"--prerelease",
				"--source", "C:\\Users\\<you>\\AppData\\Local\\InventorMcp\\Feed",
				"--yes"
			],
			"env": {
				"DOTNET_NOLOGO": "1",
				"DOTNET_CLI_TELEMETRY_OPTOUT": "1"
			}
		}
	}
}
```

- Quit Claude Desktop from the tray and start it again after each change, including a server change. Closing the
	window leaves it running.
- Settings, Developer shows the server as Running when it started.
- Claude Desktop starts two servers from one entry, one for chat and one for agent mode. Each uses a pipe connection
	once it calls Inventor. See "More than one client".
- A failed start is logged in `%APPDATA%\Claude\logs\mcp-server-autodesk-inventor.log`.

</details>

<details>
<summary><b>Visual Studio</b></summary>

Needs Visual Studio 2022 17.14 or later, or Visual Studio 2026, with Copilot Chat in **Agent** mode.
Ask mode does not call tools.

Add the entry to `servers` in `%USERPROFILE%\.mcp.json`, which Visual Studio reads for every solution. Create the file
if it does not exist. Keep any servers already in it.

**Using the server:**

```json
{
	"servers": {
		"autodesk-inventor-mcp-server": {
			"type": "stdio",
			"command": "dotnet",
			"args": [
				"tool", "exec", "InventorMcp.Server",
				"--source", "<team feed>",
				"--yes"
			],
			"env": {
				"DOTNET_NOLOGO": "1",
				"DOTNET_CLI_TELEMETRY_OPTOUT": "1"
			}
		}
	}
}
```

**Developing the server:**

```json
{
	"servers": {
		"autodesk-inventor-mcp-server": {
			"type": "stdio",
			"command": "dotnet",
			"args": [
				"tool", "exec", "InventorMcp.Server",
				"--prerelease",
				"--source", "C:\\Users\\<you>\\AppData\\Local\\InventorMcp\\Feed",
				"--yes"
			],
			"env": {
				"DOTNET_NOLOGO": "1",
				"DOTNET_CLI_TELEMETRY_OPTOUT": "1"
			}
		}
	}
}
```

- Visual Studio does not expand variables in this file. `${env:LOCALAPPDATA}` reaches `dotnet` as text, relative to
	the solution folder, so the feed path must be absolute.
- Trust the server when asked, then turn its tools on in the tools picker. Visual Studio adds them turned off.
- After a server change, restart the server from the CodeLens in `%USERPROFILE%\.mcp.json`.
- From 18.7, Visual Studio asks for trust again when the server's instructions or tools change.
- If the tools picker shows no MCP servers, check the organization policy "MCP servers in Copilot" first.
- The log is in `%TEMP%\VSGitHubCopilotLogs\`. A failed start there quotes the server's stderr.

</details>

<details>
<summary><b>Visual Studio Code</b></summary>

Needs GitHub Copilot Chat in **Agent** mode. Ask mode does not call tools.

Run "MCP: Open User Configuration", which opens `%APPDATA%\Code\User\mcp.json`, and add the entry to `servers`.
It is then available in every folder and workspace.

**Using the server:**

```json
{
	"servers": {
		"autodesk-inventor-mcp-server": {
			"type": "stdio",
			"command": "dotnet",
			"args": [
				"tool", "exec", "InventorMcp.Server",
				"--source", "<team feed>",
				"--yes"
			],
			"env": {
				"DOTNET_NOLOGO": "1",
				"DOTNET_CLI_TELEMETRY_OPTOUT": "1"
			}
		}
	}
}
```

**Developing the server:**

```json
{
	"servers": {
		"autodesk-inventor-mcp-server": {
			"type": "stdio",
			"command": "dotnet",
			"args": [
				"tool", "exec", "InventorMcp.Server",
				"--prerelease",
				"--source", "${env:LOCALAPPDATA}\\InventorMcp\\Feed",
				"--yes"
			],
			"env": {
				"DOTNET_NOLOGO": "1",
				"DOTNET_CLI_TELEMETRY_OPTOUT": "1"
			}
		}
	}
}
```

- After a server change, restart the server from "MCP: List Servers".
- Automatic restart (`dev.watch`) works only from a workspace file (`.vscode/mcp.json`) with a path relative to the
	workspace, ex. `"watch": "Source/InventorMcp.Server/obj/Debug/LocalFeed.stamp"`, which the build touches after it
	packs. In the user file it did not fire, neither with `${workspaceFolder}` nor with an absolute path. The
	repository commits no workspace file. A developer who wants the automatic restart can add one locally, but it then
	defines a second entry beside the user entry.
- Trust the server on its first start.
- "MCP: List Servers" starts, stops and restarts the server, and "Show Output" opens its log with the server's stderr.
- "MCP: Reset Cached Tools" clears the tool list Visual Studio Code keeps. Use it when a new tool does not appear.
- The tools picker lists the server as `InventorMcp.Server`, the name the server reports about itself.
- If `chat.mcp.discovery.enabled` includes `claude-desktop`, Visual Studio Code also lists the server from the
	Claude Desktop configuration, and each copy starts its own server.

</details>

<details>
<summary><b>How a client starts the server</b></summary>

Every client runs the server as a .NET tool:

```
dotnet tool exec InventorMcp.Server [--prerelease] --source <feed> --yes
```

with `DOTNET_NOLOGO=1` and `DOTNET_CLI_TELEMETRY_OPTOUT=1` in the environment.

- NuGet extracts each package version to its own cache folder and the server runs from there. No client runs the
	build output, so a rebuild succeeds while clients are connected.
- With no version given, `dotnet tool exec` runs the newest package in the feed. A client picks up a new version
	when it restarts the server.
- `--source` makes the feed the only source for that call, so a package of the same name on nuget.org is never used.
- `--yes` removes a confirmation prompt that nobody can answer, because the client owns stdin.
- `DOTNET_NOLOGO` stops the first run banner, which `dotnet` writes to stdout and which would corrupt the protocol.

The server sends the modeling rules (named faces, units, extent direction, health checks, call length) in its
`initialize` response, so every client gets them. The text is `Source/InventorMcp.Server/ServerInstructions.md`.

</details>

<details>
<summary><b>More than one client</b></summary>

Each client starts its own server, and Claude Desktop starts two. A server opens a pipe connection on its first
Inventor call. The add-in accepts 16 connections at once and runs every call on Inventor's main thread, so a call
from one client waits while another client's call runs, then succeeds.

- Every client sees the same `inventor_activity` feed. Each keeps its own cursor (`nextSequence`), and reading
	removes nothing.
- A server that finds every connection in use waits 3 s, then returns `inventor-not-running` with a message that
	says Inventor is running and not to call `inventor_start`.

</details>
