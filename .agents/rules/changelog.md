---
name: Changelog
description: How each pull request adds a new version to CHANGELOG.md and Directory.Build.props, and the format
triggers:
  - Before you create or update a pull request
  - Editing CHANGELOG.md, or the version in Directory.Build.props
globs:
  - "CHANGELOG.md"
  - "Directory.Build.props"
---

## Why each pull request adds a version

Each pull request is one new version of the server. The version is in 2 places, and they must be the same:

- The newest section of `CHANGELOG.md`, where users see what changed.
- `<Version>` in `Directory.Build.props`. It is the version of every package and assembly, and the README version badge reads it from `master`.

A change that users do not see (ex. a docs fix) is still a patch version with a short entry.
Many pull requests can merge on the same day. Each one has its own version.

## Format

```markdown
## [0.2.0] - 2026-10-28

### Added
- A new tool, option, or document. ([PR #12](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/12))

### Fixed
- A defect that is corrected. ([PR #12](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/12))

## [0.1.1] - 2026-10-28
```

| Rule | Detail |
|---|---|
| Section heading | `## [x.y.z] - yyyy-MM-dd`, one section for each pull request. The newest version is first. A date is not newer than the date above it, and not in the future. |
| Subsections | Only these, in this order: `Breaking`, `Added`, `Changed`, `Fixed`, `Removed`, `Security`. Leave out a subsection that has no entry. Do not write "None." |
| Entry | One `- ` line for each change. Write what the user sees, not how the code changed. A second line of detail is an indented sub-item. |
| PR link | End each entry with `([PR #<id>](<URL>))`. The PR number is known only after the PR exists: add the link in a second commit, or leave it out. |
| Writing style | ASD-STE100, as in the rest of the repository. Use the tool names (ex. `inventor_session`). |
| Private data | The rule "No Private Data in the Repository" in `AGENTS.md` applies. Write no client, machine or user name in an entry. |

## Which version

The subsections of your section decide the version. Start from the newest version on `master`:

| Your section has | Bump | Example from 0.1.1 |
|---|---|---|
| A `Breaking` entry | Major | 1.0.0 |
| An `Added` entry, and no `Breaking` | Minor | 0.2.0 |
| Only `Changed`, `Fixed`, `Removed`, or `Security` | Patch | 0.1.2 |

| Subsection | Use for |
|---|---|
| `Breaking` | A user must do something (ex. use a new tool name, or redeploy the add-in). Say what to do. |
| `Added` | A new tool, option, or document. |
| `Changed` | A change of behavior in something that exists. |
| `Fixed` | A defect that is corrected. |
| `Removed` | Something that no longer exists. |
| `Security` | A change that closes a risk. |

## Steps

1. Find the newest version on `master`: the first `## [` heading in `CHANGELOG.md`.
2. Write your entries in a new section on top of it. Choose the version from the table above, and use today's date.
3. Set the same version in `<Version>` in `Directory.Build.props`.
4. After you create the pull request, add the PR link to each entry (optional).

When another pull request merges first, merge the latest `master` into your branch, put your section on top of the other one, and compute your version again.

The `Changelog` workflow checks these rules on each pull request (see `Pipelines/Scripts/Test-Changelog.ps1`).
To run the check locally, in PowerShell 7, after `git fetch`:

```
./Pipelines/Scripts/Test-Changelog.ps1 -SourcesDirectory . -BaseRef origin/master
```

## Dependabot pull requests

Each Dependabot pull request also adds a version, because it changes the packages that the server and the add-in ship.
The `Dependabot changelog` workflow (`.github/workflows/dependabot-changelog.yml`) does it, so you do not edit the
branch:

1. It reads the updated packages with `dependabot/fetch-metadata`.
2. `Pipelines/Scripts/Add-DependabotChangelog.ps1` adds the next patch version on top of the newest version on
	`master`, with one entry for each package. An update that fixes a security alert goes in `Security`, each other
	update in `Changed`. The script then sets the same version in `Directory.Build.props`.
3. It commits the 2 files to the Dependabot branch through the GraphQL API, with a token of a GitHub App. GitHub signs
	the commit, and the commit starts the required checks again. A push with `GITHUB_TOKEN` would start no checks.

The `Changelog` check fails on the first commit of Dependabot, and passes on the commit of the App some seconds later.

| Setting | Value |
|---|---|
| GitHub App permissions | `Contents: Read and write` and `Dependabot alerts: Read-only`. No `Workflows` permission. No webhook. |
| Installation | Only this repository. |
| Dependabot secrets | `CHANGELOG_BOT_CLIENT_ID` (the Client ID of the App) and `CHANGELOG_BOT_PRIVATE_KEY` (the full `.pem` text). A workflow that Dependabot starts cannot read Actions secrets. |

When another pull request merges first, the version of the Dependabot pull request is no longer the next one, and
Dependabot does not rebase a branch that has a commit from another account. Comment `@dependabot recreate` on the pull
request. Dependabot then makes the branch again, and the workflow adds a new section.

## One changelog

`CHANGELOG.md` at the repository root is the only changelog. Do not add a changelog to a project folder.
