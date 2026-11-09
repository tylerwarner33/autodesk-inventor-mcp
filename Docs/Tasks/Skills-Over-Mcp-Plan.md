# Skills Over MCP Plan

Created: 2026-10-05
Updated: 2026-11-09

Status: **the extension is served (version 0.3.0). Two items stay open:** a check by hand in the clients, and the
removal of `inventor_skill` when the clients support the extension.

The decisions and the design are in `Docs/Architecture.md`, "Longer knowledge travels as skills, and a tool
description names its skill". The SDK facts and the check over stdio are in
`Docs/Research/Usage-Findings-And-Knowledge-Delivery.md`, "Why it cannot be the only path yet".

## What is done

- `ModelContextProtocol` 2.4.0 and `ModelContextProtocol.Extensions.Skills` 2.4.0.
- `SkillCatalog` keeps the bytes of each embedded file. `CreatePackages()` builds one `McpServerSkillPackage` per
	skill at `skill://inventor/<name>/SKILL.md`. `WithInventorSkills(catalog)` registers them and counts each read of a
	`SKILL.md` in `inventor.skill.reads`.
- `MCPEXP005` is suppressed in `.editorconfig` for `Services/SkillCatalog.cs` and its test file only.
- `SkillExtensionTests` reads every skill through the SDK client, which verifies each digest and size, and compares
	the text with `inventor_skill`.
- A check over stdio of the built server: the extension and `resources` are declared, `skills/list` gives 11 skills,
	`resources/read` gives `text/markdown`, and `inventor_skill` still works. The process answers `initialize` in about
	0.4 s. That time was not compared with a build without the packages.

## Open items

### Check in the clients

- [ ] Run MCP Inspector against the server, with its skill verification
	(https://github.com/modelcontextprotocol/inspector/blob/main/clients/cli/README.md#skill-verification---verify).
- [ ] Connect Claude Code and Claude Desktop. Check that the server starts, that `inventor_skill` works, and record if
	the client shows the skills through the extension. The client matrix can lag the products.
- [ ] Record the results in `Docs/Research/Usage-Findings-And-Knowledge-Delivery.md`, "Checks of the skill
	delivery".

### Remove the fallback

Start when the clients that the team uses (Claude Code, Claude Desktop, Visual Studio, Visual Studio Code with
Copilot) support the Skills extension on the
[client matrix](https://modelcontextprotocol.io/extensions/client-matrix), or in a check by hand.

- [ ] Remove `inventor_skill` (`McpTools/InventorTool.Skills.cs`) and the tests that call it.
- [ ] Replace its line in `ServerInstructions.md` with a line that tells a model to read the `interop` skill before the
	first snippet. The instruction is still necessary, because a list of skills alone did not make a client read one.
- [ ] Change the tool descriptions that name `inventor_skill`, so they name the skill only.
- [ ] Remove the text API of `SkillCatalog` (`Read`) if nothing else uses it.
- [ ] Check the SDK for a stable (not experimental) Skills API, and remove the `MCPEXP005` sections from
	`.editorconfig` when it exists.

## When the plan is done

- Record what the client checks change in the delivery decisions in `Docs/Architecture.md`.
- Delete this document.
