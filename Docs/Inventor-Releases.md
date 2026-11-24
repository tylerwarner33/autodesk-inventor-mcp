# Starting Inventor and using more than one release

How the server finds Inventor, starts it, and picks a release when several run.

## Inventor must be running

The add-in hosts the named pipe, so the bridge exists only while Inventor is open with the add-in loaded.

With Inventor closed, every tool returns a readable `inventor-not-running` result:

```
No Inventor session is hosting the MCP bridge. Ask the user whether to start Inventor, then call inventor_start.
If Inventor is already open, make sure the Inventor MCP Bridge add-in is loaded.
```

The MCP server itself still starts and lists its tools, so the connection stays healthy while Inventor is closed.
That is the reason for the two process design.

<details>
<summary><b>Starting Inventor from Claude</b></summary>

`inventor_start` starts Inventor, only when you ask for it. It offers only releases that have the add-in deployed.

- One such release: it starts with no question.
- More than one: Claude Code shows a form with "Autodesk Inventor 2025", "2026" and "2027".
	A client that cannot show a form returns `version-required`, and Claude asks you instead.
- The release already running, with or without the bridge: nothing starts. A different release that runs does not
	stop the start.
- `newInstance`: one more Inventor process of the release starts, even when the release already runs (ex. a second
	Inventor 2026). Claude sets it only when you ask for one more Inventor.
- After a start, this session uses the release it started. After a start with `newInstance`, it uses the new process
	only, and the Inventor processes that ran before stay with the sessions that use them. See "More than one Inventor
	release".

Inventor starts with Explorer as its parent, so closing Claude does not close Inventor.
The call waits up to 45 s for the add-in. After that it returns `still-starting`, because a sign-in or recovery
dialog can hold Inventor. To wait longer, `inventor_session` takes `waitSeconds` (up to 45 s for each call).
Set `INVENTOR_MCP_RELEASES` (ex. `2025`) on the server to limit the releases it considers.

</details>

<details>
<summary><b>More than one Inventor release</b></summary>

Inventor 2025, 2026 and 2027 can run at the same time. Each add-in listens on its own pipe, and each Claude session
uses one release.

- `inventor_start` with `version` starts a release next to the others, and sets the release of the session.
- `inventor_use_release` sets it with no start. A version of 0 clears the choice.
- `INVENTORMCP_RELEASE` in the `env` of the MCP entry fixes the release for a project, ex.
	`claude mcp add-json ... -s local`. The repository holds no client configuration, so this is your choice.
- With none of these, the server uses the one release that runs. When more than one runs, a tool returns
	`release-required` with the years, and Claude asks you.

A chosen release never falls back to another release. If its Inventor is closed, the result is `inventor-not-running`
and names the release.

Two Inventor processes of one release each have their own bridge, and each bridge accepts up to 16 client connections.
Start the second one yourself, or ask Claude to start it (`inventor_start` with `newInstance`).
In a session that did not start it, a tool then returns `instance-required` with the
id, the start time and the window title of each process, and Claude asks you which Inventor to use, then calls
`inventor_use_release` with the release and the `processId`. The retry of a call after a dropped connection never goes
to another process, because it could then change other documents. After you restart Inventor, the next call uses the
new process when it is the only one.

| Error | Meaning |
| --- | --- |
| `release-required` | More than one release runs, and this session has not chosen one. Call `inventor_use_release`. |
| `instance-required` | More than one Inventor of the release runs. Call `inventor_use_release` with the release and the `processId` of the Inventor to use. `inventor_session` lists the processes. |
| `bridge-outdated` | An Inventor hosts an add-in of protocol version 2 or older, which uses an old pipe name. Close Inventor, redeploy the add-in for each release, and start it again. |
| `version-mismatch` | An Inventor hosts an add-in that is newer than this server. Restart the MCP client, so it starts the new server. |
| `bridge-connections-in-use` | The bridge of the release exists, but every one of its 16 connections is in use. Close an MCP client that is not in use. Waiting does not help. |
| `untrusted-bridge-host` | The pipe belongs to a different Windows user, or its process is not this user's Inventor. The server did not use it. |

A Claude session that started before an upgrade keeps its old server, with the old pipe name, until you restart it.

</details>
