# Multi-Release Bridge Plan

Created: 2026-10-01

Status: **implemented on 2026-10-05, live verification open.** Steps 1 to 6 and 8 of "Order of work" are done. Step 7 needs two releases running with the redeployed add-in, and Inventor closed first. See `.agents/rules/verification-status.md`, "Multiple releases". Delete this file after step 7. The bridge used one fixed pipe name, so only one Inventor session on the machine can host it. This
plan lets Inventor 2025, 2026 and 2027 run at the same time, each with its own bridge, and lets each Claude session
use a different release with no effect on the others.

## Problem

- `BridgeProtocol.PipeName` is the constant `InventorMcp.Bridge`. The add-in of a second Inventor cannot claim it.
	`BridgeServer.cs` logs `Could not claim the pipe` and stops. That Inventor runs with no bridge.
- Several Claude sessions can already share one Inventor (up to 16 pipe instances). They cannot use two releases.
- `inventor_start` refuses to start Inventor while any `Inventor.exe` runs. The refusal assumes a second Inventor
  never gets the bridge.
- The server picks the API documentation from `BridgeClient.ReleaseYear`. That value follows the one session that
  answered, so it is correct only with one Inventor.

## Goal

- Run one Inventor for each release (2025, 2026, 2027) with the add-in loaded in each. No bridge conflict.
- Let a Claude session use a chosen release. A session never reaches another release by accident.
- Keep today's behavior when only one release runs and nothing is configured.
- Let a session change its release at run time, with no restart of the MCP server.

## Not a goal

- Two Inventor processes of the same release. They still compete for one pipe. See "Considered and not selected".
- Isolation between two Claude sessions on the same Inventor. They share the documents and the active document. The
  add-in handles requests one at a time on Inventor's main thread. This plan does not change that.

## Decisions

1. **One pipe for each release.** The name is `InventorMcp.Bridge.<year>`, ex. `InventorMcp.Bridge.2026`.
	- The name is still fixed for a release, so the server connects with no discovery step.
	- The ACL stays the current Windows user only. `MaxPipeInstances` stays 16 for each pipe.
2. **The add-in reads its release from Inventor.** It does not use a build constant. The add-in takes the software
	version from the running application, and maps it to the year. A wrong build input then cannot give a pipe name
	that does not match the process.
3. **One map in `InventorMcp.Contracts`.** A new `InventorReleases` class holds the map from release year to
	software version (2025 = 29, 2026 = 30, 2027 = 31). `InventorInstallations` has its own copy now, and
	`Directory.Build.props` has a third. After this change, the server and the add-in share one copy. The comment
	on the map keeps the pointer to `Directory.Build.props`.
4. **The server resolves the release in this order:**

	| Order | Source | Use |
	| --- | --- | --- |
	| 1 | The release the session chose with `inventor_use_release` or `inventor_start` | The pipe of that release only |
	| 2 | The variable `INVENTORMCP_RELEASE` in the `env` of the MCP entry | The pipe of that release only |
	| 3 | Automatic: the pipes that exist now | One pipe: use it. More than one: return `release-required` |

	- A chosen release never falls back to another release. If its Inventor is closed, the result is
	  `inventor-not-running` and names that release.
	- `release-required` lists the running releases. The model asks the user, then calls `inventor_use_release`.
	- The choice lives in the server process. Each Claude session has its own server, so one session cannot change
	  another session's release.
5. **The release is not a parameter on every tool.** A parameter on about 50 tools adds noise to each call and each
	tool description. One new tool sets the release for the session.
6. **`inventor_start` starts a release next to the others.** It refuses only when a process of the same release runs
	without a bridge. It sets the session release to the release that it started.
7. **Break the old name, say so clearly.** The server and the add-in ship together, but a machine can have a new
	server with an old add-in. The old add-in listens on `InventorMcp.Bridge`. When no release pipe exists and the old
	pipe exists, the server returns `bridge-outdated` and says to redeploy the add-in with Inventor closed. Without
	that check, the model sees `inventor-not-running` and starts a second Inventor.
8. **Bump `BridgeProtocol.Version` to 2.** The pipe name is part of the contract. A server and an add-in that
	disagree on it must not look compatible.
