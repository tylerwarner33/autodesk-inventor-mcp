---
name: Inventor Modeling
description: How to write geometry against a live Inventor session - named faces, extent direction, and units
triggers:
  - Writing geometry, sketches, or features through inventor_eval_csharp or inventor_run_ilogic
  - A request that names a face (ex. "the top face") or a direction
  - Converting lengths or angles for a script
---

## The rules are in the server's skills

The modeling rules apply to every model that uses the server, not only to agents on this repository, so they live
in the skills that the server serves through `inventor_skill`:

- `Source/InventorMcp.Server/Skills/modeling/SKILL.md`: named faces and the ViewCube, units, extent direction, the
  feature that the request names, stable references, and how to verify a write.
- `Source/InventorMcp.Server/Skills/interop/SKILL.md`: API traps that make a snippet fail.

Read the skill file before you write geometry. Change the skill, not this rule, when a fact changes.

Every client also gets a short form of the most important rules in the `initialize` response, from
`Source/InventorMcp.Server/ServerInstructions.md`. Change that text when a rule in the skill changes. Visual Studio
asks the user to trust the server again after each change of it, so change it seldom.
