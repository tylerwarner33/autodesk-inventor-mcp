<#
.SYNOPSIS
	Tests the Docker stack end to end: starts it, sends telemetry from the real
	server, and finds it in Prometheus and Tempo.

.DESCRIPTION
	Step B of Docs/Telemetry.md, as a test. It needs Docker and a built
	InventorMcp.Server, not Inventor: each call is inventor_skill, which works
	with Inventor closed.

	Each run gives its calls a new client name ("stack-test <run>"), which the
	server sends as inventor.client.name. So the data of an earlier run in the
	same volumes does not change the result.

	The stack is stopped at the end with 'docker compose down', which keeps the
	data volumes. The exit code is 0 when each check passes.

.PARAMETER ServerPath
	InventorMcp.Server.dll. By default the Release build, else the Debug build,
	of this repository.

.PARAMETER KeepRunning
	Leaves the stack running, to look at it in Grafana after the test.
#>
[CmdletBinding()]
param(
	[string]$ServerPath,
	[switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'

# Two server processes, because each process is a new series in Prometheus, as for two sessions of a client.
$calls = 2
$composeFile = Join-Path $PSScriptRoot 'compose.yaml'
$repositoryRoot = Split-Path (Split-Path (Split-Path $PSScriptRoot))
$client = "stack-test $([guid]::NewGuid().ToString('N').Substring(0, 8))"
$failures = 0

function Write-Check([string]$Name, [bool]$Passed, [string]$Detail = '') {
	if ($Passed) {
		Write-Host "PASS  $Name $Detail"
	}
	else {
		Write-Host "FAIL  $Name $Detail"
		$script:failures++
	}
}

function Wait-Until([scriptblock]$Check, [int]$Seconds) {
	$deadline = [DateTime]::UtcNow.AddSeconds($Seconds)

	do {
		try {
			$result = & $Check

			if ($result) {
				return $result
			}
		}
		catch {
			# Not there yet. The next try can find it.
		}

		Start-Sleep -Seconds 2
	} while ([DateTime]::UtcNow -lt $deadline)
}

# One server process with one tool call. Closing stdin ends the server, which exports what is left.
function Invoke-ServerCall([string]$Dll) {
	$start = [System.Diagnostics.ProcessStartInfo]::new('dotnet', "`"$Dll`"")
	# Windows PowerShell 5.1 runs on .NET Framework, where the default is true and a redirect then throws.
	$start.UseShellExecute = $false
	$start.RedirectStandardInput = $true
	$start.RedirectStandardOutput = $true
	$start.Environment['DOTNET_NOLOGO'] = '1'
	$start.Environment['OTEL_EXPORTER_OTLP_ENDPOINT'] = 'http://127.0.0.1:4317'

	$process = [System.Diagnostics.Process]::Start($start)

	function Read-Response([int]$Id) {
		while ($true) {
			$line = $process.StandardOutput.ReadLineAsync()

			if (-not $line.Wait(30000) -or $null -eq $line.Result) {
				return $null
			}

			if ($line.Result -match "`"id`":$Id\b") {
				return $line.Result
			}
		}
	}

	try {
		$name, $version = $client -split ' '
		$initialize = @{
			jsonrpc = '2.0'; id = 1; method = 'initialize'
			params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = $name; version = $version } }
		}
		$process.StandardInput.WriteLine(($initialize | ConvertTo-Json -Compress -Depth 5))
		$null = Read-Response 1

		$process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
		$process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"inventor_skill","arguments":{"action":"list"}}}')
		$answer = Read-Response 2

		return $answer -match '"result"' -and $answer -notmatch '"isError":true'
	}
	finally {
		$process.StandardInput.Close()

		if (-not $process.WaitForExit(15000)) {
			$process.Kill()
		}

		$process.Dispose()
	}
}

if (-not $ServerPath) {
	$ServerPath = 'Release', 'Debug' |
		ForEach-Object { Join-Path $repositoryRoot "Source/InventorMcp.Server/bin/$_/InventorMcp.Server.dll" } |
		Where-Object { Test-Path $_ } |
		Select-Object -First 1
}

if (-not $ServerPath -or -not (Test-Path $ServerPath)) {
	throw 'InventorMcp.Server.dll was not found. Build the server first (dotnet build Source/InventorMcp.Server -c Release), or give -ServerPath.'
}

Write-Host "Server $ServerPath, client name '$client'"

try {
	# --wait returns when Prometheus and Grafana are healthy, and the Collector and Tempo run.
	docker compose -f $composeFile up -d --wait --wait-timeout 180
	Write-Check 'The stack started' ($LASTEXITCODE -eq 0)

	if ($LASTEXITCODE -ne 0) {
		exit 1
	}

	# Grafana's health check of each data source queries Prometheus and Tempo.
	foreach ($uid in 'inventor-mcp-prometheus', 'inventor-mcp-tempo') {
		$health = Wait-Until { (Invoke-RestMethod "http://127.0.0.1:3000/api/datasources/uid/$uid/health").status -eq 'OK' } 60
		Write-Check "Data source $uid works" ([bool]$health)
	}

	$dashboards = Invoke-RestMethod 'http://127.0.0.1:3000/api/search?type=dash-db'
	Write-Check 'Dashboard "Inventor MCP" is loaded' ('Inventor MCP' -in $dashboards.title)

	$answered = @(1..$calls | Where-Object { Invoke-ServerCall $ServerPath }).Count
	Write-Check 'The server answered each call' ($answered -eq $calls) "($answered of $calls)"

	# The client name is a label of the call counter only. The duration histogram leaves it out.
	$query = [uri]::EscapeDataString("sum(inventor_tool_calls_total{inventor_client_name=`"$client`",gen_ai_tool_name=`"inventor_skill`",inventor_outcome=`"ok`"})")
	$count = Wait-Until {
		$value = [int]((Invoke-RestMethod "http://127.0.0.1:9090/api/v1/query?query=$query").data.result | Select-Object -First 1).value[1]
		if ($value -eq $calls) { $value }
	} 60
	Write-Check 'Prometheus counted each call' ($count -eq $calls) "($([int]$count) of $calls)"

	# Tempo is not published, so it is searched from inside the stack, as Grafana searches it. A new trace is found
	# some seconds after the call.
	$traceQl = [uri]::EscapeDataString("{ span.inventor.client.name = `"$client`" }")
	$traces = Wait-Until {
		$found = @((docker compose -f $composeFile exec -T grafana wget -qO- "http://tempo:3200/api/search?q=$traceQl&limit=50" | ConvertFrom-Json).traces |
			Where-Object rootTraceName -eq 'tools/call inventor_skill')
		if ($found.Count -ge $calls) { $found }
	} 90
	Write-Check 'Tempo has a trace of each call' (@($traces).Count -ge $calls) "($(@($traces).Count) of $calls)"
}
finally {
	if ($KeepRunning) {
		Write-Host 'The stack still runs: Grafana at http://127.0.0.1:3000.'
	}
	else {
		docker compose -f $composeFile down
	}
}

if ($failures -gt 0) {
	Write-Host "$failures check(s) failed."
	exit 1
}

Write-Host 'Each check passed.'
