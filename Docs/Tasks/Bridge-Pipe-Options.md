# Bridge Pipe Options

Created: 2026-11-14. Plan added: 2026-11-16.

Status: **done on the branch of #41, with the live checks. Not merged.** #41 stays open until the rest of
`Docs/Tasks/Bridge-JsonRpc-Migration.md` (bridge version 3) is done, so the add-in is deployed one time. That
document deletes this one. The analysis and the probe results are in `Docs/Research/Task-Analysis.md`, "Bridge pipe
options".

## Verdict

- **Makes sense:** yes. The current design has a defect with two Inventor processes of one release.
- **Recommended:** yes: a pipe for each process, the first instance flag, and a deny entry for remote clients.
- **Architecture:** changes one decision. `Docs/Architecture.md` did not select "a pipe for each process ID"
	because it needs a discovery step. The server has listed the pipes since protocol version 2, so that cost is
	paid.

## Answers to the open questions

1. **Two Inventor processes of the same release.** The second one does serve. It adds instances to the name of
	the first with no error, because the ACL gives the user `CreateNewInstance`. A client gets the instance that
	has listened for the longest time, so the servers are spread over both processes. `GetNamedPipeServerProcessId`
	returns the process of the first instance for both, so the host check and the dialog watch can use the wrong
	process. `README.md` is correct. `Docs/Architecture.md`, `Docs/Setup-and-Usage-Guide.md` and the remark on
	`BridgeProtocol.PipeNameFor` are wrong.
2. **Two Windows users with the same release.** The second user gets no bridge. The create fails with
	`UnauthorizedAccessException`, and the add-in logs "Could not claim the pipe".
3. **A connection from another computer.** Possible for the same account, because .NET does not set
	`PIPE_REJECT_REMOTE_CLIENTS`.

## Facts about named pipes on Windows and in .NET

From the `dotnet/runtime` source (`System.IO.Pipes`, main and release/8.0), the Win32 documentation, and probes.

1. **One namespace for the machine.** A name that one user's process holds is taken for all users and sessions.
2. **A name lives while a server handle is open.** No stale name stays after a crash.
3. **Listing does not connect.** `Directory.EnumerateFiles(@"\\.\pipe\")` uses no instance. `File.Exists` on a
	pipe name does connect, and uses an instance.
4. **Any process with `CreateNewInstance` can add instances to a name.** Observed: only a different direction
	makes the create fail. A different type or maximum does not. The maximum of the first creator applies.
5. **The ACL cannot tell apart two processes of one user.**
6. **`PipeOptions.FirstPipeInstance`** (.NET 8 and later, value `0x00080000`) makes the create fail with
	`UnauthorizedAccessException` when the name exists. `NamedPipeServerStreamAcl.Create` accepts it. Pass it on
	the first instance only. It protects only the call that has it.
7. **.NET does not set `PIPE_REJECT_REMOTE_CLIENTS`**, and has no option for it. A network logon has the
	`NETWORK` SID (S-1-5-2) in its token, and an interactive logon does not. A deny entry for that SID blocks
	remote clients. WCF uses the same entry.
8. **`PipeOptions.CurrentUserOnly`** on a server only sets an ACL, and throws `ArgumentException` together with an
	explicit `PipeSecurity`. On a client it compares the owner of the pipe with the owner of the token after the
	connect.
9. **A client gets the instance that has listened for the longest time** (observed, not documented).
10. **`GetNamedPipeServerProcessId` gives the process of the first instance** of the name (observed).
11. **A connect that the ACL refuses** throws `UnauthorizedAccessException` at once. A connect to a name with all
	instances in use, or to no name, throws `TimeoutException` at the timeout.
12. **A name** can have any character but a backslash, up to about 250 characters.

## Decisions

1. **Pipe name.** Recommended: for each process, with the protocol version:
	`InventorMcp.Bridge.v<protocol>.<year>.<process id>`, ex. `InventorMcp.Bridge.v3.2026.12345`.
2. **A second process of the same release.** Recommended: serve it. Each process has its own name, and
	`FirstPipeInstance` on the first instance guards the name.
3. **Remote clients.** Recommended: add the deny entry for `NETWORK`.
4. **A first message after the connect.** Recommended: none. The name gives the protocol version and the
	release, and the pipe gives the process, all before a connect.

Not changed: 16 instances, one duplex pipe, byte mode, one message for each line, and `PipeHost.Check` on the
server.

If the owner prefers the smallest change: keep the name for each release and add only `FirstPipeInstance` and
the deny entry. A second Inventor of a release then has no bridge, and the documents that say so become true.
The add-in work is the same size. The server work of the section "Server" below is then not necessary, except
the detection of older names.

## Steps

