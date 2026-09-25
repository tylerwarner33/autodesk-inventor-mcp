---
name: read-back-after-failed-write
description: What to do after a write to Inventor fails or times out - read the real state before any retry, because a failed call can still have changed the model. Read it when a call that changes a document returns an error, a timeout, or blocked-by-dialog.
---

# Read back after a failed write

A failed call can still change the model: the error can come after part of the work, an on-open rule can change a
document before the error, and a timed-out call keeps running on Inventor's main thread. A retry of the same call
can then do the work twice.

1. **Check for a dialog.** If the result is `blocked-by-dialog`, or has `blockingDialogs`, read the text. An iLogic
   error dialog gives the rule and the line that failed. A question dialog needs the user: see `inventor_dialogs`.
2. **Read the session.** `inventor_session`: is Inventor there, which documents are open, which are changed, and are
   they modifiable? A write to a document that is not modifiable fails with E_FAIL.
3. **Read what the call was to change.** Ex. `inventor_parameters` after a parameter write, `inventor_features` or
   `inventor_health` after a feature write, `inventor_ilogic_rule_get` after a rule edit,
   `inventor_activity` for the transactions that were committed.
4. **Compare with what you expected.** Decide what is done, what is not, and what is wrong.
5. **Retry only the missing part**, with the cause fixed. If the model is wrong, tell the user before you undo:
   the user can undo in Inventor, or you can restore from a backup or a copy.

Do not retry a call that timed out until `inventor_session` answers: the first call can still be running.
