---
name: repo-review
description: Review a change in this repository against its own rules - private data, evidence in code comments, a thin add-in, async and naming, analyzer suppressions, Inventor session safety, and docs that follow the code. Use when the user asks to "review my changes", "review this branch", "review before the PR", or "check this against the repo rules". Do NOT use for correctness bugs - that is /code-review. Do NOT use while you write the code - use it in a new session.
---

# Repository Review

The build already enforces the analyzers and the style rules (`Directory.Build.props`, `.editorconfig`).
This review checks only what a build cannot check.

Run it in a **new session**, not in the session that wrote the code.
The session that wrote the code still has the reason for each choice in its context, so it defends the code
instead of reading it.

## Step 1. Find the scope

```
git fetch origin
git diff --stat origin/master...HEAD
git diff --name-only --diff-filter=AMR origin/master...HEAD
git status --short
```

The scope is the committed diff plus any changes that are not committed.
Report only the lines that the change adds or changes. Put a hit on an old line in "Not in scope".

## Step 2. Build

```
dotnet build InventorMcp.slnx
```

The build must succeed with no warning. If it fails, stop and report the build output. Do not review a broken build.

## Step 3. Run the detection

Run each command on the changed files (`<files>`). A hit is a candidate, not a finding: read the line before you
report it. `references/rules.md` gives the reason and an example for each rule.

```
# R1 Private data. The commit author and noreply@ addresses are allowed.
rg -n -i "[a-z]:[\\/]+users[\\/]+[a-z0-9]|/c/users/[a-z0-9]|/home/[a-z0-9]" <files>
rg -n "[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}" <files>
rg -n -i "(api[_-]?key|token|password|secret|connectionstring)\s*[:=]\s*\S" <files>

# R2 Evidence in comments
rg -n "(//|#|<!--).*\b20[0-9]{2}-[0-9]{2}-[0-9]{2}\b" <files>
rg -n "Docs/Tasks" Source Tests .editorconfig Directory.Build.props Directory.Packages.props .agents

# R3 Thin add-in
git diff --stat origin/master...HEAD -- Source/InventorMcp.AddIn Source/InventorMcp.Contracts

# R4 Async
rg -n "\basync\s+void\b" <files>
rg -n --pcre2 "\basync\s+[\w<>\[\]?,]+\s+(?!Main\b)\w+(?<!Async)\s*[(<]" <files in Source>

# R5 Names and flags
rg -n "\b(class|record|struct|interface)\s+\w*(Manager|Helper|Helpers|Utils|Utility|Utilities)\b" <files>
rg -n "^\s*#region" <files>
rg -n "\b(public|internal|private|protected)\b[^=;]*\(.*\bbool\s+\w+\s*(=\s*(true|false))?\s*[,)]" <files>

# R6 Suppressions
rg -n "#pragma warning disable|SuppressMessage|\.severity\s*=\s*(none|silent|suggestion)" <files>

# R7 Inventor session safety
rg -n "CloseAll|\.Save\(|SaveAs\(|2\.54|25\.4|Math\.PI\s*/\s*180" <files>

# R8 Docs follow the code
git diff --name-only origin/master...HEAD -- Source/InventorMcp.Server/McpTools Source/InventorMcp.Server/ServerInstructions.md Source/InventorMcp.Server/Skills README.md Docs

# R9 Tests
git diff --name-only origin/master...HEAD -- Tests
```

## Step 4. Judge

Read the diff once for each rule, in the order of the table. Do not start with the change that catches your eye.
For each rule, decide one of:

- **Pass** - the change obeys the rule.
- **Finding** - give the file, the line, what is wrong, and the smallest fix.
- **Not applicable** - the change touches nothing that the rule covers. Say why in one line.

Some rules have no detection command, or need more than one:

- R3: a new bridge operation needs a reason that a server-side snippet through `inventor_eval_csharp` cannot do the job.
- R4: a method with `[McpServerTool]` has the name of its tool, and a test method has the name of its behavior.
	Neither needs the `Async` suffix.
- R5: a boolean parameter is a finding only when a call site reads as `Do(x, true)`. A named argument is enough.
- R8: a new or changed tool needs its text in `ServerInstructions.md` or a skill, and `README.md` if a user sees it.
	A new decision needs a section in `Docs/Architecture.md`.
- R9: new behavior in the server needs a `Unit` test. A dialog change needs a `Desktop` test if the fixture can show it.

## Step 5. Write the report

Give every rule a row, also a rule that passed. A missing row means a skipped rule.

```
## Repository review: <branch>, <n> files

| Rule | Result | Evidence |
| --- | --- | --- |
| R1 Private data | Pass | 3 commands, no hit |
| R2 Evidence in comments | Finding | Source/.../BridgeClient.cs:301 - a date in a comment. Move it to Docs/Architecture.md |
| ... | ... | ... |

### Findings
1. <rule> - <file:line> - <what is wrong> - <the fix>

### Not in scope
- <hits on lines that the change did not touch>
```

Do not change the code during the review. Give the report, and let the user decide what to fix.
