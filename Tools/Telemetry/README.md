# Local telemetry stack

The config files of the OpenTelemetry Collector, Prometheus, Tempo and Grafana, the Grafana dashboard, and the
scripts that start and stop them. The programs and the data are not in the repository.

See `Docs/Telemetry.md` for how to install, start and read them. Use one setup:

| Folder | Setup |
| --- | --- |
| `powershell/` | Step A: download the programs, then start them with `Start-TelemetryStack.ps1` |
| `docker/` | Step B: run them in Docker, from `compose.yaml` |

| File | Use |
| --- | --- |
| `powershell/Start-TelemetryStack.ps1`, `powershell/Stop-TelemetryStack.ps1` | Step A: start and stop the programs |
| `powershell/otel-collector.yaml`, `powershell/tempo.yaml` | Step A configs, with `127.0.0.1` addresses |
| `docker/compose.yaml` | Step B: the four services, their ports, health checks and volumes |
| `docker/otel-collector.yaml`, `docker/tempo.yaml` | Step B configs, with `0.0.0.0` and the service names |
| `docker/Test-TelemetryStack.ps1` | Tests Step B end to end. The workflow `telemetry-stack.yml` runs it. |
| `prometheus.yaml` | Shared by both steps. It has no address of another program. |
| `grafana/provisioning/` | Shared: the data sources and the dashboard folder |
| `grafana/dashboards/inventor-mcp.json` | Shared: the dashboard |

The tag lists in both `otel-collector.yaml` files must equal `TelemetryTags` in the server. `TelemetryTagsTests` fails
when they differ.
