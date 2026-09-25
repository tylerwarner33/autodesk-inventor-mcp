# Server Test Project Plan

Created: 2026-09-25

Status: **open.** The repository has no test project. The first feature that needs one is the blocking dialog
detection in `Docs/Research/Blocking-Dialog-Detection.md`. This plan sets up a test project that can also serve
later server work.

## Goal

- Test the server logic with no Inventor: the dialog close policy, the bridge response match, and the watchdog.
- Test the dialog detection against real Windows dialogs with no Inventor.
- Test the full path against a live Inventor only when a developer asks for it.
- Run all default tests with `dotnet test InventorMcp.slnx`.

## Decisions

1. **Three levels.** Each level has a trait `Level`, so a filter can select it.

	| Level | Needs | Runs by default |
	| --- | --- | --- |
	| `Unit` | Nothing | Yes |
	| `Desktop` | An interactive Windows desktop. No Inventor. | Yes, on a developer machine |
	| `Live` | Inventor with the add-in loaded | No. Only when `INVENTORMCP_LIVE_TESTS=1` |

2. **The test project targets `net10.0-windows`.** NETSDK1146 applies only to a project with `PackAsTool`, and the
	test project is not packed. The server stays `net10.0` and uses `Interop.UIAutomationClient` (see "UI Automation
	without the Desktop Runtime" in the research). Only the fixture uses `UseWindowsForms` and `UseWPF`, because only
	the fixture shows dialogs.
3. **xUnit v3.** Use `Assert.SkipUnless` for the live level, so a skipped test shows the reason. xUnit v3 4.x runs
	only on Microsoft.Testing.Platform. The root `global.json` selects it for `dotnet test`, so the project needs no
	`Microsoft.NET.Test.Sdk` and no `xunit.runner.visualstudio`.
4. **A fixture process shows the dialogs.** The desktop level needs real dialogs in a different process:
	- The server reads Inventor from outside its process. The test must do the same.
	- A UI Automation call from a thread into a dialog that the same thread owns can deadlock.
5. **No parallel run for the desktop and live levels.** They use the same desktop and the same Inventor. Put them in
	one xUnit collection with `DisableParallelization = true`. Unit tests can run in parallel.
6. **Fake time for the watchdog.** The watchdog waits 3 s, then checks every 2 s. Inject `TimeProvider` and use
	`FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`), so the unit tests do not wait.

## Layout

```
Tests/
	InventorMcp.Server.Tests/
		InventorMcp.Server.Tests.csproj
		Unit/
			DialogPolicyTests.cs
			BridgeResponseMatchTests.cs
			WatchdogTests.cs
			PipeProcessIdTests.cs
		Desktop/
			BlockingDialogsTests.cs
			DialogFixture.cs          (starts and stops InventorMcp.TestDialogs.exe)
		Live/
			LiveDialogTests.cs
			LiveInventorFact.cs       (skips unless INVENTORMCP_LIVE_TESTS=1)
	InventorMcp.TestDialogs/
		InventorMcp.TestDialogs.csproj
		Program.cs
```

## Project files

`Tests/InventorMcp.Server.Tests/InventorMcp.Server.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

	<PropertyGroup>
		<!-- Not packed, so a platform target framework is allowed. The server stays net10.0 for PackAsTool. -->
		<TargetFramework>net10.0-windows</TargetFramework>
		<IsPackable>false</IsPackable>
		<OutputType>Exe</OutputType>
	</PropertyGroup>

	<ItemGroup>
		<PackageReference Include="xunit.v3" />
		<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
	</ItemGroup>

	<ItemGroup>
		<Using Include="Xunit" />
	</ItemGroup>

	<ItemGroup>
		<ProjectReference Include="..\..\Source\InventorMcp.Server\InventorMcp.Server.csproj" />
		<!-- Copies the fixture exe next to the tests, so DialogFixture starts it from AppContext.BaseDirectory. -->
		<ProjectReference Include="..\InventorMcp.TestDialogs\InventorMcp.TestDialogs.csproj" />
	</ItemGroup>

</Project>
```

