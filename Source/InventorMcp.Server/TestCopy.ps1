# Copies a document tree for a test, through Inventor Apprentice, so no iLogic rule runs and Inventor can stay closed.
# inventor_test_copy runs this in a child Windows PowerShell process, so the native Apprentice DLL never loads into the
# server. It reads one request file (JSON) and writes one result (JSON) to the output.
#
# The walk goes through File.ReferencedFileDescriptors, not AllReferencedDocuments, because the documents miss a
# suppressed component. The copies are plain file copies, so each keeps the internal identity that ReplaceReference
# needs, and only files in the target folder are saved. See Docs/Tasks/Usage-Findings-Implementation-Plan.md, Phase 4.
param([Parameter(Mandatory)] [string] $RequestFile)

$ErrorActionPreference = 'Stop'
# The server reads the output as UTF-8. Windows PowerShell writes the OEM code page by default.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$request = Get-Content -LiteralPath $RequestFile -Raw | ConvertFrom-Json
$sourceFolder = [System.IO.Path]::GetFullPath($request.sourceFolder).TrimEnd('\')
$target = [System.IO.Path]::GetFullPath($request.target).TrimEnd('\')
$prefix = [string]$request.prefix
$problems = New-Object System.Collections.Generic.List[string]
$kept = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$map = [ordered]@{}

function Test-Under([string]$path, [string]$folder) {
    return $path.StartsWith($folder + '\', [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-CopyPath([string]$source) {
    $relative = $source.Substring($sourceFolder.Length + 1)
    $folder = Split-Path $relative -Parent
    $name = $prefix + (Split-Path $relative -Leaf)
    $copy = if ($folder) { Join-Path (Join-Path $target $folder) $name } else { Join-Path $target $name }
    # Normalized, so a '..' cannot pass the check below and still resolve outside the target.
    $copy = [System.IO.Path]::GetFullPath($copy)
    if (-not (Test-Under $copy $target)) { throw "Refusing to copy to '$copy', which is outside the target." }
    return $copy
}

function Write-Result([object]$result) {
    $result | ConvertTo-Json -Depth 6 -Compress
}

if ((Test-Under $target $sourceFolder) -and -not $prefix) {
    Write-Result @{ error = 'invalid-arguments'; message = "The target '$target' is inside the source folder, so give a prefix." }
    exit 0
}

$apprentice = New-Object -ComObject Inventor.ApprenticeServer
try {
    $version = $apprentice.SoftwareVersion.DisplayVersion

    # Walk the reference tree of each top file.
    $pending = New-Object System.Collections.Generic.Stack[string]
    foreach ($source in $request.sources) { $pending.Push([System.IO.Path]::GetFullPath($source)) }
    while ($pending.Count -gt 0) {
        $source = $pending.Pop()
        if ($map.Contains($source)) { continue }
        if (-not (Test-Under $source $sourceFolder)) { [void]$kept.Add($source); continue }

        $map[$source] = Get-CopyPath $source
        $document = $apprentice.Open($source)
        try {
            if ($document.NeedsMigrating) { $problems.Add("'$source' needs migration to Apprentice $version, so its copy cannot be saved. Migrate the master first, or use Apprentice of its release.") }
            foreach ($descriptor in $document.File.ReferencedFileDescriptors) {
                if ($descriptor.ReferenceMissing) { $problems.Add("'$source' references a missing file '$($descriptor.FullFileName)'."); continue }
                $pending.Push($descriptor.FullFileName)
            }
        }
        finally { $document.Close() }
    }

    $existing = @($map.Values | Where-Object { Test-Path -LiteralPath $_ })
    if ($existing.Count -gt 0) {
        Write-Result @{ error = 'target-exists'; message = 'Some copies exist already, so nothing was copied. Give a new target or prefix.'; existing = $existing }
        exit 0
    }

    # Copy the files, and clear the read-only attribute that a Vault workspace sets.
    foreach ($entry in $map.GetEnumerator()) {
        $folder = Split-Path $entry.Value -Parent
        if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder | Out-Null }
        Copy-Item -LiteralPath $entry.Key -Destination $entry.Value
        $item = Get-Item -LiteralPath $entry.Value
        if ($item.IsReadOnly) { $item.IsReadOnly = $false }
    }

    # Point each copy at the other copies. Save only files in the target folder.
    $repointed = 0
    foreach ($copy in $map.Values) {
        if (-not (Test-Under $copy $target)) { throw "Refusing to save '$copy', which is outside the target." }
        $document = $apprentice.Open($copy)
        try {
            $changed = 0
            foreach ($descriptor in $document.File.ReferencedFileDescriptors) {
                $referenced = $descriptor.FullFileName
                if ($map.Contains($referenced)) {
                    $descriptor.ReplaceReference($map[$referenced])
                    $changed++
                }
            }
            if ($changed -gt 0) {
                $save = $apprentice.FileSaveAs
                $save.AddFileToSave($document, $document.FullFileName)
                $save.ExecuteSave()
                $repointed += $changed
            }
        }
        catch { $problems.Add("Could not point '$copy' at the copies: $($_.Exception.Message)") }
        finally { $document.Close() }
    }

    # Check: no copy may reference a file that was copied, and no reference may be missing.
    foreach ($copy in $map.Values) {
        $document = $apprentice.Open($copy)
        try {
            foreach ($descriptor in $document.File.ReferencedFileDescriptors) {
                if ($map.Contains($descriptor.FullFileName)) { $problems.Add("'$copy' still references the master '$($descriptor.FullFileName)'.") }
                if ($descriptor.ReferenceMissing) { $problems.Add("'$copy' has a missing reference '$($descriptor.FullFileName)'.") }
            }
        }
        finally { $document.Close() }
    }

    Write-Result @{
        apprenticeVersion = $version
        target = $target
        copied = @($map.GetEnumerator() | ForEach-Object { @{ source = $_.Key; copy = $_.Value } })
        referencesRepointed = $repointed
        referencesKept = @($kept)
        problems = @($problems)
    }
}
catch {
    Write-Result @{ error = 'copy-failed'; message = $_.Exception.Message; problems = @($problems) }
}
finally {
    $apprentice.Close()
}
