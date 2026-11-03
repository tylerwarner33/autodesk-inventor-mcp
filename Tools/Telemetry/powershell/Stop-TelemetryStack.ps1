<#
.SYNOPSIS
	Stops the programs that Start-TelemetryStack.ps1 started.

.DESCRIPTION
	Stops each process whose executable is in -ProgramsFolder. The data stays in
	the data folder. See Docs/Telemetry.md, "Delete the data".

.PARAMETER ProgramsFolder
	The same folder as for Start-TelemetryStack.ps1.
#>
[CmdletBinding()]
param(
	[string]$ProgramsFolder = (Join-Path $env:LOCALAPPDATA 'InventorMcp\Telemetry\programs')
)

$root = (Resolve-Path $ProgramsFolder).Path

# The Collector first, so it does not send to a program that is gone.
$order = 'otelcol-contrib', 'grafana', 'prometheus', 'tempo'

foreach ($name in $order) {
	Get-Process -Name $name -ErrorAction SilentlyContinue |
		Where-Object { $_.Path -and $_.Path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase) } |
		ForEach-Object {
			Stop-Process -Id $_.Id
			Write-Host "$name stopped, process $($_.Id)."
		}
}