`Tests/InventorMcp.TestDialogs/InventorMcp.TestDialogs.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

	<PropertyGroup>
		<OutputType>WinExe</OutputType>
		<TargetFramework>net10.0-windows</TargetFramework>
		<UseWindowsForms>true</UseWindowsForms>
		<UseWPF>true</UseWPF>
		<IsPackable>false</IsPackable>
	</PropertyGroup>

</Project>
```

Other changes:

- `Directory.Packages.props`: add `PackageVersion` items for the two test packages, at the current stable versions.
- `global.json`: `{ "test": { "runner": "Microsoft.Testing.Platform" } }`.
- `Source/InventorMcp.Server/InventorMcp.Server.csproj`: add
	`<ItemGroup><InternalsVisibleTo Include="InventorMcp.Server.Tests" /></ItemGroup>`. The server types are `internal`.
- `InventorMcp.slnx`: add both projects in a `/Tests/` folder.
- `.agents/rules/build.md`: add the test commands (see "Commands").

`Directory.Build.props` applies to the test projects too. `TreatWarningsAsErrors` is on, so the test code must
build with no warnings. The Inventor properties in it have no effect on these projects.

Verify on the first build that the `ProjectReference` to the fixture exe copies `InventorMcp.TestDialogs.exe`
and its `runtimeconfig.json` to the test output. If it does not, set `ReferenceOutputAssembly="false"` and copy the
fixture output with a target.

## The fixture process

`InventorMcp.TestDialogs.exe <mode> [--close-after <ms>]` opens a main window with the title
`InventorMcp Test Main`. It then opens one modal dialog, owned by the main window. The main window is then disabled,
the same as the Inventor main frame. It writes `READY <hwnd of dialog>` to standard output when the dialog is visible.

| Mode | Dialog | Why |
| --- | --- | --- |
| `none` | No dialog | The not-blocked state |
| `ok` | `MessageBox.Show(owner, "Test message", "Test OK")` | Win32 message box, class `#32770`, one button |
| `yesno` | `MessageBox.Show(owner, "Save?", "Test Question", MessageBoxButtons.YesNo)` | A question. It must stay open. |
| `okcancel` | Message box with `OK` and `Cancel` | Two buttons. It must stay open. |
| `ilogic-like` | WinForms form, title `Error on line 1 in rule: Test, in document: Test.ipt`, a multiline read-only `TextBox`, a visible `OK`, and hidden `Apply`, `Extra`, `Second`, `Cancel` buttons. A second tab page with a stack trace text. | The copy of the real iLogic dialog. Tests the hidden button filter and the text of a tab that is not selected. |
| `wpf` | WPF `Window` with a `TextBlock` and one `Button` named `OK` | WPF controls have no window handle. Tests the `IsOffscreen` path. |
| `unknown-ok` | WinForms form with a custom title and only `OK` | Not in the catalog. It must stay open. |
| `hang` | `ok`, then the UI thread sleeps for 30 s | UI Automation gets no answer. Tests the read timeout. |

`--close-after <ms>` closes the dialog after the given time. It tests a dialog that closes between the detection
and the click.

## Changes to the server that make it testable

| Change | Why |
| --- | --- |
| `BridgeClient` takes the pipe name as a constructor parameter. Default: `BridgeProtocol.PipeName`. | A unit test hosts its own `NamedPipeServerStream`. |
| `BlockingDialogs` takes a main window test as a parameter. Default: class name starts with `AfxMDIFrame`. | The fixture's main window takes the place of the Inventor main frame. |
| The close policy is a pure static function of a dialog snapshot (title, class, framework, visible and enabled buttons) and the setting. | Unit tests with no windows. |
| `BridgeClient` uses an `IBlockingDialogs` interface and a `TimeProvider`. | The watchdog tests use a fake detector and fake time. |

## Test cases

### Unit

