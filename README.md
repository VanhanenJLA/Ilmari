# Ilmari

Azure IIoT scaffold for a simple building Digital Twins demo. It deploys core Azure resources (ADT, IoT Hub, Service Bus, Function App, monitoring, storage) and includes .NET apps for bootstrapping, ingestion, and simulation.

## What's in the box
- Infrastructure as code: `ilmari.bicep` (compiled template in `ilmari.json`).
- Deployment script: `Deploy-Ilmari.ps1` (Azure CLI driven).
- Bootstrapper: `Ilmari.AdtBootstrap` (uploads models and seeds a sample twin graph).
- Functions: `Ilmari.Functions` (Azure Functions isolated worker, Event Hub trigger -> ADT updates).
- Simulator: `Ilmari.Simulator` (console app sending telemetry to IoT Hub).

## Prerequisites
- Azure CLI installed and logged in (`az login`).
- .NET SDK (net8.0).
- PowerShell.
- Azure Functions Core Tools (only for local function host).
  - Homebrew: `brew tap azure/functions` then `brew install azure-functions-core-tools@4`
- Access to the target Azure subscription.

## Quick start (dev flow)
1) Deploy infra:

```powershell
./Deploy-Ilmari.ps1 -Env dev -ProjectName ilmari -Location northeurope
```

2) Bootstrap ADT models and sample twins:

```powershell
./Deploy-Ilmari.ps1 -Env dev -ProjectName ilmari -Location northeurope -RunAdtBootstrap
```

3) Provision a simulated device and copy its connection string:

```powershell
./Deploy-Simulator.ps1 -Env dev -ProjectName ilmari
```

4) Run the simulator locally:

```powershell
$env:IOTHUB_DEVICE_CONNECTION_STRING="..."
dotnet run --project ./Ilmari.Simulator/Ilmari.Simulator.csproj
```

5) Run the functions locally or deploy them to the Function App (see below).

## Local development
### Functions (Azure Functions)
The functions read IoT Hub events via the Event Hub-compatible endpoint and patches ADT twins.

Required settings:
- `ADT_SERVICE_URL` (e.g. `https://<adt-hostname>`).
- `IOTHUB_EVENTHUB_NAME` (the IoT Hub name; matches the Event Hub-compatible entity path).
- `IOTHUB_EVENTHUB_CONNECTION` (Event Hub-compatible connection string from the IoT Hub built-in endpoint).
- `AzureWebJobsStorage` (required by the Functions host when running locally).

Create a `Ilmari.Functions/local.settings.json` (do not commit):

```json
{
  "IsEncrypted": false,
  "Values": {
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "ADT_SERVICE_URL": "https://<adt-hostname>",
    "IOTHUB_EVENTHUB_NAME": "<iot-hub-name>",
    "IOTHUB_EVENTHUB_CONNECTION": "Endpoint=sb://...;SharedAccessKeyName=...;SharedAccessKey=...;EntityPath=<iot-hub-name>"
  }
}
```

Then:

```powershell
dotnet run --project ./Ilmari.Functions/Ilmari.Functions.csproj
```

Authentication to ADT uses `DefaultAzureCredential`, so `az login` or a signed-in IDE should be enough for local runs.

### Simulator
Environment variables:
- `IOTHUB_DEVICE_CONNECTION_STRING` (required).
- `ILMARI_ENV` (default `dev`).
- `SIM_ROOMS` (default `5`).
- `SIM_INTERVAL_MS` (default `5000`).

```powershell
$env:IOTHUB_DEVICE_CONNECTION_STRING="..."
dotnet run --project ./Ilmari.Simulator/Ilmari.Simulator.csproj
```

## Project layout
```
Ilmari.sln
ilmari.bicep
ilmari.json
Deploy-Ilmari.ps1
Deploy-Simulator.ps1
Ilmari.AdtBootstrap/
  Program.cs
  Models/
Ilmari.Functions/
  IngestTelemetry.cs
Ilmari.Simulator/
  Program.cs
```

## Notes
- The bootstrapper builds a demo graph: building -> floor -> rooms -> HVAC + sensors.
- Model IDs and sample data live in `Ilmari.AdtBootstrap/Program.cs` and `Ilmari.AdtBootstrap/Models/`.
- `Deploy-Ilmari.ps1` sets `ADT_SERVICE_URL` on the Function App and can run the bootstrapper.
