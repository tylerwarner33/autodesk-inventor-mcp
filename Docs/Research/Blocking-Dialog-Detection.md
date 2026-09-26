# Blocking Dialog Detection

Created: 2026-09-24

Status: **research.** A modal dialog in Inventor blocked the server during real use. A test from a separate
process found the dialog, read all of its text, and found its buttons. This document records the test, the
method, and a proposal to put the method into the server. The test work is in
`Docs/Tasks/Server-Test-Project-Plan.md`.

Related documents:

- `Docs/Tasks/Usage-Findings-Implementation-Plan.md`, decision 6 ("Find a modal dialog from outside Inventor's
	process") and Phase 4 (the dialog check in `inventor_session`). This document gives the tested method for them.
- `Docs/Research/Usage-Findings-And-Knowledge-Delivery.md` found one earlier block: an iLogic Security Alert after
	a rule edit through the API. Its rule "Never answer a dialog on the user's behalf" conflicts with the automatic
	close in this proposal. See "Decision needed" below.
- `Docs/Architecture.md`, "Writes suppress Inventor's dialogs". This block happened under `SilentOperation`.
	See "Prevention" below.

## The problem

On 2026-09-24, one Claude Code session ran the iLogic rule `Drawing_Main` in `Frame Shop Drawing.idw`.
Line 16 of the rule calls `RunExternalRuleWithArguments("Drawing_External", ...)`. iLogic did not find
the external rule file, and it showed its modal error dialog:

```
Error on line 16 in rule: Drawing_Main, in document: Frame Shop Drawing.idw

RunExternalRule: Cannot find an external rule file named: "Drawing_External"
(referenced in the document Frame Shop Drawing.idw).
```

The dialog runs a modal message loop on Inventor's main thread. The add-in marshals every operation to that thread,
so every tool call from every connected server waits until someone closes the dialog. The server cannot see why the
call does not return. The model sees only a timeout, and it cannot close the dialog with an Inventor tool.
The user saw the dialog and told the model. The model did not find it on its own.

Evidence in `%LOCALAPPDATA%\InventorMcp\executed-code.log`:

| Time (UTC) | Entry |
| --- | --- |
| 2026-09-24 20:56:29 | `inventor_eval_csharp` opens a test copy of `Frame Shop Drawing.idw` in a Claude Code scratchpad folder. |
| 2026-09-24 20:56:33 | `inventor_eval_csharp` gets the iLogic add-in's `Automation` object and calls `auto.RunRule(doc, "Drawing_Main")`. The dialog opens during this call. |
| 2026-09-24 21:07:53 | The next call from that session. It reads `auto.FileOptions.ExternalRuleDirectories` and the active project. |

So the session was blocked for approximately 11 minutes, until the user closed the dialog by hand.
The drawing was a copy outside the project workspace, so the external rule was not in a folder that iLogic searched.

## Test results

The test ran from a second Claude Code session, with Windows PowerShell 5.1, in a separate process.
Inventor Professional 2026. The dialog was open during the test.

| Step | Method | Result |
| --- | --- | --- |
| Find the dialog from the desktop | UI Automation `AutomationElement.RootElement.FindAll(Children, ...)`, filtered by process ID | **Failed.** The search found the main frame and two WPF `Pane Border` windows only. The dialog was not in the results. |
| Find the dialog | Win32 `EnumWindows` + `GetWindowThreadProcessId` + `IsWindowVisible` | **Found.** Title, class `WindowsForms10.Window.8.app.0.22c9f37_r3_ad1`, owner = the main frame. |
| Know that Inventor is blocked | Win32 `IsWindowEnabled` on the main frame (class `AfxMDIFrame140u`) | **`False`.** A modal dialog disables its owner. This is a reliable block signal. |
| Read the dialog | `AutomationElement.FromHandle(hwnd)`, then `FindAll(Descendants, TrueCondition)` | **Full text.** The message and the "More Info" stack trace. The "More Info" tab was not selected, but its text was still available. |
| Find the buttons that the user can see | UI Automation `IsOffscreen` | **Wrong.** `False` for all six buttons. |
| Find the buttons that the user can see | Win32 `IsWindowVisible(NativeWindowHandle)` | **Correct.** Only `OK` is visible. `Apply`, `Extra`, `Second`, `Cancel` and a help button are hidden template buttons. |
| Click `OK` | UI Automation `InvokePattern.Invoke()` | **Not tested.** The Claude Code auto mode classifier blocked the click (reason "Interfere With Workloads"), because the dialog belonged to the other session. |

### The iLogic error dialog

- Framework: WinForms with DevExpress controls (`XtraTabControl`, `CommonMemoEdit`).
- Title: `Error on line <n> in rule: <rule>, in document: <document>`.
- AutomationId prefix: `ShowExceptionDialog_<title>`. The prefix `ShowExceptionDialog_` identifies this dialog type.
- Tab 1 (`CommonMemoEdit_RichTextMessage`): the message. Tab 2 (`CommonMemoEdit_RichTextMoreInfo`): the .NET exception and stack trace.
- Buttons: `CommonButton_m_okButton` is the only visible button.
- iLogic does not show the same error for the same rule again for 120 minutes, or until Inventor restarts.
	It writes repeats to the iLogic Log window only. So after the first dialog, the same failure is silent.

The stack trace shows the call path of `RunExternalRuleWithArguments`:

```
at iLogic.ExternalRuleNotFoundHandler.HandleRuleNotFound(String ruleName, Document doc, Boolean isDesktop)
at Autodesk.iLogic.Core.RuleProcessing.RuleRunManager.RunExternalRule(String ruleName, NameValueMap ruleArguments)
...
at Autodesk.iLogic.Automation.iLogicAutomationNonCOM.RunExternalRuleWithArguments(Document doc, String ruleName, NameValueMap ruleArguments)
at ThisRule.Main() in rule: Drawing_Main, in document Frame Shop Drawing.idw:line 16
```

## Why the method works

- **A separate process does not need the main thread.** Win32 window functions and UI Automation run in the
	caller's process. Inventor's modal loop still pumps messages, so the dialog answers UI Automation requests.
- **Inventor uses three UI frameworks.** The main frame is MFC (`AfxMDIFrame140u`), iLogic dialogs are WinForms, and
	panels and many add-in dialogs are WPF (`HwndWrapper[...]`). UI Automation reads all three.
- **Use Win32 to find windows, and UI Automation to read them.** The root UI Automation search missed an owned
	top-level WinForms window. The cause is not known. `EnumWindows` did not miss it.
- **Read text from the patterns.** An edit or rich text control keeps its text in `ValuePattern` or `TextPattern`,
	not in `Name`.
- **Use Win32 to test visibility.** WinForms controls have their own window handle, and UI Automation reported the
	hidden ones as on screen. WPF controls have no handle, so use `IsOffscreen` for them.

## Where the call waits

`BridgeClient.SendAsync` writes the request, then waits on `_reader.ReadLineAsync(cancellationToken)`
(`Source/InventorMcp.Server/Bridge/BridgeClient.cs`). The wait has no timeout. Only the client can cancel it.
While a dialog is open, the add-in does not answer, so the tool call does not return.

The model of the waiting session does not run again until the call returns. So that model cannot detect the
dialog, and it cannot ask for help. The session stays stuck until a person closes the dialog.
**Only the server, or a separate process, can detect the dialog automatically.** The server is the correct
location, because it knows which call waits and it can return the dialog text in that call's result.

## Proposal for the server

The server already runs outside Inventor, in the same user desktop session. So it can do the same detection in C#
with no PowerShell. It gets the Inventor process ID from the pipe connection, with no bridge call (see "Changes",
item 1).

### 1. Detect and handle the block during the wait

Start a watchdog task next to `ReadLineAsync`. After a short time (ex. 3 s), and then every 2 s while the call
waits, check `IsWindowEnabled` on the Inventor main frame. If the main frame is disabled, collect each visible,
enabled, top-level window of the Inventor process that is not the main frame, and read it with UI Automation.
Write the text to the server log immediately. Then apply the policy:

| Dialog | Action by the server |
| --- | --- |
| Information dialog: exactly one visible, enabled button, named `OK`, and a known type (the iLogic error dialog, or a Win32 message box, class `#32770`) | Click `OK`. The main thread continues, and the call returns. Add the dialog text to the tool result (ex. a `dialogs` field or a warning), so the model knows that the rule failed. |
| All other dialogs (questions, forms, unknown types) | Do not click. Stop the wait and return an error with the dialog text and the visible buttons. The model can then ask the user, or call `inventor_dialogs`. |

Make the automatic close a setting (ex. `InventorMcp:AutoCloseInformationDialogs`, default `true`), so a user can
turn it off.

A dialog that is not in the catalog is always left open. The iLogic Security Alert that the usage research found
is not in the catalog yet. It asks the user to trust a rule, so it is a question, and the server must leave it open.
Read a real example with the appendix script before it is added.

#### Decision: close information dialogs, default on

Decided on 2026-09-25: the automatic close is on by default, and `INVENTORMCP_AUTOCLOSE_DIALOGS=false` turns it off.

The usage research and its knowledge text say: "Never answer a dialog on the user's behalf, ex. an iLogic Security
Alert. Tell the user and wait." The automatic close of an information dialog changes that rule. The change is small:
the server clicks only `OK` on a dialog that has no other button, so the click decides nothing. The rule text
changes together with the feature, to: "Never answer a question dialog on the user's behalf, ex. an
iLogic Security Alert. The server closes an information dialog that has only `OK`, and gives its text in the result."

**Stale response.** If the server stops the wait before the add-in answers, the add-in writes the answer later.
The next call then reads the wrong line. Match each response to its request ID (`BridgeRequest` already has one)
and discard old lines, or close and reconnect the pipe after a stopped wait.

The error for a dialog that the server does not close:

```
Inventor is blocked by a modal dialog. The call cannot run until the dialog is closed.
Dialog: 'Error on line 16 in rule: Drawing_Main, in document: Frame Shop Drawing.idw' (WinForms, iLogic error)
Text: RunExternalRule: Cannot find an external rule file named: "Drawing_External" ...
Visible buttons: OK
Use inventor_dialogs to close it, or ask the user.
```

The call that opened the dialog is also the call that waits on it. So the check must run on a timer in the server,
not after the bridge call returns.

A dialog can also open when no call waits (ex. an event-triggered rule after a user action). The next call then
waits on it. The same watchdog handles that case.

### 2. Add a tool: `inventor_dialogs`

- `list` (read-only): the block state and the open dialogs, with a handle for each dialog.
- `read` (read-only): the full text, and each control with its visible and enabled state.
- `click`: click one visible, enabled button, given the handle and the button name. Refuse when zero or more
	than one button matches.

Make `click` safe:

- Mark the tool as destructive in its MCP annotations, so clients can ask the user before a click.
- Before the click, read the dialog again and compare the title with the title that `list` returned.
	This prevents a click on a different dialog that uses the same handle.
- After the click, run `list` again and return the new state. A rule can show a second dialog after the first.
- Write each click to the audit log (`executed-code.log`), with the dialog title and text.

### 3. Add a health field

Add `blockedByDialog` (the dialog title, or null) to `inventor_health` and `inventor_session`.
Both tools then report a block instead of a timeout.

### 4. Record the dialog text

After a dialog closes, its text is gone. iLogic does not show the same error again for 120 minutes.
So write the text of each dialog that the server finds to the server log, even when no one clicks it.

### 5. Put the knowledge in the tool descriptions

Put this in the server instructions: "If an Inventor call does not return, call `inventor_dialogs` with `list`.
Do not close a dialog that asks a question (ex. Save changes?) without asking the user."

## Requirements for the server approach

Examined in the source on 2026-09-25.

### No add-in change

- The add-in already sends the request ID back in each response (`OperationDispatcher.cs`,
	`new BridgeResponse(request.Id, ...)`). So the server can discard a stale response with no add-in change.
- The auto-close path needs no add-in change: after the click, the main thread continues, and the original call
	returns normally.
- So all work is in `InventorMcp.Server`. A release needs a client reconnect only. Inventor does not restart.

### Dependencies

| Need | Source | Note |
| --- | --- | --- |
| Find windows, block state, visibility | Win32 `user32.dll` with `LibraryImport`: `EnumWindows`, `GetWindowThreadProcessId`, `IsWindowVisible`, `IsWindowEnabled`, `GetWindowText`, `GetClassName`, `GetWindow` | The server already uses Win32 in `DetachedProcess.cs`. |
| Read text, find and click buttons, WPF dialogs | The UI Automation COM API, through the NuGet package `Interop.UIAutomationClient` | See "UI Automation without the Desktop Runtime" below. The target framework stays `net10.0`. |

### Target framework test

The assembly attribute `SupportedOSPlatform("windows")` only controls the platform analyzer (CA1416). It does not
add the Windows Desktop framework, so it does not give access to `System.Windows.Automation`.
A framework reference does. Tested on 2026-09-25 with SDK 10.0.401:

| Project | Build | Pack as tool | Run with `dotnet tool exec` |
| --- | --- | --- | --- |
| `net10.0` + `FrameworkReference Microsoft.WindowsDesktop.App.WPF` + `PackAsTool` | Pass | Pass | Pass. `AutomationElement.RootElement.FindAll` returned the top-level windows. |
| `net10.0-windows` + `UseWPF` + `PackAsTool` | Pass | **NETSDK1146** | Not possible |

The first project writes `Microsoft.WindowsDesktop.App` to its `runtimeconfig.json`. So the machine needs the
.NET 10 Desktop Runtime, not only the .NET Runtime. NETSDK1146 also says that `PackAsTool` does not support
`UseWPF`, but the SDK does not check a direct framework reference. A later SDK can add that check.

### UI Automation without the Desktop Runtime

Decision (2026-09-25): the server uses the NuGet package `Interop.UIAutomationClient`, not the WPF framework
reference. Then a user needs no Desktop Runtime, and a later SDK check on framework references cannot break the
tool package.

The package, examined on 2026-09-25:

| Item | Result |
| --- | --- |
| Owner | NuGet account `Roemer` (Roman, Bern). He also wrote FlaUI, and `FlaUI.UIA3` depends on this package. |
| Downloads | 4.3 million in total, 3.3 million of them for 10.19041.0. Most arrive through `FlaUI.UIA3` (4.6 million). |
| Version | 10.19041.0, published 2020-07-17. It matches the Windows 10 2004 type library. The UI Automation COM API has not changed since then. |
| License | MIT |
| Known vulnerabilities | None on nuget.org. `NuGetAuditMode` `all` reports none. |
| Contents | `Interop.UIAutomationClient.dll` for `netstandard2.0` and others, a `build` `.targets` file, and `tools/install.ps1` |
| Code in the assembly | None. 2096 methods, 0 with an IL body. The only assembly reference is `mscorlib`. It is a type library import, and it only declares the COM interfaces. |
| `build/Interop.UIAutomationClient.targets` | Sets `EmbedInteropTypes` to false for its own assembly. Nothing else. |
| `tools/install.ps1` | The same change for old `packages.config` projects in Visual Studio. `PackageReference` never runs it. |
| Signature | The nuget.org repository signature. The package has no author signature, and the ID has no reserved prefix. |
| Source repository | `github.com/Roemer/UIAutomation-Interop` did not answer during the check. |

The test (2026-09-25, SDK 10.0.401): a `net10.0` project with `PackAsTool` and only this package, packed to a
local feed and run with `dotnet tool exec`. Its `runtimeconfig.json` names only `Microsoft.NETCore.App`. A second
process showed each dialog. The tool found it with `EnumWindows`, read it with `CUIAutomation8.ElementFromHandle`
and `FindAll(TreeScope_Descendants)`, and clicked `OK` with `IUIAutomationInvokePattern.Invoke()`. The fixture
process then exited each time.

| Dialog | Framework | Text read | `OK` found and clicked |
| --- | --- | --- | --- |
| `MessageBox.Show` | Win32, class `#32770` | Yes | Yes |
| WinForms form with a multiline `TextBox` and a hidden `Cancel` | WinForm | Yes, both lines, from `ValuePattern` | Yes |
| WPF `Window` with a `TextBlock` and a `Button` | WPF | Yes | Yes |

Findings from the test:

- **The title bar has buttons.** Each dialog also showed `Minimize`, `Maximize` and `Close` (the message box only
	`Close`) as visible, enabled buttons with no window handle, below a `TitleBar` element. The policy "exactly one
	visible button" must skip the buttons of the title bar, or it never closes a message box.
- **A hidden WinForms button does not always show.** The hidden `Cancel` of a plain WinForms form was not in the
	UI Automation tree. The DevExpress buttons of the real iLogic dialog were in the tree (see "Test results"). The
	Win32 visibility test is still necessary for them.
- **A standard WinForms tab that was never shown has no windows.** In the desktop test fixture, the second tab page
	of a `TabControl` and the hidden buttons had no child window at all (checked with `EnumChildWindows`). WinForms
	creates the controls of a tab only when it opens. So no process can read that text from outside, with UI
	Automation or with Win32. The real iLogic dialog gave the text of its second tab (see "Live test results").
- **WPF controls have no window handle.** The `IsOffscreen` test gave the correct answer for the WPF `OK` button.
- The WPF `OK` button showed two times: the button, and the text element inside it. Match buttons by control type.

Set a timeout for UI Automation calls (ex. run each read on a task with a 2 s limit). A dialog that does not answer
must not stop the watchdog.

### Changes

1. **Inventor process ID without the main thread.** `SessionInfo.ProcessId` comes from a bridge call, and that call
	also waits on the dialog. Get the ID from the pipe: `GetNamedPipeServerProcessId(pipe.SafePipeHandle)` after the
	connect in `BridgeClient.EnsureConnectedAsync`. This gives the process that hosts the add-in, also when more than
	one Inventor runs. Store it in `BridgeClient`.
2. **New service `Services/BlockingDialogs.cs`.** `Detect(processId)` returns the block state and, for each dialog:
	handle, title, class, UI framework, text, and visible, enabled buttons. `TryClick(dialog, buttonName)` reads the
	dialog again, compares the title, then clicks. Port the logic from the appendix script.
3. **Watchdog in `BridgeClient.SendAsync`.** Replace the single `ReadLineAsync` with a loop:
	- Wait with `Task.WhenAny` for the response or a 2 s timer (first check at 3 s).
	- On the timer, call `Detect`. If blocked, apply the policy. After an automatic close, continue to wait.
		For a dialog that needs a person, stop the wait and throw `InventorBridgeException` with a new code
		(ex. `BlockedByDialog`) and the dialog text in `Detail`.
	- Discard each response with an ID that is not the ID of the current request.
4. **Only one server clicks.** Up to 16 servers can connect to one Inventor (`MaxPipeInstances`), and each waiting
	server sees the same dialog. Use a named mutex, ex. `Local\InventorMcp.Dialogs.<processId>`. The server that
	gets the mutex reads the dialog again and clicks. The other servers report the dialog as "closed by a different
	server".
5. **Give the dialog to the model.** `InventorTool.SafeAsync` is the one path of all tools. Let `BridgeClient`
	record the dialogs that it saw during the call (ex. in an `AsyncLocal` or in the call result), and let
	`SafeAsync` add them to the result, ex. `{ result, blockingDialogs: [...] }`, or to the error object.
6. **Health without the bridge.** `inventor_health` and `inventor_session` also use the bridge, so they also wait.
	Compute `blockedByDialog` in the server before the bridge call, and return it immediately when blocked.
7. **New tool `inventor_dialogs`** (`list`, `read`, `click`). It uses only the server-side service, so it works
	while Inventor is blocked. Mark `click` as destructive.
8. **Setting.** The server has no configuration system now. Add an environment variable, ex.
	`INVENTORMCP_AUTOCLOSE_DIALOGS=false`, which clients can set in `.mcp.json`. Default: on.
9. **Logs.** Write each dialog (text, decision, which server clicked) to the server log. Write each click to the
	audit log.
10. **Server instructions.** Update `ServerInstructions.md`: what `blockingDialogs` and `BlockedByDialog` mean, and
	do not close a dialog that asks a question without asking the user.

### Tests

The repository has no test project now. `Docs/Tasks/Server-Test-Project-Plan.md` sets up
`Tests/InventorMcp.Server.Tests` with three levels (unit, desktop, live), a fixture process that shows real
dialogs with no Inventor, and the live test cases for this feature.

## Live test results

Run on 2026-09-25 against Inventor Professional 2026.2 (Build 302298010), with the live level of
`Tests/InventorMcp.Server.Tests`. The first two runs failed and found the problems below. After the fixes, two runs
in a row passed all six live tests, and each dialog closed approximately 3.2 s after the call started.

| Test | Result after the fixes |
| --- | --- |
| iLogic error through `iLogicAutomation.RunRule` in a C# snippet (the path of the incident) | Closed with `OK`. The result has the dialog text. |
| iLogic error through `inventor_run_ilogic` | Closed with `OK` |
| WinForms message box with `OK`, owned by the main frame | Closed with `OK` |
| Message box with `Yes` and `No` | Left open. `blocked-by-dialog` with `Visible buttons: Yes, No`. A second client read the block with no bridge call in less than 5 s. The next call after the question got its own response. |
| Two clients wait on one iLogic error | One click |
| `INVENTORMCP_AUTOCLOSE_DIALOGS=false` | No click. `blocked-by-dialog`. |

Findings:

- **`SilentOperation` does not stop the iLogic error dialog.** The dialog opened in each test that ran a rule
	through a C# snippet, which runs inside `SilentOperationScope`. This confirms "Prevention" below.
- **A scroll bar adds buttons.** The message of the iLogic error dialog is long enough for scroll bars in its text
	box. UI Automation reports the arrows as visible buttons (`Line up`, `Line down`, `Column left`,
	`Column right`), with a `ScrollBar` parent. So "exactly one visible button" refused to close the dialog. The
	server now skips the buttons of scroll bars and title bars.
- **UI Automation can miss the `OK` button.** In the second run, a walk of the dialog from its own element did
	not return the DevExpress buttons: the pane that holds them (a child window) had no UI Automation children.
	`EnumChildWindows` found them (`OK` visible, `Apply`, `Extra`, `Second`, `Cancel` hidden), and
	`ElementFromHandle` of the `OK` window gave a Button with the ID
	`ShowExceptionDialog_<title>.CommonButton_m_okButton` and the Invoke pattern. The first run had found the same
	button through the dialog. The cause is not known. So the server now also reads each Win32 child window from its
	own handle.
- **The tree of a window holds the windows it owns.** The UI Automation tree of a dialog has the dialogs that it
	owns as children, and the main frame has all of them. A read of one dialog then mixed in the text and buttons
	of another. The server now walks the tree itself, and stops at each element of a different top level window.
	This also explains why the root search in "Test results" did not find the dialog: it is below the main frame.
- **A call runs inside the modal loop of an open dialog.** After a call stopped with `blocked-by-dialog`, the next
	bridge call still ran in Inventor, inside the message loop of the open dialog. Each failed test opened a new
	dialog over the one before, and each outer dialog could close only after the one over it. Only the innermost
	dialog is enabled. So the server now handles the dialogs before it sends a call, and it sends nothing while a
	dialog needs a person.
- **The text of the "More Info" tab was readable** in the real dialog, from a tab that was not selected. So the
	limit found with the fixture applies only to a plain WinForms tab that was never shown.

Not yet done: the manual check of an iLogic Security Alert (see the test plan).

## The migration dialog

A save of a file that an earlier release saved shows **Data Format Has Changed** ("The data format of the following
files was migrated to the current release. Previous versions of Autodesk Inventor will not be able to open these
files if they are saved. Continue with save?"), with `OK`, `Cancel`, `Help` and a list of the files. It opens even
from a snippet, and it holds the main thread until a person answers. On 2026-09-25 one save held a call for 184 s.

### It is a decision, so the server clicks it only when the user turns that on

`OK` writes the files in the running release's format, and no earlier release can open them after that. That
matters when files go to an older Inventor. Example: the Design Automation engine of the StrobicConfigurator project
runs Inventor 2025.3, and a job fails with `E_INVALIDARG` before any plugin code runs when a part in its workfiles
was saved by 2025.4. So the automatic close of information dialogs does not cover it. It is closed only when the
user sets `INVENTORMCP_ACCEPT_MIGRATION_DIALOG=true`, and only while `INVENTORMCP_AUTOCLOSE_DIALOGS` is on too.
The tool result then says that the files were saved in this release's format.

The server acts only on a dialog that blocks one of its own calls. The same dialog after a person's own Ctrl+S is
never touched.

The catalog entry matches the title `Data Format Has Changed`, the class `#32770`, and the buttons: exactly `OK` and
`Cancel`, with `Help` allowed. A dialog with any other button (ex. a check box that a later release adds) stays open.

### UI Automation finds no button in it

Measured on 2026-09-25 on Inventor 2025.4 (Build 294407000), while a save of a 2024.3 part waited:

| Read | Result |
| --- | --- |
| UI Automation from the dialog (the test script's `Read`) | No text and no button. `Click OK` refused: 0 buttons named `OK`. |
| UI Automation through the server's walk | The file path from the list, but no button |
| `EnumChildWindows` | `Button` id 1 `OK`, `Button` id 2 `Cancel`, `Static` id 10228 with the message, `Button` id 9958 `Help`, `SysListView32` id 10232 (header `Migrating files`), `SysHeader32` |

So when UI Automation finds no button in a `#32770` dialog, the server reads the Win32 child windows: each `Button` is
a button with its control id, and each visible `Static` adds its text. `GetWindowText` reads a control of a different
process without a message, the same as a title.

### The click is WM_COMMAND, not BM_CLICK

| Click | Result |
| --- | --- |
| `BM_CLICK` sent to the `OK` button from a different process | Returned, but the dialog stayed open. The dialog was not in front. |
| `WM_COMMAND` sent to the dialog, `wParam` = 1 (`IDOK`, `BN_CLICKED`), `lParam` = the `OK` button | Closed. Inventor unblocked and the save completed: pid 67 of the part went from 2024.3 (Build 283343000, 343) to 2025.4 (Build 294407000, 407). |

The server sends the button's own control id as `WM_COMMAND`, through `SendMessageTimeout` with the read time limit,
after the same checks as the UI Automation click: the mutex, the dialog still open, the same title, and exactly one
visible, enabled button of that name.

The live tests `MigrationDialogIsClosedWhenAccepted` and `MigrationDialogStaysOpenByDefault` repeat this through the
server on a copy of a part that `INVENTORMCP_LIVE_OLD_RELEASE_PART` names. Both passed on 2026-09-25 on Inventor 2025.4,
together with the six earlier live tests. The fixture has no dialog that hides its buttons from UI Automation, so the
desktop tests call the Win32 read and click directly on a real message box.

## The .NET error dialog

On 2026-09-26 on Inventor 2025.4, an `inventor_set_parameters` call ran an assembly rule that failed. The server
closed the iLogic error dialog with `OK` at 05:59:42.5. At 05:59:44.5 the next check found a second dialog, and the
server log recorded it:

| Field | Value |
| --- | --- |
| Title | `Microsoft .NET` |
| Framework and class | `WinForm`, `WindowsForms10.Window.8.app.0.ffc8c_r3_ad1` |
| Buttons | `Details`, `Continue` |
| Text | "Unhandled exception has occurred in a component in your application. If you click Continue, the application will ignore this error and attempt to continue." then "Cannot access a disposed object. Object name: 'DevExpress.XtraTab.XtraTabPage'." |

This is the Windows Forms `ThreadExceptionDialog`. A control of the iLogic error window (a DevExpress tab page) was
used after the window closed. The dialog comes after the server's own `OK` click, so the automatic close causes it,
and it held the call until a person clicked `Continue`.

### It decides nothing, so the server clicks Continue

`Continue` ignores the exception, and the UI thread continues. The only other choice, `Quit`, ends Inventor and loses
unsaved work. `Quit` was not visible in the example. So the automatic close covers the dialog, with no opt-in, and
`INVENTORMCP_AUTOCLOSE_DIALOGS=false` turns it off with the other types.

The catalog entry matches the title `Microsoft .NET`, a class that starts with `WindowsForms10.`, the text
`Cannot access a disposed object.`, and the buttons: exactly `Continue`, with `Details` allowed. A dialog for a
different exception, or with `Quit` or any other button, stays open, because no example of it was read.

Not yet done: a live test. The fixture cannot show this dialog on demand in Inventor.

## Risks and limits

- **A click is a user decision.** A dialog can ask to save or discard data. Only an information dialog with one
	button is safe to close without asking. The server must never select a button by itself.
- **An old package.** `Interop.UIAutomationClient` has had no release since 2020. It holds only interface
	declarations, so there is no code to fix. If it is removed from nuget.org, generate the same assembly from
	`UIAutomationCore.dll` with `tlbimp`, or change to the WPF framework reference (see "Target framework test").
- **Integrity levels.** Windows UIPI does not let a process send input to a process with a higher integrity
	level. If Inventor runs as administrator and the server does not, reads can work but the click can fail.
- **A dialog from a different process.** A dialog that an external process shows (ex. Vault or a licensing
	service) is not in the Inventor process. The block check still finds the block (the main frame is disabled),
	but the dialog list is empty. Report that case clearly.
- **Custom controls.** Some third-party grids and virtualized lists give little data to UI Automation.
- **The root search miss is not explained.** Do not use `RootElement.FindAll` as the only search.

## Prevention

- **Fix the rule.** `Drawing_Main` line 16 needs `Drawing_External` in an iLogic external rule
	folder. Examine the external rule folders in the iLogic configuration, and the active project, because a
	project change can change a relative path.
- **Silent operation does not prevent it.** The add-in runs each C# snippet inside `SilentOperationScope`
	(`InventorOperations.Execution.cs`), and the snippet that ran the rule was a C# snippet. The iLogic error dialog
	still opened. So `SilentOperation = true` does not suppress the iLogic error dialog when a rule runs through
	`iLogicAutomation.RunRule`. The server cannot depend on it. The live test level confirms this again
	(`Docs/Tasks/Server-Test-Project-Plan.md`).
- **Test copies outside the workspace.** A drawing copied to a scratchpad folder still runs its rules, but a
	relative external rule folder does not resolve from there. Tell the model to examine
	`auto.FileOptions.ExternalRuleDirectories` before it runs a rule on a copy.

## Interim workaround: the watcher

Until the server has the watchdog, the user can run the appendix script in its own PowerShell window:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Inventor-Dialog.ps1 -Action Watch -AutoClose
```

Every 2 s it examines each Inventor process. For each new dialog that blocks Inventor, it writes the text to
`%LOCALAPPDATA%\InventorMcp\blocking-dialogs.log`. With `-AutoClose`, it clicks `OK` on information dialogs only,
with the same policy as the table above. It leaves all other dialogs open. The stuck call then returns, but the
model does not get the dialog text. The model must read the log.

## Appendix: test script

`Inventor-Dialog.ps1` is the script from the test. It has the actions `List`, `Read`, `Click` and `Watch`.
`List`, `Read` and `Watch` without `-AutoClose` were tested on the dialog above. `Click` and `-AutoClose` were not run.

```powershell
<#
.SYNOPSIS
    Finds, reads and closes dialogs that block Autodesk Inventor.

.DESCRIPTION
    Runs outside Inventor, so it works while a modal dialog blocks Inventor's main thread.
    It uses Win32 EnumWindows to find the windows, because a UI Automation search from the
    desktop root can miss an owned WinForms dialog. It then uses UI Automation on each window
    handle to read the text and to click a button.

    List  - Shows each Inventor process, tells if the main window is blocked, and lists the dialogs.
    Read  - Shows the controls of one dialog, with the full text of text boxes and hidden tabs.
    Click - Clicks one visible, enabled button in one dialog. This changes Inventor state.
    Watch - Runs until stopped. Writes the text of each new blocking dialog to the log. With -AutoClose,
            it also clicks OK on information dialogs only (see Test-InformationDialog).

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File Inventor-Dialog.ps1 -Action List
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File Inventor-Dialog.ps1 -Action Read -Hwnd 009B27C0
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File Inventor-Dialog.ps1 -Action Click -Hwnd 009B27C0 -ButtonName OK
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File Inventor-Dialog.ps1 -Action Watch -AutoClose
#>
param(
    [ValidateSet('List', 'Read', 'Click', 'Watch')][string]$Action = 'List',
    [string]$Hwnd,
    [string]$ButtonName,
    [switch]$AutoClose,
    [int]$IntervalSeconds = 2,
    [string]$LogPath = (Join-Path $env:LOCALAPPDATA 'InventorMcp\blocking-dialogs.log')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class InvWin {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    const uint GW_OWNER = 4;

    public class Info { public string Hwnd, Owner, Class, Title; public bool Enabled; }

    public static List<Info> VisibleTopLevel(uint processId) {
        var result = new List<Info>();
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == processId && IsWindowVisible(h)) {
                var t = new StringBuilder(1024); GetWindowText(h, t, t.Capacity);
                var c = new StringBuilder(256); GetClassName(h, c, c.Capacity);
                result.Add(new Info {
                    Hwnd = h.ToInt64().ToString("X8"),
                    Owner = GetWindow(h, GW_OWNER).ToInt64().ToString("X8"),
                    Class = c.ToString(), Title = t.ToString(), Enabled = IsWindowEnabled(h) });
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

function Get-Element([string]$Handle) {
    if (-not $Handle) { throw "Give -Hwnd. Run -Action List to get it." }
    $AE::FromHandle([IntPtr][Convert]::ToInt64($Handle, 16))
}

function Test-Visible($Element) {
    # WinForms controls have their own window handle. UI Automation IsOffscreen reports False
    # for hidden WinForms buttons, so use the Win32 test when a handle exists (WPF controls have none).
    $handle = $Element.Current.NativeWindowHandle
    if ($handle -ne 0) { return [InvWin]::IsWindowVisible([IntPtr]$handle) }
    -not $Element.Current.IsOffscreen
}

function Get-ElementText($Element) {
    # Edit and Document controls keep their text in ValuePattern or TextPattern, not in Name.
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        if ($pattern.Current.Value) { return $pattern.Current.Value }
    }
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$pattern)) {
        $text = $pattern.DocumentRange.GetText(-1)
        if ($text) { return $text }
    }
    $Element.Current.Name
}

function Get-VisibleButtons($Window) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    @($Window.FindAll($Scope::Descendants, $cond) | Where-Object { (Test-Visible $_) -and $_.Current.IsEnabled })
}

function Get-DialogText($Window) {
    $seen = [ordered]@{}
    foreach ($e in $Window.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        $type = $e.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
        if ($type -notin 'Text', 'Edit', 'Document') { continue }
        $text = Get-ElementText $e
        if ($text) { $seen[$text] = $true }
    }
    ($seen.Keys -join "`r`n---`r`n")
}

function Test-InformationDialog($Window, $Info) {
    # Close without a person only when the dialog cannot ask a question: one visible button, named OK,
    # and a known information dialog type. Add a type here only after you read a real example.
    $buttons = Get-VisibleButtons $Window
    if ($buttons.Count -ne 1 -or $buttons[0].Current.Name -ne 'OK') { return $false }
    $iLogicError = $Info.Title -like 'Error on line * in rule: *'
    $win32MessageBox = $Info.Class -eq '#32770'
    $iLogicError -or $win32MessageBox
}

function Write-DialogLog([string]$Text) {
    $folder = Split-Path $LogPath -Parent
    if (-not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder | Out-Null }
    Add-Content -Path $LogPath -Value $Text -Encoding UTF8
}

switch ($Action) {
    'Watch' {
        "Watching Inventor for blocking dialogs every $IntervalSeconds s. AutoClose=$AutoClose. Log: $LogPath"
        "Stop with Ctrl+C."
        $handled = @{}
        while ($true) {
            foreach ($p in @(Get-Process -Name Inventor -ErrorAction SilentlyContinue)) {
                $windows = [InvWin]::VisibleTopLevel([uint32]$p.Id)
                $main = $windows | Where-Object { $_.Class -like 'AfxMDIFrame*' } | Select-Object -First 1
                if (-not $main -or $main.Enabled) { continue }
                foreach ($d in @($windows | Where-Object { $_ -ne $main -and $_.Enabled -and $_.Title })) {
                    $key = "$($p.Id):$($d.Hwnd):$($d.Title)"
                    if ($handled.ContainsKey($key)) { continue }
                    $handled[$key] = $true
                    try {
                        $win = Get-Element $d.Hwnd
                        $buttons = (Get-VisibleButtons $win | ForEach-Object { $_.Current.Name }) -join ', '
                        $closeIt = $AutoClose -and (Test-InformationDialog $win $d)
                        $decision = if ($closeIt) { 'AUTO-CLOSED (OK)' } else { 'LEFT OPEN (needs a person)' }
                        $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
                        # Record the text before the click. After the dialog closes, the text is gone.
                        Write-DialogLog ("=== $stamp  PID $($p.Id)  $decision`r`nTitle: $($d.Title)`r`nClass: $($d.Class)`r`n" +
                            "Visible buttons: $buttons`r`n$(Get-DialogText $win)`r`n")
                        if ($closeIt) {
                            (Get-VisibleButtons $win)[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                        }
                        "$stamp  $decision  '$($d.Title)'"
                    }
                    catch {
                        # The dialog can close between the search and the read.
                        "$(Get-Date -Format 'HH:mm:ss')  Could not handle '$($d.Title)': $($_.Exception.Message)"
                    }
                }
            }
            Start-Sleep -Seconds $IntervalSeconds
        }
    }
    'List' {
        $processes = @(Get-Process -Name Inventor -ErrorAction SilentlyContinue)
        if ($processes.Count -eq 0) { 'NO_INVENTOR: No Inventor.exe process.'; break }
        foreach ($p in $processes) {
            $windows = [InvWin]::VisibleTopLevel([uint32]$p.Id)
            $main = $windows | Where-Object { $_.Class -like 'AfxMDIFrame*' } | Select-Object -First 1
            # A modal dialog disables its owner. A disabled main frame means Inventor is blocked.
            $state = if (-not $main) { 'UNKNOWN (no main frame found)' } elseif ($main.Enabled) { 'NOT_BLOCKED' } else { 'BLOCKED' }
            "PID $($p.Id)  state=$state  main='$($main.Title)'"
            $dialogs = @($windows | Where-Object { $_ -ne $main -and $_.Enabled -and $_.Title })
            foreach ($d in $dialogs) { "  DIALOG hwnd=$($d.Hwnd) owner=$($d.Owner) class=$($d.Class) title='$($d.Title)'" }
            if ($state -eq 'BLOCKED' -and $dialogs.Count -eq 0) {
                '  No titled dialog found. Other enabled windows:'
                $windows | Where-Object { $_ -ne $main -and $_.Enabled } |
                    ForEach-Object { "  WINDOW hwnd=$($_.Hwnd) owner=$($_.Owner) class=$($_.Class)" }
            }
        }
    }
    'Read' {
        $win = Get-Element $Hwnd
        "WINDOW '$($win.Current.Name)'  framework=$($win.Current.FrameworkId)"
        $seen = @{}
        foreach ($e in $win.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            $type = $e.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
            if ($type -notin 'Text', 'Edit', 'Document', 'Button', 'CheckBox', 'RadioButton', 'ComboBox', 'Hyperlink', 'TabItem') { continue }
            $text = Get-ElementText $e
            # Some controls repeat the text of their parent. Show each text only one time.
            if ($type -in 'Text', 'Edit', 'Document') {
                if (-not $text -or $seen.ContainsKey($text)) { continue }
                $seen[$text] = $true
            }
            $flags = @()
            if (-not (Test-Visible $e)) { $flags += 'hidden' }
            if (-not $e.Current.IsEnabled) { $flags += 'disabled' }
            $flagText = if ($flags) { " ($($flags -join ', '))" } else { '' }
            "--- [$type]$flagText '$($e.Current.Name)'"
            if ($type -in 'Text', 'Edit', 'Document') { $text }
        }
    }
    'Click' {
        if (-not $ButtonName) { throw 'Give -ButtonName.' }
        $win = Get-Element $Hwnd
        $cond = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)),
            (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $ButtonName)))
        # Some dialogs keep hidden template buttons (ex. Apply, Extra). Click only one visible, enabled match.
        $buttons = @($win.FindAll($Scope::Descendants, $cond) | Where-Object { (Test-Visible $_) -and $_.Current.IsEnabled })
        if ($buttons.Count -ne 1) { "REFUSED: $($buttons.Count) visible, enabled buttons named '$ButtonName'."; exit 1 }
        $title = $win.Current.Name
        $buttons[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        "CLICKED '$ButtonName' in '$title'. Run -Action List to confirm the state."
    }
}
```
