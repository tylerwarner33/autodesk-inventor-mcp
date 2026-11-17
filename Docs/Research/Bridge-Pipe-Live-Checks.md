# Pipe for Each Process: Live Checks

Run: 2026-11-17, Autodesk Inventor 2025.4 (build 294407000) and Autodesk Inventor 2026, each with the add-in of
protocol version 3, except where a row says otherwise.

The decisions are in `Docs/Architecture.md`. The tests are in `Tests/InventorMcp.Server.Tests/Live/`.

## Results

| Check | How | Result |
| --- | --- | --- |
| Two Inventor 2025 processes, both with the add-in | `LiveInstanceTests.TwoProcessesOfOneReleaseNeedAChoiceAndEachAnswersForItself` | Two pipe names, each with its process id. A call with no process chosen gives `instance-required`, and the message names both processes. Each process answers with its own id |
| A dialog in the other process | `LiveInstanceTests.ADialogInTheOtherProcessDoesNotBlockThisSession` | A question dialog in one process. The chosen process was not blocked, and answered a snippet in less than 5 s |
| The process in the pipe name | `LiveInstanceTests.TheProcessInTheNameIsTheProcessThatAnswers` | The name, the pipe server process and the add-in give the same id, for 2025 and 2026 |
| Inventor 2025 and Inventor 2026 at the same time | `LiveMultiReleaseTests.EachReleaseAnswersWithItsOwnYearAndProcess` | Each release answers with its own year and process |
| A restart of Inventor under an open client | A test that was not kept, because it closes and starts Inventor | The client was connected to one process with no choice. After the restart, the next call used the new process, with no `instance-required` |
| An add-in of protocol version 2 and the new server | A test that was not kept, because it needs the add-in of an earlier version | `bridge-outdated` |

All live tests passed: 8 with one Inventor 2025, and the tests of two processes and two releases with those
processes.

## Facts found on the way

- `QuestionStaysOpenAndHealthReportsItAtOnce` failed two times in the first 2 minutes after Inventor started: the
  message box closed in less than 1 s with no click. Later, it passed in each run, alone and with all the live
  tests. Start the live tests after Inventor is fully loaded.
- `SettingOffClicksNothing` set `INVENTORMCP_AUTOCLOSE_DIALOGS` after it made the client, and the client reads the
  dialog settings when it is made. So the server clicked the dialog. The test now sets the variable first.
- A test that starts Inventor as a child process keeps `dotnet test` open after the test ends, because Inventor holds
  the output handle of the test host.
