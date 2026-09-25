---
name: ilogic-rule-edit
description: The steps to change the text of an iLogic rule safely - read, back up, change at a unique anchor, compare, and run once. Read it when a user asks you to change, fix or add an iLogic rule.
---

# iLogic rule edit

1. **Work on a copy** when the rule is in a master. See the `safe-template-edit` skill.
2. **Read the rules.** `inventor_ilogic_rules` for the names, then `inventor_ilogic_rule_get` with `ruleName` for
   the text. For a large change, write all rules to a folder with `outputFolder`, so you have the full text beside
   the change.
3. **Choose an anchor** that occurs exactly once in the rule: a full line is safer than a word. A rule without
   `Sub Main` cannot declare functions; in a rule with `Sub Main`, insert statements inside it.
4. **Change the rule** with `inventor_ilogic_rule_set`: `mode` is `replace`, `insert-before`, `insert-after`, or
   `replace-all`. The tool refuses an anchor that occurs 0 times or more than once, and refuses when the rule changed
   after its read. It writes a backup of the old text and returns a diff.
5. **Read the diff.** Check that it is the change you intended, and nothing else. The backup path is in the result,
   if you must undo.
6. **Run the rule once** with `inventor_run_ilogic` and `ruleName`. If other rules run on parameter changes, change
   the parameters first with `inventor_set_parameters` and `suppressRules`, then run the rule once.
7. **Check the result.** Read `blockingDialogs` in the result: an iLogic error dialog gives the line that failed. Then
   check the model with `inventor_health` and the measurement that the change is for.

If the call returns `blocked-by-dialog`, iLogic can be asking the user to trust the changed rule (a Security Alert).
Do not answer it. Read it with `inventor_dialogs` and tell the user.