### Contracts (`Source/InventorMcp.Contracts/BridgeProtocol.cs`)

1. Replace `PipeNameFor(int releaseYear)` by `PipeNameFor(int releaseYear, int processId)`, and add
	`TryParsePipeName(string name, out int protocolVersion, out int releaseYear, out int processId)`. The version
	part comes from `BridgeProtocol.Version`.
2. Keep `LegacyPipeName` and add the format of protocol version 2 (`InventorMcp.Bridge.<year>`) as a second
	older format, for the detection only.
3. Add the code `instance-required` to `BridgeErrorCodes`.

### Add-in (`Source/InventorMcp.AddIn`)

4. `StandardAddInServer.Activate`: build the name with `System.Environment.ProcessId`.
5. `BridgeServer.CreatePipe`: add a parameter `first`. Pass `PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance`
	for the first instance, and `PipeOptions.Asynchronous` for each later one. Add
	`new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny)`
	to the `PipeSecurity`.
6. `AcceptLoopAsync`: the `UnauthorizedAccessException` branch now means that a different process holds the name
	of this process. Change the log text: the name has this process id, so the other process is not an Inventor
	of this user. Stop, as today.
7. Add the process id to each line of `BridgeLog`, or to the file header, because two processes of one release
	write to one `addin.log`. Check the lock in `BridgeLog`: it covers one process only (see
	`Docs/Architecture.md`). Use a file for each process (ex. `addin.<process id>.log`) if two processes can
	interleave lines.

### Server (`Source/InventorMcp.Server/Bridge`)

8. `ReleaseSelection`:
	- List the names with the prefix, parse each, and keep the instances with the protocol version of the
		server, in the Windows session of the server (`ProcessIdToSessionId` of the process id in the name).
	- A session holds a release and, after its first connect, a process id. A reconnect uses the same process id
		only. If that process is gone and a different process of the release hosts a bridge, return
		`instance-required` with the list, and send nothing. This is the same rule as for a release today: a
		session does not move unasked, because the retry after a dropped pipe could repeat a write in the other
		Inventor. If no process of the release is left, return `inventor-not-running`.
	- One instance of the selected release: use it. More than one: `instance-required`, with the process id, the
		start time and the main window title of each (from `InventorInstallations.FindRunning`, which needs a title
		member), so the user can choose.
	- A name of an older format, and no instance of this version: `bridge-outdated`, as today, for each older
		format. A name with a higher version: `version-mismatch`, with a message that tells the user to restart the
		MCP client so it gets the new server.
9. `inventor_use_release` (`McpTools/InventorTool.Release.cs`): add an optional `processId`. `inventor_session`:
	add the process id of each running instance to `release.running`.
10. `BridgeClient.EnsureConnectedAsync`: after the connect, compare the process id in the name with
	`GetNamedPipeServerProcessId`. When they differ, refuse the pipe with `untrusted-bridge-host`. Keep
	`PipeHost.Check`.
11. `inventor_start` (`InventorTool.Start.cs`): it refuses to start a release that already runs with no bridge,
	"because a second Inventor of the same release never gets the bridge". Change the text. Keep the refusal: a
	second Inventor is seldom what the user wants.

### Tests

12. `PipeNameTests.cs`: the format, a round trip through `TryParsePipeName`, and names that do not parse (the
	two older formats, a name with text in place of a number).
13. `ReleaseSelectionTests.cs`, with the fake pipe list: one instance, two instances of one release
	(`instance-required`), a chosen process id, an instance of another session (not listed), each older format
	(`bridge-outdated`), a higher version (`version-mismatch`), and a session that keeps its process id after that
	process is gone.
14. `TestPipeServer` in `TestBridge.cs`: use the new name format. A test with two test listeners of one release
	and one client that reconnects: it reaches the same listener each time.
15. A test that a second listener with `FirstPipeInstance` on the same name fails. This is a test of the add-in
	code, which the test project can compile (see the migration task, step "Tests").

### Documents

16. Correct `Docs/Architecture.md` ("A named pipe rather than a local HTTP port" and "Only the add-in is version
	bound"), `README.md` (the release section and the error table), `Docs/Setup-and-Usage-Guide.md`, and the
	remarks on `BridgeProtocol`.

## Live checks

- [ ] Two Inventor processes of one release, both with the add-in: two names in the pipe list, and
	`inventor_session` returns `instance-required` with both.
- [ ] After `inventor_use_release` with a process id, a dialog in the other process does not block this session.
- [ ] An add-in of protocol version 2 and the new server: `bridge-outdated`.

## When the plan is done

- Move the facts that the decisions depend on to `Docs/Architecture.md`, and record the decisions there.
- Check off "Two Inventor processes of the same release" in `Docs/Tasks/Live-Verification.md`.
- Delete this document.
