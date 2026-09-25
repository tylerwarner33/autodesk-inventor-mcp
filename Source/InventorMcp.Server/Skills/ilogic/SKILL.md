---
name: ilogic
description: How iLogic rules behave when a model changes a document through the API - parameter change triggers and their cost, batching with suppressRules, rule text edits, the Security Alert, and dialogs that rules raise. Read it before you change parameters of a document that has rules, or before you read, change or run a rule.
---

# iLogic rules

## A parameter change can run rules

A rule runs again when a parameter it uses changes, and a top level rule often uses many parameters. Measured on a
template with many rules: one parameter write with the rules on took 85 s, and one run of the top level rule 79 s.
The same writes with the rules off took under 0.1 s.

- To change several parameters, use `inventor_set_parameters` with `suppressRules: true`, and `runRuleAfter` with
  the top level rule. The rules then run once.
- `inventor_set_parameter` and `inventor_eval_csharp` also take `suppressRules`.
- `RulesEnabled` is a setting of the whole Inventor session, not of one document. The tools restore it in a
  `finally` block. If a result says that the restore failed, tell the user and restore it when Inventor answers.
  `inventor_session` gives `iLogicRulesEnabled`.
- `RulesOnEventsEnabled` does not stop the runs that a parameter change causes.
- A long rule cannot be stopped: it runs on Inventor's main thread until it returns.

## Opening a document can run rules

An on-open rule can run when a document opens. A call can then report an error while the file opened and became
changed. Check `inventor_documents` before a retry. The read tools of this server open files with the rules off.

## Reading and changing rule text

- `inventor_ilogic_rules` lists the rules. `inventor_ilogic_rule_get` reads one rule, or writes all of them to
  files.
- `inventor_ilogic_rule_set` changes a rule at an anchor that must occur exactly once. It writes a backup first and
  returns a diff. See the `ilogic-rule-edit` skill.
- A rule without `Sub Main` cannot declare functions. In a rule with `Sub Main`, put new statements inside it.
- A rule can name files, parameters and components in its text. After a copy of a model, a rule can still name the
  masters. Read the rules of the copy.

## Running a rule

- `inventor_run_ilogic` with `ruleName` runs a rule of the document. With `code`, it runs a temporary rule body in
  VB.NET and removes it.
- The server closes an iLogic error dialog (it has only OK) and gives its text in `blockingDialogs`. The text often
  says which line of which rule failed.
- A rule can open a question dialog, ex. in a partial test setup. That blocks every call: see below.

## The Security Alert

When you run a rule that iLogic detects to be potentially unsafe, it shows an iLogic Security Alert: "iLogic has
disabled a potentially harmful rule. If you trust the contents of this rule and would like to enable it on your
machine, click Run the rule." It blocks Inventor until someone answers.

- **Don't run the rule** disables the rule: it does not run again until the user enables it in the Disabled Rules
  dialog (Tools > Options > iLogic Configuration > Security).
- **Run the rule** accepts the rule as safe on this machine.

The check is on the contents of the rule, with the security option "Inspect rules for malicious code". Autodesk
does not list the code it flags. An example is code that opens another file or a website. An edit through the API
does not cause the alert: two edits that added plain text opened none. A rule that is new on this machine, or an
edit that adds such code, can. It is not confirmed whether an edit to an accepted rule makes it unknown again.

Never answer it for the user: either answer changes the rule's state. When a call returns `blocked-by-dialog`, read the dialog with `inventor_dialogs`, tell
the user, and click with `inventor_dialog_click` only the button the user chose.

## From C#

The helpers `ILogicAutomation()`, `ILogicRuleNames(document)`, `ILogicRuleText(document, name)`,
`SetILogicRuleText(document, name, text)` and `RunILogicRule(document, name)` do the casts. By hand, the automation
object must be `dynamic` and the document argument must be typed `Inventor.Document`:

```csharp
dynamic automation = ILogicAutomation();
dynamic rules = automation.get_Rules((Document)part);
```
