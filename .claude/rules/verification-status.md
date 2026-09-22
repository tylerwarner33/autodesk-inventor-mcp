---
name: Verification Status
description: Which tools and behaviors were verified against a live Inventor session, and which were not
triggers:
  - Asked what works, what is tested, or what is left to do
  - Planning verification for a change, or choosing the next task
  - Touching the activity feed ring buffer
---

## What is verified, and what is not

Everything below was checked against a live Inventor 2027 session, including a production assembly with
23 open documents, 91 parameters and eight iLogic rules.

Verified: the pipe and main thread dispatcher, the activity feed, parameter reads and writes across numeric, text
and boolean kinds, expression evaluation, iProperty reads and writes, the assembly tree including suppressed
occurrences and the node and depth budget, health reporting including a genuinely sick feature, document targeting
by name, the error paths for a missing document and a wrong document type, `inventor_update`, both execution tools,
the API lookup, and assembly isolation.

Inventor 2025 and 2026 were verified through the loader shim: the session, the API lookup, both execution tools,
and isolation with iLogic's own Roslyn loaded.

On 2026-09-22, on Inventor 2025: `inventor_eval_csharp` with no document open (`Document` null), and the plugin
development loop, which ran StrobicConfigurator's Design Automation path on nine payloads inside the live session.
See `plugin-loop.md`. The `inventor_orientation` no-document message was verified against the rebuilt server.

`inventor_run_plugin` and `inventor_drawing_layout` were verified on 2026-09-22 against the rebuilt server: nine
StrobicConfigurator payloads run one call each, and all nine generated drawings measured with no issue. Named
binding works against a plugin built with embedded interop types, where `Type` identity does not match.

`inventor_run_plugin`'s log handling that reports only the lines a call wrote was verified later on 2026-09-22
through eleven live calls against StrobicConfigurator's shared rolling log.
The server's warning on an execution result over 20 s was built then too, but no call has run that long since.

Not yet run: the plugin loop on Inventor 2027.

Not yet exercised: the ring buffer's `droppedEntries` counter, which needs more than 2000 buffered events.
Two known gaps have their own task documents in `Docs/Tasks/`:
`Busy-Inventor-Call-Rejection.md` and `Feature-Error-Messages.md`.
