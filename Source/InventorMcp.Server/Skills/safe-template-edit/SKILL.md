---
name: safe-template-edit
description: The steps to change a master template, an automation assembly or its rules, with every test on copies and the masters changed only at the end. Read it when a user asks you to change or fix a template, a master model or an automation library.
---

# Safe template edit

A template edit that goes wrong can break every model made from it, so test on copies first. Do each step, in order.

1. **Read the project.** Call `inventor_session`. Note the active `.ipj`, the library paths, and whether the masters
   are modifiable. A master in a library path or checked in to Vault is read-only: the user must make it editable.
2. **Copy.** Call `inventor_test_copy` with the top files (the assembly and its drawings) and a new target folder.
   Check that `problems` is empty. Give a `prefix` if the copies are in a folder that the project searches before the
   masters, so references cannot resolve to the masters.
3. **Check the copy.** Rules can name files in their text: read them with `inventor_ilogic_rule_get` and look for
   names of the masters.
4. **Take a baseline.** Measure what the change is for, on the copy, before you change anything: ex.
   `inventor_health`, `inventor_features`, `inventor_hole_check`, `inventor_drawing_layout`, or the parameters.
5. **Change one thing per call.** Use `inventor_set_parameters` with `suppressRules` and `runRuleAfter` for
   parameters, and `inventor_ilogic_rule_set` for rules (see the `ilogic-rule-edit` skill). To try a change and
   discard it, run it in a transaction and abort it:
   `Transaction t = Application.TransactionManager.StartTransaction((_Document)document, "Try"); ... t.Abort();`
6. **Validate at several inputs.** Run the model at the smallest, a typical and the largest input value, and measure
   again each time. Compare with the baseline.
7. **Show the user.** Report what changed and the measurements. Ask before you change the masters.
8. **Change the masters.** Make the same change on the masters, then open them visibly for the user to review.
   Do not save a master that an older Inventor release must read without asking (see the `projects-and-files`
   skill). Never check files in or out of Vault: the user does that.
9. **Clean up.** Close the copies with `inventor_close_documents`. Tell the user where the copies are. Delete them
   only when the user agrees, and never check them into Vault.
