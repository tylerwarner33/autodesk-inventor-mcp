# Local telemetry stack

The config files of the OpenTelemetry Collector, Prometheus, Tempo and Grafana, the Grafana dashboard, and the
scripts that start and stop them. The programs and the data are not in the repository.

See `Docs/Telemetry.md` for how to install, start and read them.

| File | Use |
| --- | --- |
| `Start-TelemetryStack.ps1`, `Stop-TelemetryStack.ps1` | Step A: the programs started by hand |
| `otel-collector.yaml`, `prometheus.yaml`, `tempo.yaml` | Step A configs, with `127.0.0.1` addresses |
| `grafana/provisioning/` | The data sources and the dashboard folder, for Steps A and B |
| `grafana/dashboards/inventor-mcp.json` | The dashboard |
| `compose/` | Step B: Docker Compose, and the configs with service names |

The tag lists in both `otel-collector.yaml` files must equal `TelemetryTags` in the server. `TelemetryTagsTests` fails
when they differ.
