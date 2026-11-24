# Dialog settings

A modal dialog stops every tool except `inventor_activity`. The server answers a dialog as the **dialog settings** say,
or leaves it open for you.

**Edit `Source/InventorMcp.Server/DialogSettings.json` to choose the answer to each dialog.** It is the one place
that sets this. Each entry is a dialog type, and its value is the button that the server clicks, `"Wait"` to wait
until a progress dialog closes by itself, or `"Ask"` to leave the dialog for you. The comment above each entry lists the values it accepts. A change needs a build, because the
server embeds the file.

| Entry | Dialog | Default | Other values |
| --- | --- | --- | --- |
| `iLogicError` | "Error on line N in rule: ...", after a rule throws | `"OK"` | `"Ask"` |
| `iLogicCompileError` | "Rule Compile Errors in ...", when a rule does not compile | `"OK"` | `"Ask"` |
| `messageBox` | A Win32 message box with only `OK` | `"OK"` | `"Ask"` |
| `dotNetDisposedObjectError` | "Microsoft .NET", "Cannot access a disposed object.", which can come after an iLogic error | `"Continue"` | `"Ask"` |
| `migration` | "Data Format Has Changed", when a save migrates files to the running release | `"Ask"` | `"OK"` |
| `iLogicSecurityAlert` | "Security Alert", "iLogic has disabled a potentially harmful rule." | `"Ask"` | `"Run the rule"`, `"Don't run the rule"` |
| `iLogicSecurityAdvisor` | "iLogic Security Advisor", which comes after "Run the rule" | `"Ask"` | `"OK"` |
| `vaultLogIn` | The Vault "Log In", which can hold Inventor while it starts | `"Ask"` | None: the server never signs in for you |
| `vaultDownloadProgress` | The Vault "Download progress", ex. after a project switch. It closes by itself | `"Wait"` (up to 10 min) | `"Ask"`. The server never clicks Cancel, which stops the download |

<details>
<summary><b>Before you change a default</b> (what each click does)</summary>

- `migration` `"OK"` saves the files in the running release's format, which earlier releases cannot open. Leave it
	`"Ask"` when files go to an older Inventor (ex. a Design Automation engine).
- `iLogicSecurityAlert` `"Run the rule"` runs a rule that iLogic flagged as potentially harmful, ex. one that reads the
	registry or writes files. It is `"Ask"` by default, because a call can open a file from a source that you do not
	trust. Set `"Run the rule"` in your own file (next section) only if you trust every rule that your calls run.
	`"Don't run the rule"` disables the rule until you enable it in Tools > Options > iLogic Configuration.
- `iLogicSecurityAdvisor` `"OK"` trusts that one rule on this machine from now on. The server clicks it only when
	"Assume that this external rule is safe" is selected, never when the option for all rules in the folder is.
	You can remove a trusted rule in Tools > Options > iLogic Configuration > Security.

</details>

<details>
<summary><b>Your own settings, with no build</b></summary>

Put a file of the same name, `DialogSettings.json`, in `%LOCALAPPDATA%\InventorMcp\`. Each entry in it replaces the
entry of the same name in the repository file, ex. `{ "iLogicSecurityAlert": "Run the rule", "iLogicSecurityAdvisor": "OK" }`.
The server reads it one time, when it starts, so restart the server in the client after a change. An agent that runs
as you can write the file too, and the restart keeps you in the loop before a looser setting has an effect. An entry
that is not in your file keeps the repository default, so a new dialog type or a changed default needs no change to
your file. Use it when you run the server from a team feed, or to keep a setting that differs from the default while
you work on the repository.
The file is outside the repository, so git never commits it. A file that the server cannot read makes every dialog `"Ask"`, and
`inventor_dialogs` lists the problems in `dialogSettingsProblems`.

Two environment variables on the server still work, and apply after both files:
`INVENTORMCP_AUTOCLOSE_DIALOGS=false` makes every dialog `"Ask"`, and `INVENTORMCP_ACCEPT_MIGRATION_DIALOG=true` makes
`migration` `"OK"`.

</details>

<details>
<summary><b>How the server watches for a dialog, and answers it</b></summary>

A modal dialog owns Inventor's message pump, and the bridge posts its work to that pump.
So an unanswered dialog stalls every tool except `inventor_activity`, which reads plain managed memory.

Write operations therefore run with `SilentOperation` set, and restore the previous value afterwards.
Inventor answers a suppressed dialog with its own default, which is a real behavior change, so reads never suppress anything.

`SilentOperation` does not stop every dialog (ex. the iLogic error dialog). So the server watches from outside
Inventor's process while a call waits. After 3 s, and then every 2 s, it checks whether the Inventor main window is
disabled. If it is, the server reads each dialog with UI Automation (text, buttons, radio buttons and check boxes),
writes it to the server log, and answers it as the dialog settings say:

- A dialog type with a button in the settings gets that button. The call then continues, and the result lists the
	dialog and the click in `blockingDialogs`.
- A progress dialog set to `"Wait"` (the Vault "Download progress") gets no click. The call waits until the dialog
	closes by itself, for up to 10 minutes, and the result lists the dialog in `blockingDialogs`. A request that is not
	sent yet waits too. After 10 minutes, the dialog needs a person.
- A dialog type set to `"Ask"`, a dialog that is not in the settings, or a dialog with a button that the server has
	not seen before stays open. The call stops with `blocked-by-dialog`, the dialog text and its buttons.
	`inventor_dialogs` reads it, and `inventor_dialog_click` clicks the button that the user chose.

The server answers only a dialog that blocks one of its own calls, never one that comes after your own action.

</details>

<details>
<summary><b>How a dialog matches an entry</b></summary>

A dialog gets the button of an entry only when all of these match the example that
was read. Otherwise it stays open, whatever the settings say:

- The title (ex. `Security Alert`, or the pattern `Error on line N in rule: ...`), and the window class: `#32770` for a
	Win32 dialog, or a class that starts with `WindowsForms10.` for a WinForms dialog.
- The text, for a type whose title is not unique: "iLogic has disabled a potentially harmful rule." for the Security
	Alert, and "Cannot access a disposed object." for the .NET error.
- The buttons. The iLogic error, the compile error and the message box have only `OK`. For the other types, each
	required button is present, and every other visible button is on the allowed list (ex. `Help` on
	the migration dialog, `Details` on the .NET error, `Show details` on the Security Alert). A dialog with a button
	that a later release adds (ex. `Quit`) stays open. The Vault "Download progress" has only `Cancel`, which the
	server never clicks: the buttons decide only whether the server waits.
- For the Security Advisor, the selected option is "Assume that this external rule is safe". A dialog with no option
	read stays open.

To add a dialog type, read a real example first, then add it to `Services/DialogPolicy.cs` and to the settings file.
`Docs/Architecture.md`, "How the server finds, reads and clicks a dialog", has the method, and
`Docs/Research/Blocking-Dialog-Detection.md` has the examples that were read.

</details>