9. **Each release writes its own log files.** Two Inventor processes append to one `addin.log` now. The lock in
	`BridgeLog` covers one process only, so lines can be lost. Use `addin.<year>.log` and `addin-startup.<year>.log`.

## Changes

### `InventorMcp.Contracts`

- `InventorReleases` (new): the year to software version map, `TryGetYear(int softwareVersion)` and
  `TryGetSoftwareVersion(int year)`.
- `BridgeProtocol`:
	- Remove the constant `PipeName`.
	- Add `PipeNameFor(int releaseYear)` that returns `InventorMcp.Bridge.<year>`.
	- Add `LegacyPipeName` (`InventorMcp.Bridge`) for the `bridge-outdated` check only.
	- Update the doc comment. The name is fixed for a release, and a second Inventor of that release reports the conflict.
	- `Version` = 2.
- New error codes in `BridgeErrorCodes`: `ReleaseRequired` and `BridgeOutdated`.

### `InventorMcp.AddIn`

- `StandardAddInServer.Activate`: read the release from `_inventor.SoftwareVersion`, map it, and pass the year to
  `BridgeServer`. If the version is not in the map, log it and do not start the bridge. Do not guess a name.
- `BridgeServer`: take the pipe name in the constructor. Update the log line in the `catch` at line 107, and the
  `Bridge started` line in `StandardAddInServer.cs` line 55. Both names must be the one that the add-in uses.
- `BridgeLog` and the loader `StartupLog`: add the year to the file name. The loader runs before the add-in has
  Inventor, so give the loader the year from its own build (the loader is built for 2025 and 2026 only).
- Check `ExecutionAuditLog` for a shared file, and apply the same rule if it has one.

### `InventorMcp.Server`

- `BridgeClient`:
	- Replace the `pipeName` constructor parameter with an `IReleaseSelection` (see below). Keep a way to pass a
	  fixed name, because `TestBridge` hosts its own pipe.
	- `EnsureConnectedAsync` asks the selection for the pipe name each time it connects.
	- When the selection changes, `CloseAsync` runs, and `ReleaseYear` and `InventorProcessId` reset. The next call
	  connects to the new pipe.
	- The not-running message counts only the Inventor processes of the selected release. `FindRunning` needs a
	  release filter.
- `ReleaseSelection` (new service, registered as a singleton next to `BridgeClient`):
	- `Chosen`: the year from the tool, or from `INVENTORMCP_RELEASE`.
	- `Resolve()`: returns the pipe name, or the `release-required` or `bridge-outdated` result.
	- Lists the running pipes with `Directory.EnumerateFiles(@"\\.\pipe\", "InventorMcp.Bridge.*")`.
- `InventorInstallations`:
	- Use `InventorReleases` and delete the local map.
	- `RunningInventor` gets a `ReleaseYear` property. Read the software version from the major part of the file
	  version of `ExecutablePath`. When Windows refuses the path, the value is null.
	- `FindRunning(int? releaseYear = null)`.
- New tool `inventor_use_release(version)`:
	- Sets the release of this session. Returns the release, whether its bridge answers now, and the list of
	  running releases.
	- `version` of 0 or null clears the choice and goes back to automatic.
	- The tool is not destructive and is idempotent.
- `inventor_session`: add `selection` to the result (`chosen`, `environment` or `automatic`) and the running releases.
- `InventorTool.Start`:
	- The first check calls the session of the resolved release only. A different release that runs is not
	  `already-running`.
	- The "Inventor is running" refusal applies to a process of the target release only. When no release is known yet
	  (no `version`, one release deployed), the target is that one release.
	- After a start, set the session release to the started release.
	- Update the text of the tool description, and the message of `inventor-starting-or-no-bridge`.

### The API documentation

`ApiReferenceService.ResolveReleaseYear` uses `bridge.ReleaseYear`. With a chosen release, it can use the chosen
year before the first call. No other change is needed.

## How a user sets it up

Two sessions on two releases, with no restart:

1. Start both Inventors (`inventor_start version=2025` in one session, `inventor_start version=2027` in the other).
2. Each session now has its own release, set by `inventor_start`. Nothing else is required.

A fixed release for a project: add `INVENTORMCP_RELEASE` to the `env` of the MCP entry in a project scope file
(ex. `claude mcp add-json ... -s local`). `Docs/Architecture.md` says the repository holds no client configuration,
so this is an option for the user and not a file in this repository.

