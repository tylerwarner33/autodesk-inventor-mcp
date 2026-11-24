# Development

How the server is built, and how to work on it.

## How it is built

<details>
<summary><b>The three hops, and why the add-in is thin</b></summary>

| Hop | Transport | Notes |
| --- | --- | --- |
| Client to server | stdio | Claude Code, Claude Desktop, Visual Studio or Visual Studio Code starts the server with `dotnet tool exec`. |
| Server to add-in | Named pipe `InventorMcp.Bridge.v3.<year>.<process id>`, one for each Inventor process | Only the current Windows user can connect, and a client from another computer is refused. |
| Add-in to Inventor | In-process COM | Every call runs on Inventor's main thread. |

The server targets `net10.0`. The add-in targets `net8.0-windows` on Inventor 2025 and 2026, and `net10.0-windows` on 2027.
The server holds every MCP tool. The add-in is only a thin bridge: pipe listener, dispatch, and main thread marshaling.

The add-in stays thin on purpose.
Changing it costs an Inventor restart, while the tool surface changes constantly during development.

</details>

<details>
<summary><b>Projects</b></summary>

| Project | Purpose |
| --- | --- |
| `Source/InventorMcp.Contracts` | The pipe wire contract. Holds no Inventor interop, so the server builds without Inventor installed. |
| `Source/InventorMcp.AddIn` | The Inventor add-in. Owns the pipe listener and main thread dispatch only. |
| `Source/InventorMcp.AddIn.Loader` | Isolates the add-in on Inventor 2025 and 2026, which cannot do it themselves. Unused on 2027. |
| `Source/InventorMcp.Server` | The MCP server. Owns every tool. |
| `Libs/Inventor/<version>` | The vendored Inventor interop assembly per release, so a machine without Inventor can still build. |

</details>

<details>
<summary><b>Debugging the add-in</b></summary>

The add-in project sets `StartProgram` to Inventor, so pressing F5 on it launches Inventor with the debugger attached.
Deploy the manifest once first, otherwise Inventor has nothing to load.

**Inventor must be closed to rebuild the add-in.**
Unloading it through the Add-In Manager runs `Deactivate` but does not release the assembly,
because Inventor's assembly load context is not collectible, so the build still fails with a file lock.

This is the reason the add-in stays thin.
Every line in it costs a CAD restart to change, while the server's tools can be changed freely.

</details>

<details>
<summary><b>Assembly isolation</b></summary>

The manifest sets `UseInventorAssemblyContext` to `0`, which loads the add-in in its own `AssemblyLoadContext`.
The element names whether to use *Inventor's* context, so `0` is the isolated mode.
This keeps the add-in's dependencies from clashing with the versions Inventor and other vendors' add-ins load into the same process.

Inventor 2025 and 2026 ignore that element.
There, the manifest names `InventorMcp.AddIn.Loader`, which loads the add-in from an `App\` subfolder into an isolated context.

The `Autodesk.Inventor.Interop` reference must stay `Private=false` for this to work.
The interop has to resolve from Inventor itself, while the add-in's own packages resolve from its output folder.

</details>

## Which coding agents read AGENTS.md

`AGENTS.md` sits at the repository root, where each agent that supports it looks.

| Agent | Reads the root `AGENTS.md` |
| --- | --- |
| Claude Code | Yes, from v2.1.277, when no `CLAUDE.md`, `.claude/CLAUDE.md` or `CLAUDE.local.md` exists in the working folder or above it. Not in a session with telemetry disabled, on a third-party provider (ex. Amazon Bedrock), or in the first session after an upgrade. |
| GitHub Copilot in Visual Studio Code | Yes, through the setting `chat.useAgentsMdFile`. |
| GitHub Copilot in Visual Studio | No. It reads `.github/copilot-instructions.md`, which this repository does not have. |

The name `.agents/` has no special meaning to any tool. It is only a neutral folder that keeps the agent rules apart
from `Docs/`, which is written for people. No agent reads `.agents/` by itself: `AGENTS.md` lists each rule with its
trigger, and an agent opens a rule only when a task matches it. So the rules cost no context until they are needed.

