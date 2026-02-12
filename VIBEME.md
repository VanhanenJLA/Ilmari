## Ilmari IIoT Project - LLM Context (Current State)

### Snapshot (as of 2026-02-12)

Ilmari is an Azure-based IIoT reference implementation that is currently functional end-to-end for simulated telemetry ingestion, Digital Twins state updates, and rule-based alert publishing.

### What Is Working

- Infrastructure deployment through `Deploy-Ilmari.ps1` and `ilmari.bicep`.
- Telemetry simulation from `Ilmari.Simulator` (net10.0) to IoT Hub over MQTT.
- Event ingestion in `Ilmari.Functions/IngestTelemetry.cs` (Event Hub trigger from IoT Hub endpoint).
- ADT patch updates for Room and HVAC twins.
- Scheduled alert processing in `Ilmari.Functions/ProcessAlertRules.cs`.
- Service Bus topic publishing for threshold breaches (temperature and CO2).
- Application Insights logging pipeline, including explicit Info-level override and exception middleware.

### Implemented Architecture

Telemetry flow:

1. `Ilmari.Simulator` sends per-room JSON telemetry events.
2. IoT Hub receives device telemetry.
3. `IngestTelemetry` function parses each event and updates ADT twin properties.
4. `ProcessAlertRules` timer function queries ADT room twins every 10 seconds.
5. Breaches are emitted as Service Bus messages.

Key Azure services in active use:

- IoT Hub
- Azure Functions (isolated worker, .NET 8)
- Azure Digital Twins
- Service Bus
- Application Insights

### Current Rules Engine Status

Implemented rules:

- `TempOutOfRange`: min 18 C, max 24 C, severity 2.
- `Co2OutOfRange`: max 1000 ppm, severity 2.

Current behavior:

- One alert per breached metric per room per timer evaluation.
- No deduplication/suppression window yet.
- Alert payload and Service Bus application properties are structured and consistent.

### Simulator Status

`Ilmari.Simulator` currently includes deterministic scenario logic:

- Occupancy transitions.
- Comfort drift (temperature issue) scenario.
- CO2 drift scenario.
- HVAC mode/setpoint/power variation.

Configurable via:

- `IOTHUB_DEVICE_CONNECTION_STRING` (required)
- `ILMARI_ENV`
- `SIM_ROOMS`
- `SIM_INTERVAL_MS`

### Known Gaps / Risks

- `Ilmari.AdtBootstrap/Program.cs` currently uses a hardcoded ADT URL instead of env-driven configuration.
- Alerting currently lacks deduplication, cooldown windows, and recovery events.
- No historical data sink yet (only current state in ADT + emitted alerts).
- Local development docs and deployment docs are mostly current, but bootstrap configuration should be aligned with the rest of the env-based setup.

### Recommended Next Steps

1. Remove hardcoded ADT URL in bootstrap; require `ADT_SERVICE_URL`.
2. Add alert deduplication/cooldown and explicit recovery messages.
3. Introduce a time-series sink for telemetry history and trend analysis.
4. Add tests for ingestion parsing edge cases and rule evaluation behavior.
