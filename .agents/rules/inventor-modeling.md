---
name: Inventor Modeling
description: How to write geometry against a live Inventor session - named faces, extent direction, and units
triggers:
  - Writing geometry, sketches, or features through inventor_eval_csharp or inventor_run_ilogic
  - A request that names a face (ex. "the top face") or a direction
  - Converting lengths or angles for a script
---

## Where the rules are

The modeling rules apply to any code that drives Inventor, not only to agents on this repository, so they live in the
plugins of the [autodesk-agent-skills](https://github.com/tylerwarner33/autodesk-agent-skills) repository (marketplace
`tylerwarner-autodesk`):

- `inventor-modeling` (plugin `inventor-api`): named faces and the ViewCube, units, extent direction, the feature that
  the request names, stable references, and how to verify a write.
- `inventor-api-traps` (plugin `inventor-api`): API traps that make a snippet fail.

The steps with this server's tools are in the server's own skills (`Source/InventorMcp.Server/Skills/`):
`verify-a-write` (which tool reads the ViewCube before a write and checks the model after it), and `snippets` (the
snippet host, with the unit helpers).

Read the skills before you write geometry. Change a fact in the plugin repository, not in this rule.

Every client also gets a short form of the most important rules in the `initialize` response, from
`Source/InventorMcp.Server/ServerInstructions.md`. Change that text when a rule in a skill changes. Visual Studio
asks the user to trust the server again after each change of it, so change it seldom.
