## 🔷 Ilmari IIoT Project — LLM Quick-Start Context

### 🧠 Project Overview

Ilmari is a cloud-native Industrial IoT (IIoT) reference implementation on Azure, focused on telemetry ingestion, digital twins, and observability.
The goal is to simulate, ingest, process, and model building telemetry using Azure services and .NET isolated Azure Functions.

This is not a demo-only project — it’s structured as a realistic, production-leaning architecture with infrastructure-as-code, environment separation, and extensibility in mind.

### 🏗️ Core Architecture

Telemetry flow:

- Telemetry Simulator
  - .NET console app (net10.0)
  - Sends per-room JSON telemetry via Azure IoT Hub Device SDK
  - Simulates a single building with multiple rooms and HVAC behavior
  - Configurable via environment variables (`SIM_ROOMS`, `SIM_INTERVAL_MS`, `ILMARI_ENV`)
- Azure IoT Hub
  - Primary ingestion point for device telemetry
- Azure Functions (Isolated, net8.0)
  - Consumption (Y1) plan
  - Event-driven ingestion (IoT Hub / Event Hub trigger)
  - Responsible for:
    - Parsing telemetry (batch of events)
    - Updating Azure Digital Twins for Room + HvacUnit
    - Emitting logs/metrics
- Azure Digital Twins (ADT)
  - Models building, floor, room, sensors, and HVAC units
  - Keeps last known state for room + HVAC twins
  - Used as the system of record for topology + state
- Observability & Messaging
  - Application Insights
  - Service Bus (provisioned; optional for downstream consumers)

### 🧩 Digital Twin Modeling

DTDL v2 models

Room model excerpt:

```json
{
  "@context": "dtmi:dtdl:context;2",
  "@id": "dtmi:ilmari:building:Room;1",
  "@type": "Interface",
  "displayName": "Room",
  "contents": [
    { "@type": "Property", "name": "roomId", "schema": "string" },
    { "@type": "Property", "name": "occupancy", "schema": "boolean" },
    { "@type": "Property", "name": "tempC", "schema": "double" },
    { "@type": "Property", "name": "humidityPct", "schema": "double" },
    { "@type": "Property", "name": "co2Ppm", "schema": "double" },
    { "@type": "Property", "name": "energyKw", "schema": "double" },
    { "@type": "Property", "name": "lastUpdated", "schema": "dateTime" }
  ]
}
```

Twin graph structure:

Building → Floor → Room

Room → HvacUnit (servedBy)
Room → Sensor (hasSensor)

Functions update Room + HvacUnit twins with latest telemetry values.

### 🚀 Deployment & Tooling

- Infrastructure as Code: Bicep
  - IoT Hub
  - Azure Digital Twins
  - Function App
  - Service Bus
  - Monitoring
- Deployment scripts:
  - Infra provisioning
  - ADT bootstrapper (models + sample graph)
  - Simulator provisioning
  - Function App settings injected automatically (e.g., `ADT_SERVICE_URL`)
- Local development via Azure Functions Core Tools

### ⚙️ Tech Stack

Language: C#

Runtime:
- Functions: .NET isolated (net8.0)
- Simulator + ADT bootstrapper: net10.0

Azure Services:
- IoT Hub
- Azure Functions
- Azure Digital Twins
- Application Insights
- Service Bus (optional / future)

### 🎯 Current Focus Areas

- Telemetry ingestion correctness (batch vs single event handling)
- EventHubTrigger configuration for IoT Hub messages
- Clean separation between:
  - Simulation
  - Ingestion
  - Twin updates
- Preparing for future additions:
  - Rules/alerts
  - Historical storage
  - Real-time dashboards
