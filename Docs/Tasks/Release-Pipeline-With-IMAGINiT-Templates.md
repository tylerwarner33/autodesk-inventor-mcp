# Release Pipeline With IMAGINiT.Pipelines.Templates

Created: 2026-09-21

Status: **not started.** This is a plan for if and when this tool is distributed to other people.
Nothing here is implemented, and nothing depends on it.

`Pipelines/Build.yml` already exists and is unrelated: it only proves the repository builds on an agent with no
Inventor installed. This document covers signing and installers, which it deliberately leaves alone.

## Why it is not done yet

The reference repositories ship products to customers.
This is a local developer tool, and no decision has been made to distribute it.
An installer nobody has asked for is work that has to be maintained, so it waits for a real need.

## What has to exist first

| Prerequisite | State |
| --- | --- |
| A decision to distribute the tool | Not made |
| `Installer/Product.wxs` | Does not exist |
| `Signed Installers/` folder | Does not exist |
| Access to `IMAGINiT/IMAGINiT.Pipelines.Templates` | Assumed available, same as the reference repositories |
| Azure DevOps project with the signing service connection | Assumed, inherited from the templates |

## The shape, taken from the reference repositories

`SprungStructures.RevitToInventor/Pipelines/Inventor-Release.yml` and
`Hy-Vee/HyVee.SAPFixtureCostUpdater/Pipelines/*.yml` are the closest guides.
`Trinity.TrailerConfigurator` follows the same pattern.

### Trigger

Manual only. A release is a decision, not a consequence of a push.

```yaml
trigger: none
pr: none

pool:
  vmImage: 'windows-latest'
```

### Reference the template repository

```yaml
resources:
  repositories:
    - repository: templates
      type: git
      name: 'IMAGINiT/IMAGINiT.Pipelines.Templates'
      ref: master

steps:
  - checkout: self
    # Required to push the signed installer back as a pull request.
    persistCredentials: true
```

### Read the version from MSBuild rather than duplicating it

```yaml
- template: Steps/get-msbuild-property.yml@templates
  parameters:
    filePath: '$(Build.SourcesDirectory)/$(appFilePath)'
    properties:
      - name: Version
        outputVariable: AppVersion
      - name: AutodeskVersion
```

Both properties already exist in this repository's `Directory.Build.props`, so this step works unchanged.

### Build, sign, package, sign again, publish

One composite template does the whole middle of the job:

```yaml
- template: Steps/Composites/build-project-sign-file-build-wix-v5-installer-sign-and-publish-file.yml@templates
  parameters:
    project: '$(appFilePath)'
    filePattern: '$(appFolderPath)/bin/$(buildConfiguration)/$(appFileName).dll'
    wixSource: '$(appFolderPath)/Installer/Product.wxs'
    wixOutput: '$(appFolderPath)/bin/$(buildConfiguration)/$(msiFileName)'
    buildConfiguration: '$(buildConfiguration)'
    additionalPackageFiles: 'Directory.Packages.props'
    wixVariables: 'BuildOutputDir=...;ProductVersion=$(AppVersion);AutodeskVersion=$(AutodeskVersion)'
```

`additionalPackageFiles` is needed because both repositories use central package management,
as this one does.

`wixExtensions` is added when the installer needs one, ex. Hy-Vee passes `WixToolset.Util.wixext`
for `util:PermissionEx`.

### Commit the signed installer back through a pull request

```yaml
- template: Steps/create-folder.yml@templates
  parameters:
    folder: '$(Build.SourcesDirectory)/Signed Installers/Inventor/$(AutodeskVersion)/Build $(AppVersion)'

- template: Steps/copy-file.yml@templates
  parameters:
    filePattern: '$(msiFileName)'
    sourceFolder: '$(appFolderPath)/bin/$(buildConfiguration)'
    targetFolder: '$(Build.SourcesDirectory)/Signed Installers/Inventor/$(AutodeskVersion)/Build $(AppVersion)'

- template: Steps/Composites/create-branch-commit-and-create-pull-request.yml@templates
  parameters:
    sourceBranch: 'Release/Inventor-$(AutodeskVersion)/Build-$(AppVersion)'
    filePattern: 'Signed Installers/Inventor/$(AutodeskVersion)/Build $(AppVersion)'
    reviewerEmail: '$(pullRequestReviewerEmail)'
```

Hy-Vee also publishes the `.docx` guides from `Docs` as a separate artifact, using `Steps/publish-file.yml`.
The equivalent here would be `Docs/Setup-and-Usage-Guide.md`.

## What is different about this repository

Every reference repository ships **one** plugin assembly and installs it into an Autodesk add-ins folder.
This repository ships **two** pieces that have to arrive together:

| Piece | Where it goes |
| --- | --- |
| `InventorMcp.AddIn` and its dependencies | The Inventor add-ins folder, as the `.bundle` layout |
| `InventorMcp.Server` executable | Anywhere, but its path must be written into the user's MCP client configuration |

So the installer has to do something none of the reference installers do.
Decide before writing `Product.wxs`:

- Does the MSI install both pieces, or only the add-in, leaving the server to be run from a build folder?
- Does it write the Claude Desktop `claude_desktop_config.json` entry, or only print the path for the user?
	Editing another application's configuration file is intrusive and easy to get wrong when several MCP servers exist.
- Per user or per machine? The bundle currently deploys per user, to the version independent
	`%APPDATA%\Autodesk\ApplicationPlugins\InventorMcp.AddIn`, with one manifest per release.

A reasonable first version installs the add-in bundle per user, places the server beside it, and shows the
configuration snippet at the end rather than editing anything.

## Build targets that already exist

`-p:DeployBundle=true` on the add-in project already produces the bundle layout an installer would ship.
Each run adds one release's manifest and `Contents\<version>` folder, so a release build runs it three times, once
per `AutodeskVersion`. There is no `PackageContents.xml`: Inventor discovers the manifests directly.
`Product.wxs` can harvest that output rather than describing files by hand.

This changes the template steps above. `AutodeskVersion` is no longer one value per installer, so the
`Signed Installers/Inventor/$(AutodeskVersion)` folder and the `Release/Inventor-$(AutodeskVersion)` branch name
should drop the release year. One installer covers 2025, 2026 and 2027.

## Open questions

- Is signing actually required for an internal developer tool, or is an unsigned MSI enough?
- Should the server be published self contained, so an end user does not need the .NET 10 runtime?
- Does the repository want `Signed Installers/` committed, as the reference repositories do,
	given this one already commits three 16 MB vendored interop folders, one per release?
