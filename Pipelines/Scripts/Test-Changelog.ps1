<#
.SYNOPSIS
	Checks CHANGELOG.md and the version in Directory.Build.props, and that a pull
	request adds a new version.

.DESCRIPTION
	Used by .github/workflows/changelog.yml. The format is in
	.agents/rules/changelog.md.

	1. The format. The file starts with '# Changelog'. Each section heading is
	   '## [x.y.z] - yyyy-MM-dd', one section for each pull request. The versions
	   go down from the top, and a date is not newer than the date above it or in
	   the future. Many sections can have the same date. Each section has one or
	   more subsections, in this order: Breaking, Added, Changed, Fixed, Removed,
	   Security. Each subsection has one or more '- ' entries.
	2. The newest version is the <Version> in Directory.Build.props.
	3. With -BaseRef: the pull request adds exactly one section on top of the
	   newest section of BaseRef, and its version is the next one:
	   a Breaking entry bumps the major version, an Added entry the minor
	   version, and other entries the patch version.

	Writes GitHub Actions workflow commands, so errors show on the run summary and
	on the pull request.

.PARAMETER SourcesDirectory
	The repository root.

.PARAMETER BaseRef
	The commit to compare HEAD with. In a pull request run, HEAD is the merge
	commit that GitHub makes, and 'HEAD^1' is the target branch. Without this
	parameter, steps 1 and 2 run (ex. a manual run). To run step 3 locally,
	use 'origin/master'.
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$SourcesDirectory,
	[string]$BaseRef
)

$ErrorActionPreference = 'Stop'
$errors = 0
$file = 'CHANGELOG.md'
$propsFile = 'Directory.Build.props'
$subsections = @('Breaking', 'Added', 'Changed', 'Fixed', 'Removed', 'Security')
$headingFormat = '## [x.y.z] - yyyy-MM-dd'

function Write-PipelineError { param($msg) Write-Host "::error::$msg"; $script:errors++ }

# Returns the version after $Version for a section with the subsections $Subs.
function Get-NextVersion {
	param([version]$Version, [string[]]$Subs)
	if ($Subs -ccontains 'Breaking') { return [version]::new($Version.Major + 1, 0, 0) }
	if ($Subs -ccontains 'Added')    { return [version]::new($Version.Major, $Version.Minor + 1, 0) }
	return [version]::new($Version.Major, $Version.Minor, $Version.Build + 1)
}

$path = Join-Path $SourcesDirectory $file
if (-not (Test-Path -LiteralPath $path)) {
	Write-PipelineError "$file does not exist."
	exit 1
}
$lines = @((Get-Content -LiteralPath $path -Raw -Encoding UTF8) -split '\r?\n')

# --- 1. Format ----------------------------------------------------------------------
if ($lines[0] -cne '# Changelog') { Write-PipelineError "${file}:1: the first line must be '# Changelog'." }

$sections = New-Object System.Collections.Generic.List[object]   # Line, Version, Date, and Subs of each '## '.
$section = $null; $sub = $null; $subIndex = -1; $entries = 0; $subLine = 0
$latest = [datetime]::UtcNow.Date.AddDays(1)   # A developer east of UTC can be one day ahead.

# Reports an empty subsection or section when the next heading or the end is reached.
function Close-Subsection { if ($script:sub -and $script:entries -eq 0) { Write-PipelineError "${file}:$($script:subLine): '### $($script:sub)' has no '- ' entry." } }
function Close-Section {
	Close-Subsection
	if ($script:section -and $script:section.Subs.Count -eq 0) {
		Write-PipelineError "${file}:$($script:section.Line): '## [$($script:section.Version)]' has no subsection."
	}
}