| Test | Expected |
| --- | --- |
| Policy: iLogic error title, only `OK` visible | Close |
| Policy: class `#32770`, only `OK` visible | Close |
| Policy: `OK` visible, `Cancel` hidden | Close. Hidden buttons do not count. |
| Policy: `OK`, and `Close` in the title bar | Close. Title bar buttons do not count. |
| Policy: `Yes` and `No` | Leave open |
| Policy: `OK` and `Cancel` visible | Leave open |
| Policy: unknown title and class, only `OK` | Leave open |
| Policy: iLogic error, setting off | Leave open |
| Response match: the test pipe sends a response with an old ID, then the correct response | The client returns the correct result. It discards the old line. |
| Response match: the pipe closes after an old response only | `IOException` path, then one reconnect, as now |
| Watchdog: fake detector reports not blocked; fake time moves 10 s; then the response arrives | The call returns the result with no dialog field |
| Watchdog: fake detector reports an information dialog at 3 s | `TryClick` is called one time. The result has `blockingDialogs` with the text. |
| Watchdog: fake detector reports a question at 3 s | The call stops with `BlockedByDialog`. `Detail` has the text and the buttons. No click. |
| Watchdog: no response in the first 3 s | No detection before 3 s of fake time |
| Pipe process ID: the test hosts a pipe and connects | `GetNamedPipeServerProcessId` returns the test process ID |
| One clicker: two detectors race for the same dialog | Only one `TryClick` succeeds. The other reports "closed by a different server". |

### Desktop

Each test starts the fixture, waits for `READY`, and stops the fixture at the end, also after a failure.

| Test | Fixture mode | Expected |
| --- | --- | --- |
| Not blocked | `none` | `Blocked = false`. No dialogs. |
| Detect and read | `ok` | `Blocked = true`. One dialog. Text `Test message`. Buttons: `OK`. |
| Hidden buttons | `ilogic-like` | Visible buttons: `OK` only. The text of both tabs is in the result. |
| WPF | `wpf` | The text and the `OK` button are found. |
| Click | `ok` | `TryClick("OK")` succeeds. Then `Blocked = false`. The fixture exits. |
| Refuse a question | `yesno` | The policy says leave open. The dialog is still open after the watchdog period. |
| Refuse a hidden button | `ilogic-like` | `TryClick("Cancel")` refuses: no visible, enabled match. |
| Dialog closes early | `ok --close-after 200` | Detection, then `TryClick` after 500 ms. No exception. Result: "dialog closed". |
| Read timeout | `hang` | The read stops at the time limit. The detector returns the block with an unread dialog. It does not wait 30 s. |

### Live

Skipped unless `INVENTORMCP_LIVE_TESTS=1`. Needs Inventor running with the add-in, and no unsaved documents.
Each test goes through `BridgeClient`, the same as a tool call.

| Test | How to cause it | Expected |
| --- | --- | --- |
| iLogic error through automation | `inventor_eval_csharp`: get `ApplicationAddIns.ItemById["{3BDD8D79-2179-4B11-8A5A-257B1C0263AC}"].Automation`, then `RunRule` on a temporary part with a rule that calls `iLogicVb.RunExternalRule("DoesNotExist")` | Closed after approximately 3 s. The result has the dialog text. This is the path of the real incident. |
| iLogic error through the tool | `inventor_run_ilogic` with `iLogicVb.RunExternalRule("DoesNotExist")` | Closed. The result has the text. |
| Silent operation | The first test | Confirms that the dialog opens although `SilentOperationScope` is active. If no dialog opens, record that in the research. |
| Message box, OK only | `inventor_eval_csharp` with `System.Windows.Forms.MessageBox.Show("test")` | Closed. The result has the text. |
| Question | `MessageBox.Show("test", "t", MessageBoxButtons.YesNo)` | Left open. `BlockedByDialog` error with the buttons `Yes` and `No`. The test then closes it with `No`. |
| Stale response | After the question test, run a new call | The new call gets its own response, not the old one. |
| Two servers | Two `BridgeClient` instances wait while the iLogic error opens | One click. Both results have the text. |
| Setting off | `INVENTORMCP_AUTOCLOSE_DIALOGS=false` | Nothing is clicked. `BlockedByDialog` error. |
| Health while blocked | `inventor_health` during the question test | Returns `blockedByDialog` immediately, with no bridge call. |

