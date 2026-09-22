---
name: Build and Test
description: Build commands per Inventor release, log locations, and how to drive the stdio server by hand
triggers:
  - Running dotnet build, deploying the add-in, or building the bundle
  - Verifying a change compiles or marking a coding task complete
  - A file lock during a build
  - Reading logs or driving InventorMcp.Server with a test client
---

## Build and test

```
dotnet build InventorMcp.slnx
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -p:AutodeskVersion=2025 -p:DeployAddIn=true
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -c Release -p:AutodeskVersion=2025 -p:DeployBundle=true
```

`AutodeskVersion` selects the Inventor release and defaults to 2027. Supported: 2025, 2026, 2027.
The add-in builds to `bin\<configuration>\<version>`, so the three releases never overwrite each other.
Run the bundle command once per release: each adds its own manifest and `Contents` subfolder to one
version independent bundle, and Inventor loads only the manifest matching its own version.
See `Docs/Architecture.md`.

## Changing the add-in costs an Inventor restart

Rebuilding `InventorMcp.AddIn` requires **Inventor closed**.
Unloading the add-in through the Add-In Manager runs `Deactivate` but does not release the assembly,
because Inventor's assembly load context is not collectible, so the build still fails with a file lock.

## Logs

| Path | Contents |
| --- | --- |
| `%LOCALAPPDATA%\InventorMcp\addin.log` | Add-in lifecycle and handler failures |
| `%LOCALAPPDATA%\InventorMcp\addin-startup.log` | Loader failures on 2025 and 2026, before `addin.log` exists |
| `%LOCALAPPDATA%\InventorMcp\server-<date>.log` | MCP server activity |
| `%LOCALAPPDATA%\InventorMcp\executed-code.log` | Every snippet run through the execution tools |

## Driving the server by hand

The server is stdio, so a test client must **hold standard input open**.
Closing it immediately makes the transport reach end of input before the host starts: handlers run, but responses
go nowhere. The log gives it away, with "transport completed reading messages" appearing before "Application started".

A server process whose stdin never closes stays alive and locks `InventorMcp.Server.exe`, which then blocks a rebuild.

## A closed Inventor session must not wedge the server

Disposing the pipe's `StreamWriter` flushes, and that throws on a dead pipe,
so `BridgeClient.CloseAsync` tolerates `IOException`. Without that, the reconnect never runs.
