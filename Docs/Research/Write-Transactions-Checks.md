# Write Transactions: Live Checks

Run: 2026-11-16, Autodesk Inventor 2025.4 (build 294407000), a project in Vault mode. Each check used a new part that
was never saved, and the checks of a document that is not active used two more new parts.

The result of each check decided the code of the parameter write tools. The decisions are in `Docs/Architecture.md`,
"Parameter writes are one Undo step". The test of the same behavior is
`Tests/InventorMcp.Server.Tests/Live/LiveTransactionTests.cs`.

## Method

A snippet through `inventor_eval_csharp` read `TransactionManager.CommittedTransactions.Count` and the
`DisplayName` of the last transaction before and after each step, and called `UndoTransaction`. The values of the
parameters were read after each step.

## Results

| Check | Result | Then |
| --- | --- | --- |
| A write, `Update` and `End` in one transaction | One committed transaction with the given name. One `UndoTransaction` reverts the write | Group the writes of a call in one transaction |
| A rule that runs inside the transaction (`RunRule` after the write) | The rule runs. The step count is the same. One Undo reverts the parameter and the value that the rule wrote | Run `runRuleAfter` inside the step |
| `Update` inside the transaction, and after `End` | Inside: one step. After `End`: a second step | Keep `Update` inside |
| A nested transaction, both ended | One step, named after the outer transaction. One Undo reverts both writes. `CurrentTransaction` is the inner one while it is open | No check of `CurrentTransaction` before the start |
| `Abort` of the outer transaction after the inner ended | Both writes are reverted, and no step is recorded | The abort of a failed call is correct |
| `Abort` of the outer transaction while the inner is open | No error. `End` of the inner transaction afterwards throws | The helper catches a failure of `Abort`, logs it, and throws the first exception again |
| A document that is not active, with a window | One step with the given name, and no step on the active document | Start the step on the document that is written |
| A document with no window (`Documents.Add` with `visible` false) | The same: one step with the given name | The same |
| A document that is created and closed while the transaction is open (what a rule that opens other files does) | No error. The step ends and is named. One Undo reverts the write | No change |

## Checks run later by the live tests

Run: 2026-11-17, Autodesk Inventor 2025 (the same build). The checks above did not do these two cases, so two live
tests in `LiveTransactionTests.cs` do them. Both passed, with all 15 live tests of that build.

| Check | Test | Result | Then |
| --- | --- | --- | --- |
| A rule that iLogic runs by itself after a parameter change (the rule names the parameter as a variable: `Size2 = Size1 * 2`) | `ARuleThatAParameterChangeStartsJoinsTheStep` | The rule ran inside the step. The step count went up by one. One Undo reverted the parameter and the value that the rule wrote | No change |
| A rule that saves the document (`ThisDoc.Document.Save()`), run through `runRuleAfter`, on a part saved to the temporary folder | `ARuleThatSavesInsideTheStepKeepsTheUndoStep` | No dialog and no rule error. The last step is still `MCP: Set Parameters`. One Undo reverted the parameter | Keep `runRuleAfter` inside the step |

## Not checked

- **A save inside a transaction in a Vault project.** The save test used a file in the temporary folder. A save of a
  file in a Vault project can show a dialog. The tools that save (`inventor_ilogic_rule_set` with a save, and
  `inventor_close_documents`) do not use a transaction.
- **A rule with an event trigger** ("After parameter change" in the Event Triggers dialog). The test rule ran
  because it names the parameter as a variable, which is a different start.

## Facts found on the way

- A user parameter named `A` or `T` fails with `Exception has been thrown by the target of an invocation`
  (`E_FAIL`), because the name is a unit (ampere, tesla). A name such as `WidthA` works. The `interop` skill says so.
- `CurrentTransaction` is never null. With no transaction open, its name is `Unidentified transaction`.
- Adding an iLogic rule with `AddRule` runs the rule once, and its own steps (`Add iLogic Rule`,
  `iLogic Rule Parameters`) are in the list.
- A parameter that a rule writes as a number can show in inches (ex. `0.551181102362205 in`), because the rule gives
  no units. Compare the internal value.
