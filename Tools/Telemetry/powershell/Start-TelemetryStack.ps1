<#
.SYNOPSIS
	Starts the local telemetry stack: the Collector, Prometheus, Tempo and
	Grafana.

.DESCRIPTION
	Step A of Docs/Telemetry.md. Each program runs in the background, with no
	window, and listens on 127.0.0.1 only. The Collector and Tempo configs come
	from this folder. The Prometheus config and the Grafana files come from the
	parent folder, which the Docker stack shares. The data and
	the logs go to -DataFolder, outside the repository.

	A program that already runs is not started again. Stop-TelemetryStack.ps1
	stops all four.

.PARAMETER ProgramsFolder
	The folder that holds one subfolder for each program, as Docs/Telemetry.md,
	"Install the programs", describes.

.PARAMETER DataFolder
	The folder for the data and the logs.
#>
[CmdletBinding()]
param(
	[string]$ProgramsFolder = (Join-Path $env:LOCALAPPDATA 'InventorMcp\Telemetry\programs'),
	[string]$DataFolder = (Join-Path $env:LOCALAPPDATA 'InventorMcp\Telemetry')
)

$ErrorActionPreference = 'Stop'

$configFolder = $PSScriptRoot
$sharedFolder = Split-Path $PSScriptRoot
$logFolder = Join-Path $DataFolder 'logs'

foreach ($folder in 'logs', 'prometheus', 'tempo', 'grafana') {
	$null = New-Item -ItemType Directory -Force -Path (Join-Path $DataFolder $folder)
}

function Find-Program([string]$Name) {
	$found = Get-ChildItem -Path $ProgramsFolder -Filter $Name -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1

	if ($null -eq $found) {
		throw "$Name is not in $ProgramsFolder. See Docs/Telemetry.md, 'Install the programs'."
	}

	return $found.FullName
}

function Start-Program([string]$Name, [string]$Exe, [string[]]$Arguments, [hashtable]$Environment = @{}, [string]$WorkingDirectory = $null) {
	if (Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe }) {
		Write-Host "$Name already runs."
		return
	}

	# A child process inherits the variables. Start-Process -Environment needs PowerShell 7.4, so they are set here
	# and restored after the start.
	$previous = @{}

	foreach ($key in $Environment.Keys) {
		$previous[$key] = [Environment]::GetEnvironmentVariable($key)
		[Environment]::SetEnvironmentVariable($key, $Environment[$key])
	}

	try {
		$log = Join-Path $logFolder "$Name.log"
		$start = @{
			FilePath = $Exe
			WorkingDirectory = if ($WorkingDirectory) { $WorkingDirectory } else { Split-Path $Exe }
			WindowStyle = 'Hidden'
			RedirectStandardOutput = "$log.out"
			RedirectStandardError = $log
			PassThru = $true
		}

		if ($Arguments.Count -gt 0) {
			# Start-Process joins the arguments with spaces, so a path with a space needs quotes.
			$start.ArgumentList = $Arguments | ForEach-Object { if ($_ -match '\s') { '"{0}"' -f $_ } else { $_ } }
		}

		$process = Start-Process @start
		Write-Host "$Name started, process $($process.Id). Log: $log"
	}
	finally {
		foreach ($key in $previous.Keys) {
			[Environment]::SetEnvironmentVariable($key, $previous[$key])
		}
	}
}

$tempo = Find-Program 'tempo.exe'
Start-Program 'tempo' $tempo @("-config.file=$(Join-Path $configFolder 'tempo.yaml')", '-config.expand-env=true') @{
	INVENTORMCP_TELEMETRY_DATA = $DataFolder.Replace('\', '/')
}

$prometheus = Find-Program 'prometheus.exe'
Start-Program 'prometheus' $prometheus @(
	"--config.file=$(Join-Path $sharedFolder 'prometheus.yaml')",
	"--storage.tsdb.path=$(Join-Path $DataFolder 'prometheus')",
	'--storage.tsdb.retention.time=90d',
	# The Collector sends the metrics over OTLP. The 0 at the start time of each counter counts the first call of a
	# server process, and the dashboard's "anchored" ranges count without extrapolation.
	'--web.enable-otlp-receiver',
	'--enable-feature=exemplar-storage,created-timestamp-zero-ingestion,promql-extended-range-selectors',
	'--web.listen-address=127.0.0.1:9090'
)

$grafana = Find-Program 'grafana.exe'
$grafanaHome = Split-Path (Split-Path $grafana)
Start-Program 'grafana' $grafana @('server', "--homepath=$grafanaHome") @{
	GF_PATHS_DATA = Join-Path $DataFolder 'grafana'
	GF_PATHS_LOGS = $logFolder
	GF_PATHS_PROVISIONING = Join-Path $sharedFolder 'grafana\provisioning'
	GF_SERVER_HTTP_ADDR = '127.0.0.1'
	GF_SERVER_HTTP_PORT = '3000'
	GF_ANALYTICS_REPORTING_ENABLED = 'false'
	GF_ANALYTICS_CHECK_FOR_UPDATES = 'false'
	GF_AUTH_ANONYMOUS_ENABLED = 'true'
	GF_AUTH_ANONYMOUS_ORG_ROLE = 'Admin'
	GF_AUTH_DISABLE_LOGIN_FORM = 'true'
	INVENTORMCP_PROMETHEUS_URL = 'http://127.0.0.1:9090'
	INVENTORMCP_TEMPO_URL = 'http://127.0.0.1:3200'
	INVENTORMCP_DASHBOARDS = Join-Path $sharedFolder 'grafana\dashboards'
} $grafanaHome

$collector = Find-Program 'otelcol-contrib.exe'
Start-Program 'otel-collector' $collector @("--config=$(Join-Path $configFolder 'otel-collector.yaml')")

Write-Host ''
Write-Host 'Grafana:    http://127.0.0.1:3000'
Write-Host 'Prometheus: http://127.0.0.1:9090'
Write-Host 'Set OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4317 in the MCP client entry, and reconnect the client.'
