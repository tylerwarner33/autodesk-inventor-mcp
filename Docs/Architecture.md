# Architecture

Why this tool is built the way it is.
For installing and using it, see `Setup-and-Usage-Guide.md`.

## Shape

```
Claude
    | stdio
InventorMcp.Server            net10.0    every MCP tool lives here
    | named pipe  InventorMcp.Bridge     restricted to the current Windows user
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

The pipe name is fixed, so the server connects with no discovery step.
Only one Inventor session can host it; a second logs the conflict rather than competing.

### stdio rather than HTTP to Claude

A single user developer tool, with the pipe already carrying the trust boundary.
stdio means Claude starts the server itself: no port, no token, no URL to keep in sync.

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

### Plugins under development load from a copy

`inventor_run_plugin` copies a plugin's build output and loads the copy into a new collectible context on every call,
so the build output is never locked and a code change needs no Inventor restart. See `Plugin-Development-Loop.md`.

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
