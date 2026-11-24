---
name: ilogic-tools
description: How this server's tools change parameters and run iLogic rules - suppressRules and runRuleAfter for a batch, the RulesEnabled restore, the rule tools, and the iLogic dialogs that the server answers or leaves for the user. Read it before you change parameters of a document that has rules, or before you read, change or run a rule.
---

# iLogic tools

The iLogic facts (what triggers a rule, the cost of a run, the error dialogs, the Security Alert) are in the
`inventor-ilogic` skill of the `inventor-ilogic` plugin (marketplace `tylerwarner-autodesk`). If your client has that
plugin, read that skill too. This skill gives the tools of this server. To change the text of a rule, follow the
`ilogic-rule-edit` skill.

## Change parameters with the rules off

A rule runs again when a parameter that it uses changes, and one run can take more than a minute.

- To change several parameters, use `inventor_set_parameters` with `suppressRules: true`, and `runRuleAfter` with the
  top level rule. The rules then run once.
- `inventor_set_parameter` and `inventor_eval_csharp` also take `suppressRules`.
- With `suppressRules`, the result lists in `rulesNotRun` the active rules that did not run on the new values. Do not
  report the work as done until you ran the rule that the workflow needs, or told the user that the rules did not run.
- `RulesEnabled` is a setting of the whole Inventor session. The tools restore it after the call. If a dialog stops
  the restore, the result says that the rules are still off, and the server turns them on before its next call to
  Inventor: tell the user. `inventor_session` gives `iLogicRulesEnabled`.
- iLogic `Parameter("Name")` uses the document's units, not centimeters. Write an expression with its unit.
- The read tools of this server open files with the rules off. An on-open rule can still run when another tool opens a
  document: check `inventor_documents` before a retry.

## Read, change and run rules

- `inventor_ilogic_rules` lists the rules. `inventor_ilogic_rule_get` reads one rule, or writes all of them to files.
- `inventor_ilogic_rule_set` changes a rule at an anchor that must occur exactly once. It writes a backup first and
  returns a diff.
- `inventor_run_ilogic` with `ruleName` runs a rule of the document. With `code`, it runs a temporary rule body in
  VB.NET and removes it.
- In a snippet, the iLogic helpers of the `snippets` skill do the casts.

## The dialogs that a rule shows

- The server closes an iLogic error dialog (it has only OK) and gives its text in `blockingDialogs`. The text often
  says which line of which rule failed. A "Rule Compile Errors" dialog is closed the same way. After an iLogic error,
  the server clicks `Continue` on a "Microsoft .NET" error ("Cannot access a disposed object").
- iLogic shows the same error for the same rule only one time in 120 minutes. So no `blockingDialogs` entry on a run
  again does not mean that the rule ran. Read the model state after the run.
- The server answers the iLogic Security Alert and the Security Advisor as the user set in its dialog settings
  (`DialogSettings.json`). By default it leaves both open for the user. A user can set it to click **Run the rule**,
  then `OK` in the Advisor, but only when the option for the one rule is selected. The result lists each click in
  `blockingDialogs`: tell the user that the rule is now trusted.
- When the call returns `blocked-by-dialog`, never answer the dialog for the user. Read it with `inventor_dialogs`,
  tell the user, and click with `inventor_dialog_click` only the button that the user chose.