Notes for the live tests:

- If the message box does not disable the Inventor main frame, give the owner explicitly: a `NativeWindow` with
	`AssignHandle(new IntPtr(Application.MainFrameHWND))`.
- Verify that `System.Windows.Forms` can be used in a snippet. If it cannot, use the iLogic `MessageBox` in
	`inventor_run_ilogic` for the message box cases.
- iLogic shows the same error for the same rule only one time in 120 minutes. Use a new rule name in each run
	(ex. with a time stamp), or the second run shows no dialog.

### Manual, one time

- **iLogic Security Alert.** Cause it (a rule text edit through the API, then a run). Read it with
	`Inventor-Dialog.ps1 -Action Read`. Record the title, class and buttons in the research. Confirm that the policy
	leaves it open.

## Commands

```
dotnet test InventorMcp.slnx
dotnet test InventorMcp.slnx --filter-not-trait Level=Desktop
```

The live level, in PowerShell:

```
$env:INVENTORMCP_LIVE_TESTS = '1'; dotnet test InventorMcp.slnx --filter-trait Level=Live
```

The live level, in `cmd`:

```
set "INVENTORMCP_LIVE_TESTS=1" && dotnet test InventorMcp.slnx --filter-trait Level=Live
```

Use the quotes in `cmd`. Without them, `set INVENTORMCP_LIVE_TESTS=1 && ...` sets the value to `1` with a trailing
space. In PowerShell, `set` is `Set-Variable`, which does not set an environment variable.
`LiveInventorFact` trims the value before it compares it with `1`.

The desktop level needs an interactive desktop. A build agent that runs as a Windows service has none, so use the
second command there. The filter syntax was verified with xunit.v3 4.0.1 on SDK 10.0.401. A filter that selects no
test ends with exit code 8.

## Phases

### Phase 1 - Project setup

- [x] Add both projects, the package versions, `InternalsVisibleTo` and the solution entries.
- [x] Add one unit test that passes, and confirm that `dotnet test InventorMcp.slnx` runs it.
- [x] Confirm that the fixture exe is in the test output. The `ProjectReference` copies the exe and its
	`runtimeconfig.json`, so no extra target is necessary.

### Phase 2 - Fixture and desktop tests

- [ ] Write `InventorMcp.TestDialogs` with all modes.
- [ ] Write `DialogFixture`: start, wait for `READY` with a time limit, stop.
- [ ] Port the detection from the research appendix script into `Services/BlockingDialogs.cs`.
- [ ] Give `BlockingDialogs` the main window test as a parameter (see "Changes to the server that make it
	testable"). The desktop tests need it, because the fixture main window is not an `AfxMDIFrame` window.
- [ ] Make the desktop tests pass.

### Phase 3 - Unit tests with the watchdog

- [ ] Make the other server changes in "Changes to the server that make it testable".
- [ ] Write the policy, response match, watchdog, pipe process ID and one clicker tests.

### Phase 4 - Live tests

- [ ] Write `LiveInventorFact` and the live tests.
- [ ] Run them against Inventor 2026. Record the results in `.agents/rules/verification-status.md`.
- [ ] Do the manual Security Alert check.

## Not in this plan

- Tests for the other tools. The project makes them possible, but they are separate work.
- A build pipeline. The repository has none now.
- Tests for the add-in. The add-in runs inside Inventor, and each change needs an Inventor restart.

## When the plan is done

- `dotnet test InventorMcp.slnx` runs the unit and desktop levels with no failures.
- The live level passed one time against a live Inventor, and `verification-status.md` records it.
- `.agents/rules/build.md` has the test commands.
