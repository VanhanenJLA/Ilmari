## 🔷 Ilmari IIoT Project — LLM Quick-Start Context

### 🧠 Project Overview

Ilmari is a cloud-native Industrial IoT (IIoT) reference implementation on Azure, focused on telemetry ingestion, digital twins, and observability.
The goal is to simulate, ingest, process, and model building/room sensor data using modern Azure services and .NET isolated Azure Functions.

This is not a demo-only project — it’s structured as a realistic, production-leaning architecture with infrastructure-as-code, environment separation, and extensibility in mind.

### 🏗️ Core Architecture

Telemetry flow:

- Telemetry Simulator
  - .NET console app
  - Sends JSON telemetry via Azure IoT Hub Device SDK
  - Simulates buildings → rooms → sensors
  - Configurable via environment variables (room count, sensor count, interval, env)
- Azure IoT Hub
  - Primary ingestion point for device telemetry
- Azure Functions (Isolated, .NET 10 preferred)
  - Windows Consumption (Y1) plan
  - Event-driven ingestion (IoT Hub / Event Hub trigger)
  - Responsible for:
    - Parsing telemetry
    - Enriching metadata
    - Updating Azure Digital Twins
    - Emitting logs/metrics
- Azure Digital Twins (ADT)
  - Models buildings, rooms, and sensors
  - Keeps last known sensor state
  - Used as the system of record for topology + state
- Observability & Messaging
  - Application Insights
  - (Optional / planned) Service Bus for downstream consumers

### 🧩 Digital Twin Modeling

DTDL v2 models

Example Sensor model:

```json
{
  "@context": "dtmi:dtdl:context;2",
  "@id": "dtmi:ilmari:building:Sensor;1",
  "@type": "Interface",
  "displayName": "Sensor",
  "contents": [
    { "@type": "Property", "name": "sensorType", "schema": "string" },
    { "@type": "Property", "name": "unit", "schema": "string" },
    { "@type": "Property", "name": "lastValue", "schema": "double" },
    { "@type": "Property", "name": "lastTimestamp", "schema": "dateTime" },
    { "@type": "Property", "name": "status", "schema": "string" }
  ]
}
```

Twin graph structure:

Building → Room → Sensor

Functions update sensor twins with latest telemetry values.

### 🚀 Deployment & Tooling

- Infrastructure as Code: Bicep
  - IoT Hub
  - Azure Digital Twins
  - Function App
  - Service Bus
  - Monitoring
- Deployment scripts:
  - Infra provisioning
  - Function App deployment
  - Simulator deployment
  - Function App settings injected automatically (e.g., `ADT_SERVICE_URL`)
- Local development via Azure Functions Core Tools

### ⚙️ Tech Stack

Language: C#

Runtime: .NET isolated (targeting .NET 10 where possible, currently constrained by Azure Functions support)

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
