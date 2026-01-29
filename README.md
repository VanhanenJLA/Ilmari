# Ilmari

Azure IIoT scaffold for a simple building Digital Twins demo. It deploys core Azure resources (ADT, IoT Hub, Service Bus, Function App, monitoring, storage) and includes a .NET bootstrapper that uploads models and seeds a small twin graph.

## What's in the box
- Infrastructure as code: `ilmari.bicep` (compiled template in `ilmari.json`).
- Deployment script: `Deploy-Ilmari.ps1` (Azure CLI driven).
- Bootstrapper: `Ilmari.AdtBootstrap` (.NET app to upload DT models and create sample twins/relationships).

## Prerequisites
- Azure CLI installed and logged in (`az login`).
- .NET SDK (for the bootstrapper).
- Access to the target Azure subscription.

## Deploy
PowerShell from the repo root:

```powershell
./Deploy-Ilmari.ps1 -Env dev -ProjectName ilmari -Location northeurope
```

The script will:
- Register required Azure resource providers.
- Create the resource group (if missing).
- Deploy `ilmari.bicep`.
- Discover the ADT instance and Function App.
- Set `ADT_SERVICE_URL` on the Function App.

## Bootstrap Digital Twins (optional)
Run after deployment to upload models and seed sample twins:

```powershell
./Deploy-Ilmari.ps1 -Env dev -ProjectName ilmari -Location northeurope -RunAdtBootstrap
```

This sets `ADT_SERVICE_URL` and `ILMARI_ENV` and then runs the .NET bootstrapper.

## Project layout
```
Ilmari.sln
ilmari.bicep
ilmari.json
Deploy-Ilmari.ps1
Ilmari.AdtBootstrap/
  Program.cs
  Models/
```

## Notes
- The bootstrapper builds a demo graph: building -> floor -> rooms -> HVAC + sensors.
- Model IDs and sample data live in `Ilmari.AdtBootstrap/Program.cs` and `Ilmari.AdtBootstrap/Models/`.
- `ADT_SERVICE_URL` is also set on the Function App for downstream integrations.
