# Busy Inventor: RPC_E_CALL_REJECTED

Created: 2026-09-21

Status: **handled, but never observed.** The code path exists and has never run, because Inventor has not yet been
busy enough to reject a call. This document exists so it can be addressed properly if it ever becomes relevant.

## What happens

Inventor is a single threaded apartment COM server. While it is running a command, rebuilding, or showing a modal
dialog, it can refuse an incoming call rather than queue it. The caller receives a `COMException` with one of:

| HRESULT | Name | Meaning |
| --- | --- | --- |
| `0x80010001` | `RPC_E_CALL_REJECTED` | The call was refused outright |
| `0x8001010A` | `RPC_E_SERVERCALL_RETRYLATER` | The server is busy, try again |

Both are transient. The right response is to wait and retry, not to fail.

## What this repository does today

`OperationDispatcher.DispatchAsync` catches both HRESULTs and converts them into a structured result:

```
{
  "error": "inventor-busy",
  "message": "Inventor rejected the call because it is busy. Retry once the current command finishes."
}
```

So the model gets a readable explanation rather than an opaque failure, and can decide whether to retry.
There is **no automatic retry**.

Two other mitigations reduce how often this should happen at all:

- Write operations run inside a `SilentOperationScope`, so a modal dialog does not sit unanswered.
- `inventor_activity` reads a managed ring buffer rather than calling into Inventor, so the event feed keeps
	working even while Inventor is busy. That is deliberate: it is the one tool that must not fail during an
	automation loop.

## Why it is unproven

Every session so far has driven Inventor between commands, never during one.
The dispatcher marshals work onto Inventor's main thread through a message only window, so a request simply waits
its turn in the message queue instead of being rejected.

That may mean rejection is rare in this design, since the calls are in process rather than cross process.
It is not a safe assumption, only an untested one.

## How to reproduce it

Start a long running operation and call a tool while it is running:

- Rebuild a large assembly, or run an iLogic rule that rebuilds, then call `inventor_parameters` immediately.
- Open a modal dialog by hand, ex. the Parameters dialog, then call any tool other than `inventor_activity`.
- Run an automation loop that creates features in a tight sequence, and poll `inventor_health` throughout.

Expected today: either the call waits, or it returns `inventor-busy`.
Anything else, ex. a hang or an unhandled `COMException`, is a real defect worth fixing.

## What to add if it does become a problem

Ordered by cost.

1. **Retry with backoff in the server.** `BridgeClient` already retries once on a dropped pipe.
	A retry on `inventor-busy`, with a short delay and a small attempt count, would be a contained change,
	entirely server side, and therefore needs no Inventor restart.
2. **An `IOleMessageFilter` in the add-in.** The standard COM answer: implement `MessageFilter` and return
	`SERVERCALL_RETRYLATER` handling so the runtime retries automatically. This is what a cross process automation
	client normally needs. It is an add-in change, so it costs an Inventor restart, and it may be unnecessary
	given the add-in is in process.
3. **Report busy state up front.** `inventor_session` already returns `isBusy`, taken from `Application.Ready`.
	A caller could check it before a long sequence rather than discovering the problem mid way.

## Decision

Do nothing until it is observed. The current behaviour fails clearly and safely, which is enough until there is
evidence that a retry is actually needed.
