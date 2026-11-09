<#
.SYNOPSIS
	Adds the CHANGELOG.md section and the version in Directory.Build.props for a
	Dependabot pull request.

.DESCRIPTION
	Used by .github/workflows/dependabot-changelog.yml. The format is in
	.agents/rules/changelog.md.

	A Dependabot update adds only Changed and Security entries, so the new version
	is always the next patch version after the newest version of BaseRef. An
	update that fixes a security alert goes in Security, each other update in
	Changed.

	If the newest section in CHANGELOG.md is already newer than BaseRef, the
	script changes nothing. A second run on the same branch is then safe.

	Writes 'changed=true' or 'changed=false' to $env:GITHUB_OUTPUT when it is set.

.PARAMETER SourcesDirectory
	The repository root, with the pull request branch checked out.

.PARAMETER BaseRef
	The target branch (ex. 'origin/master').

.PARAMETER DependenciesJson
	The 'updated-dependencies-json' output of dependabot/fetch-metadata.

.PARAMETER PullRequestUrl
	The URL of the pull request, for the link at the end of each entry.
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$SourcesDirectory,
	[Parameter(Mandatory)]
	[string]$BaseRef,
	[Parameter(Mandatory)]
	[string]$DependenciesJson,
	[Parameter(Mandatory)]
	[string]$PullRequestUrl
)

$ErrorActionPreference = 'Stop'
$file = 'CHANGELOG.md'
$propsFile = 'Directory.Build.props'
$versionPattern = '(?m)^## \[(\d+)\.(\d+)\.(\d+)\] - '

function Set-Output { param($value) if ($env:GITHUB_OUTPUT) { "changed=$value" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 } }

function Get-NewestVersion {
	param([string]$Text, [string]$Source)
	$match = [regex]::Match($Text, $versionPattern)
	if (-not $match.Success) { throw "$Source has no '## [x.y.z] - yyyy-MM-dd' section." }
	return [version]::new([int]$match.Groups[1].Value, [int]$match.Groups[2].Value, [int]$match.Groups[3].Value)
}

$path = Join-Path $SourcesDirectory $file
$propsPath = Join-Path $SourcesDirectory $propsFile
$text = [IO.File]::ReadAllText($path)

$baseText = (& git -C $SourcesDirectory show "${BaseRef}:$file") -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Cannot read $file from '$BaseRef'." }
$baseVersion = Get-NewestVersion $baseText "$file on $BaseRef"
$headVersion = Get-NewestVersion $text $file

if ($headVersion -gt $baseVersion) {
	Write-Host "$file already has version $headVersion on top of $baseVersion. Nothing to change."
	Set-Output 'false'
	exit 0
}
if ($headVersion -lt $baseVersion) {
	throw "The newest version of this branch ($headVersion) is older than $BaseRef ($baseVersion). Comment '@dependabot rebase' on the pull request."
}

# --- The entries ------------------------------------------------------------------
$dependencies = @($DependenciesJson | ConvertFrom-Json)
if ($dependencies.Count -eq 0) { throw 'The pull request has no updated dependency.' }

$changed = New-Object System.Collections.Generic.List[string]
$security = New-Object System.Collections.Generic.List[string]
$link = "([PR #$($PullRequestUrl.Split('/')[-1])]($PullRequestUrl))"
foreach ($dependency in $dependencies | Sort-Object dependencyName -Unique) {
	$kind = switch ($dependency.packageEcosystem) {
		'nuget'          { 'the NuGet package' }
		'github_actions' { 'the workflow action' }
		default          { 'the dependency' }
	}
	$entry = "- Update $kind ``$($dependency.dependencyName)``"
	if ($dependency.prevVersion -and $dependency.newVersion) { $entry += " from $($dependency.prevVersion) to $($dependency.newVersion)" }
	elseif ($dependency.newVersion) { $entry += " to $($dependency.newVersion)" }

	if ($dependency.ghsaId) {
		$security.Add("$entry, which fixes [$($dependency.ghsaId)](https://github.com/advisories/$($dependency.ghsaId)). $link")
	} else {
		$changed.Add("$entry. $link")
	}
}

# --- The section, on top of the newest section --------------------------------------
$newVersion = [version]::new($baseVersion.Major, $baseVersion.Minor, $baseVersion.Build + 1)
$section = New-Object System.Collections.Generic.List[string]
$section.Add("## [$newVersion] - $([datetime]::UtcNow.ToString('yyyy-MM-dd'))")
if ($changed.Count -gt 0) { $section.Add(''); $section.Add('### Changed'); $section.AddRange($changed) }
if ($security.Count -gt 0) { $section.Add(''); $section.Add('### Security'); $section.AddRange($security) }

$newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
$first = [regex]::Match($text, $versionPattern)
$text = $text.Insert($first.Index, ($section -join $newline) + $newline + $newline)
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new([IO.File]::ReadAllBytes($path)[0] -eq 0xEF))

# --- The version in Directory.Build.props --------------------------------------------
# A text replace, not an XML save, so the rest of the file stays byte for byte the same.
$props = [IO.File]::ReadAllText($propsPath)
$propsBom = [IO.File]::ReadAllBytes($propsPath)[0] -eq 0xEF
$versionTag = [regex]::new('<Version>[^<]*</Version>')
if (-not $versionTag.IsMatch($props)) { throw "$propsFile has no <Version>." }
$props = $versionTag.Replace($props, "<Version>$newVersion</Version>", 1)
[IO.File]::WriteAllText($propsPath, $props, [Text.UTF8Encoding]::new($propsBom))

Write-Host "Added version $newVersion on top of $baseVersion, with $($changed.Count) Changed and $($security.Count) Security entries."
Set-Output 'true'