for ($i = 1; $i -lt $lines.Count; $i++) {
	$n = $i + 1
	$line = $lines[$i]
	if ($line -cmatch '^## ') {
		Close-Section
		$section = $null; $sub = $null; $subIndex = -1; $entries = 0
		$date = [datetime]::MinValue
		if ($line -cnotmatch '^## \[(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\] - (\d{4}-\d{2}-\d{2})$' -or
			-not [datetime]::TryParseExact($Matches[4], 'yyyy-MM-dd', [cultureinfo]::InvariantCulture, 'None', [ref]$date)) {
			Write-PipelineError "${file}:${n}: '$line' must be '$headingFormat' (ex. '## [0.2.0] - 2026-10-28')."
			continue
		}
		$version = [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
		if ($sections.Count -gt 0) {
			$above = $sections[$sections.Count - 1]
			if ($version -ge $above.Version) {
				Write-PipelineError "${file}:${n}: version $version must be lower than $($above.Version) above it. The newest version is first."
			}
			if ($date -gt $above.Date) {
				Write-PipelineError "${file}:${n}: the date $($date.ToString('yyyy-MM-dd')) is newer than the date of $($above.Version) above it."
			}
		}
		if ($date -gt $latest) { Write-PipelineError "${file}:${n}: '$line' has a date in the future." }
		$section = [pscustomobject]@{ Line = $n; Version = $version; Date = $date; Subs = (New-Object System.Collections.Generic.List[string]) }
		$sections.Add($section)
	}
	elseif ($line -cmatch '^### ') {
		Close-Subsection
		$entries = 0; $subLine = $n
		$name = $line.Substring(4).Trim()
		$index = [array]::IndexOf($subsections, $name)
		if (-not $section) { Write-PipelineError "${file}:${n}: '$line' is not in a '$headingFormat' section." }
		if ($index -lt 0) {
			Write-PipelineError "${file}:${n}: '$line' is not a known subsection. Use: $($subsections -join ', ')."
		} elseif ($index -le $subIndex) {
			Write-PipelineError "${file}:${n}: '$line' is out of order or a repeat. The order is: $($subsections -join ', ')."
		}
		$sub = $name; $subIndex = [math]::Max($subIndex, $index)
		if ($section) { $section.Subs.Add($name) }
	}
	elseif ($line -cmatch '^- ') {
		if (-not $sub) { Write-PipelineError "${file}:${n}: the entry is not under a '### ' subsection." }
		$entries++
	}
	elseif ($line -cmatch '^#') {
		Write-PipelineError "${file}:${n}: '$line' is not a known heading. Use '$headingFormat' and '### <subsection>'."
	}
}
Close-Section
if ($sections.Count -eq 0) {
	Write-PipelineError "$file has no '$headingFormat' section."
	exit 1
}
$newest = $sections[0]
Write-Host "Checked: $file format ($($sections.Count) versions, newest $($newest.Version))"

# --- 2. The version in Directory.Build.props -----------------------------------------
$props = [xml](Get-Content -LiteralPath (Join-Path $SourcesDirectory $propsFile) -Raw)
$propsVersion = $props.SelectSingleNode('//Version').InnerText.Trim()
if ($propsVersion -cne "$($newest.Version)") {
	Write-PipelineError "$propsFile has <Version> '$propsVersion', but the newest $file version is $($newest.Version). Use the same version in both files."
}
Write-Host "Checked: $propsFile version ($propsVersion)"

# --- 3. The pull request adds the next version ---------------------------------------
if ($BaseRef) {
	$null = & git -C $SourcesDirectory rev-parse --verify --quiet "$BaseRef^{commit}"
	if ($LASTEXITCODE -ne 0) {
		Write-PipelineError "Cannot find the base commit '$BaseRef'. The checkout needs 'fetch-depth: 2' or more."
		exit 1
	}
	$baseText = (& git -C $SourcesDirectory show "${BaseRef}:$file" 2>$null) -join "`n"
	$baseMatch = [regex]::Match($baseText, '(?m)^## \[(\d+)\.(\d+)\.(\d+)\] - ')
	if ($LASTEXITCODE -ne 0 -or -not $baseMatch.Success) {
		Write-Host "The target branch has no versioned $file. This pull request adds the first one."
	} else {
		$baseVersion = [version]::new([int]$baseMatch.Groups[1].Value, [int]$baseMatch.Groups[2].Value, [int]$baseMatch.Groups[3].Value)
		$expected = Get-NextVersion $baseVersion $newest.Subs
		$bumpRule = 'a Breaking entry bumps the major version, an Added entry the minor version, and other entries the patch version'
		if ($newest.Version -eq $baseVersion) {
			Write-PipelineError "This pull request adds no new version to $file. Add a '## [x.y.z] - <today>' section on top of [$baseVersion] ($bumpRule), and set the same version in $propsFile - see .agents/rules/changelog.md."
		} elseif ($sections.Count -lt 2 -or $sections[1].Version -ne $baseVersion) {
			Write-PipelineError "This pull request must add exactly one section, directly on top of [$baseVersion], the newest version on the target branch. If another pull request merged first, move your entries to a new section on top of it."
		} elseif ($newest.Version -ne $expected) {
			Write-PipelineError "The new version must be $expected, not $($newest.Version): $bumpRule. Use $expected in $file and in $propsFile."
		} else {
			Write-Host "Checked: this pull request adds version $($newest.Version) on top of $baseVersion"
		}
	}
}

if ($errors -gt 0) { exit 1 }