## Tests

Unit (no Inventor, default run):

- `InventorReleasesTests`: map both ways, unknown version.
- `PipeNameTests`: `PipeNameFor` for each release. The legacy name is not a release name.
- `ReleaseSelectionTests` (use `TestBridge` pipes with release names):
	- No choice, one pipe: connects to it.
	- No choice, two pipes: `release-required` with both years.
	- Environment variable set: connects to that release even when another pipe exists.
	- Chosen release with no pipe: `inventor-not-running` that names the release. No fallback.
	- A change of the choice closes the connection and connects to the new pipe.
	- Only the legacy pipe exists: `bridge-outdated`.
- `BridgeClientTests`: `ReleaseYear` and `InventorProcessId` reset on a change of the choice.
- Update `PipeProcessIdTests` and `TestBridge` for the new name.

Live (needs two Inventor releases, `INVENTORMCP_LIVE_TESTS=1`):

- Start 2025 and 2027. Each bridge answers `inventor_session` with its own year and process ID.
- Open different documents in each. A call in one session never shows the document of the other.
- Close one Inventor. The other keeps its connection.

## Documentation

- `Docs/Architecture.md`, "A named pipe rather than a local HTTP port": replace "Only one Inventor session can host
  it" with the per-release rule. Add a dated note on this change.
- `README.md`: the connection table, the `inventor_start` text, the tool table (`inventor_use_release`), and the
  error table (`release-required`, `bridge-outdated`).
- `Docs/Setup-and-Usage-Guide.md`: the "No Inventor session is hosting" text and a section on two releases.
- `.agents/rules/verification-status.md`: record what was verified and on which release pair.
- The doc comments in `BridgeProtocol.cs` (done in the changes above).

## Risks to check first

These are not proven. Do the first one before any code, because it can end the plan.

1. **Two releases at one time on one machine.** Autodesk supports side by side installs. Confirm that 2025 and 2027
	start together with the same Windows user and that the add-in loads in both. Inventor 2025 and 2026 use the .NET 8
	loader. 2027 uses .NET 10. Two runtimes in two processes should not conflict, but test it.
2. **COM registration.** The add-in uses the application from its own site, so it does not depend on the shared
	`Inventor.Application` registration. The Live test helper (`LiveInventor.cs`) and `inventor_eval_csharp` code
	that calls `GetActiveObject` or `GetObject` can reach the last Inventor that registered. Search for both and
	make each use the application object that the add-in passes in.
3. **Shared files in `%LOCALAPPDATA%\InventorMcp`.** Other files than the logs (ex. the audit trail) may have a
	shared name. Find each and add the year where two processes write.
4. **Dialog watchdog.** It uses `InventorProcessId` from the pipe, so it already follows the connected process.
	Add a Desktop test with two fixture processes to prove that a dialog in release A does not block a call to
	release B.
5. **Old servers in running sessions.** A session that started before the upgrade keeps the old server with the old
	pipe name until the user restarts it. Say so in the release note.

## Considered and not selected

- **One pipe for each process ID** (`InventorMcp.Bridge.<pid>`). It would also allow two Inventors of one release.
  The server would need a discovery step and a way to choose between processes with the same release, and a
  restart gives a new name. A release is what the user means when they say "use 2025". Add this later if two
  processes of one release become a real need.
- **A release parameter on every tool.** See decision 5.
- **A broker process** that owns the single pipe and forwards to each Inventor. It adds a third process and a new
  failure point for a problem that a pipe name solves.
- **Keep the old name for the first Inventor that starts.** The name would mean different releases at different
  times, and a session could reach the wrong release with no sign of it.

## Order of work

1. Check risk 1 by hand. Stop and report if two releases cannot run together.
2. `InventorReleases` and `BridgeProtocol` in Contracts, with their unit tests.
3. Add-in: release read, pipe name, log file names. Build for 2025, 2026 and 2027.
4. Server: `ReleaseSelection`, `BridgeClient`, `InventorInstallations`, with their unit tests.
5. Tools: `inventor_use_release`, `inventor_session`, `inventor_start`.
6. Search for `GetActiveObject` and `GetObject` (risk 2) and the shared files (risk 3).
7. Live test with two releases. Record the result in `verification-status.md`.
8. Update the documentation.
