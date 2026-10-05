# Skills Over MCP Plan

Created: 2026-10-05

Status: **open.** The skills are written and served through `inventor_skill`. Two items are left from
`Usage-Findings-Implementation-Plan.md`, which was deleted on 2026-10-05: record the delivery decisions in
`Docs/Architecture.md`, and serve the skills through the Skills over MCP extension when the C# SDK releases it.
The evidence is in `Docs/Research/Usage-Findings-And-Knowledge-Delivery.md`, sections R2 to R4.

## Decisions

These come from the deleted plan. Phase 1 records them in `Docs/Architecture.md`.

1. **Put a trap for one tool in the description of that tool.** Claude Code loads an MCP tool schema only when the
	tool is needed, so a tool description uses context only when the tool is used. `ServerInstructions.md` uses context
	in every session. So the `Parameter.Value` cast and "calls run one at a time" are in tool descriptions or in the
	`interop` skill, not in the instructions. The descriptions of `inventor_eval_csharp`, `inventor_ilogic_rule_set`,
	`inventor_test_copy`, `inventor_run_plugin` and `inventor_drawing_layout` name their skill.
2. **Serve the skills from the server.** They are in `Source/InventorMcp.Server/Skills/<name>/SKILL.md`, embedded with
	a `LogicalName` that keeps the path, and `inventor_skill` serves them until the C# SDK and the clients support the
	extension. `McpServerSkill.Create` computes the manifest from bytes, so the embedded files also work with the
	extension later.
3. **The skills own the Inventor API facts.** `.agents/rules/inventor-interop.md` and `inventor-modeling.md` keep only
	the facts about this repository (ex. the add-in load context) and point to the skills for the rest. So there are
	no two copies that drift apart.
4. **Tell the model when to read a skill.** `ServerInstructions.md` has one line that names the skills and tells a
	model to read `interop` before its first snippet. A list of the skills alone did not make a client read one (see
	`.agents/rules/verification-status.md`, the skills check of 2026-09-25).

## Phases

### Phase 1 - Record the decisions

- [ ] Add a section to `Docs/Architecture.md` for decision 1: tool descriptions against server instructions, and the
	context cost of each.
- [ ] Add a section to `Docs/Architecture.md` for decisions 2 to 4: how the skills are delivered, and why the
	`interop` skill is named in the instructions.
- [ ] Point `Source/InventorMcp.Server/McpTools/InventorTool.Skills.cs` at the new section, not at this document.

### Phase 2 - Skills over MCP

Start when `ModelContextProtocol.Extensions.Skills` (csharp-sdk#1864, or the pull request that replaces it) has a
release.

- [ ] Serve the same folder with the extension, through `McpServerSkill.Create` over the embedded files, or
	`WithSkillsFromDirectory`.
- [ ] Keep `inventor_skill` until the clients that the team uses are on the client matrix. Then remove it, and
	remove its line from `ServerInstructions.md`.

## When the plan is done

- Record the client check of Phase 2 in `.agents/rules/verification-status.md`.
- Delete this document.
