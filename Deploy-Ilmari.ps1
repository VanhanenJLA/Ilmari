<#
.SYNOPSIS
Deploy Ilmari IIoT scaffold (ADT + IoT Hub + SB + Function + Monitoring) using Bicep,
then set Function App setting ADT_SERVICE_URL from the deployed ADT hostname.

.PREREQS
- Azure CLI installed
- Logged in: az login
- ilmari.bicep present in the same folder (or change $TemplateFile)
#>

param(
  [ValidateSet('dev','prod')]
  [string]$Env = 'dev',
  [string]$ProjectName = 'ilmari',
  [string]$Location = 'westeurope',
  [string]$ResourceGroupName = "rg-$ProjectName-$Env",
  [string]$TemplateFile = "./$ProjectName.bicep",

  # Optional: run a .NET bootstrapper after setting ADT_SERVICE_URL
  [switch]$RunAdtBootstrap,

  # If running bootstrapper: path to csproj
  [string]$BootstrapProject = "./Ilmari.AdtBootstrap/Ilmari.AdtBootstrap.csproj",

  # Optional: run the functions locally after deployment (blocks in foreground)
  [switch]$RunFunctions,

  # If running functions: path to csproj
  [string]$FunctionsProject = "./Ilmari.Functions/Ilmari.Functions.csproj",

  # Optional: publish functions to the deployed Function App
  [switch]$PublishFunctions,

  # Optional: provision a simulated device after deployment
  [switch]$DeploySim,

  # If provisioning a simulated device: path to script
  [string]$DeploySimScript = "./Deploy-Simulated-Device.ps1",

  # If provisioning a simulated device: device id
  [string]$SimDeviceId = "ilmari-sim-01"
)

$ErrorActionPreference = "Stop"

Write-Host @'
  ,--.,--.                         ,--. 
  `--'|  |,--,--,--. ,--,--.,--.--.`--' 
  ,--.|  ||        |' ,-.  ||  .--',--. 
  |  ||  ||  |  |  |\ '-'  ||  |   |  | 
  `--'`--'`--`--`--' `--`--'`--'   `--'
'@

Write-Host "Env:        $Env"
Write-Host "Project:    $ProjectName"
Write-Host "Location:   $Location"
Write-Host "RG:         $ResourceGroupName"
Write-Host "Template:   $TemplateFile"
Write-Host ""

# 1) Ensure logged in
Write-Host "Checking Azure CLI session..."
az account show | Out-Null
if ($LASTEXITCODE -ne 0) {
  throw "Not logged in to Azure CLI. Run: az login"
}

# 2) Register required providers (safe to run repeatedly)
Write-Host "Registering resource providers..."
$providers = @(
  "Microsoft.DigitalTwins",
  "Microsoft.Devices",
  "Microsoft.ServiceBus",
  "Microsoft.Web",
  "Microsoft.Insights",
  "Microsoft.OperationalInsights",
  "Microsoft.Storage",
  "Microsoft.Authorization",
  "Microsoft.Devices"
)

foreach ($p in $providers) {
  az provider register --namespace $p | Out-Null
}

# 3) Create RG if missing
Write-Host "Ensuring resource group exists..."
$rgExists = az group exists --name $ResourceGroupName | ConvertFrom-Json
if (-not $rgExists) {
  az group create --name $ResourceGroupName --location $Location | Out-Null
  Write-Host "Created resource group: $ResourceGroupName"
} else {
  Write-Host "Resource group exists: $ResourceGroupName"
}

# 4) Optional: compile bicep for sanity
Write-Host "Compiling Bicep..."
az bicep build --file $TemplateFile | Out-Null

# 5) Deploy
Write-Host "Deploying Bicep..."
$deploymentName = "deploy-$ProjectName-$Env" + (Get-Date -Format "yyyyMMdd-HHmmss")

az deployment group create `
  --name $deploymentName `
  --resource-group $ResourceGroupName `
  --template-file $TemplateFile `
  --parameters env=$Env projectName=$ProjectName location=$Location `
  --only-show-errors | Out-Null

Write-Host ""
Write-Host "✅ Deployment completed."
Write-Host ""

# 6) Discover DT + Function names (by resource type + CAF prefix)
Write-Host "Discovering Azure Digital Twins + Function App resources..."
$adtName = az resource list -g $ResourceGroupName `
  --resource-type "Microsoft.DigitalTwins/digitalTwinsInstances" `
  --query "[?starts_with(name, 'dt-$ProjectName-$Env')].name | [0]" -o tsv

if ([string]::IsNullOrWhiteSpace($adtName)) {
  throw "Could not find ADT instance in RG $ResourceGroupName with prefix dt-$ProjectName-$Env"
}

$functionAppName = az resource list -g $ResourceGroupName `
  --resource-type "Microsoft.Web/sites" `
  --query "[?starts_with(name, 'func-$ProjectName-$Env')].name | [0]" -o tsv

if ([string]::IsNullOrWhiteSpace($functionAppName)) {
  throw "Could not find Function App in RG $ResourceGroupName with prefix func-$ProjectName-$Env"
}

$iotHubName = az resource list -g $ResourceGroupName `
  --resource-type "Microsoft.Devices/IotHubs" `
  --query "[?starts_with(name, 'iot-$ProjectName-$Env')].name | [0]" -o tsv

if ([string]::IsNullOrWhiteSpace($iotHubName)) {
  throw "Could not find IoT Hub in RG $ResourceGroupName with prefix iot-$ProjectName-$Env"
}

$sbNamespaceName = "sbns-$ProjectName-$Env"
$alertsTopicName = "alerts-$ProjectName-$Env"

Write-Host "ADT instance:     $adtName"
Write-Host "Function App:     $functionAppName"
Write-Host "IoT Hub:          $iotHubName"
Write-Host "SB Namespace:     $sbNamespaceName"
Write-Host "Alerts Topic:     $alertsTopicName"
Write-Host ""

# 6.5) Ensure caller has ADT data-plane RBAC on the ADT instance (idempotent)
Write-Host "Ensuring caller has Azure Digital Twins Data Owner on the ADT instance..."

# Get ADT resource id (scope)
$adtId = az resource show -g $ResourceGroupName -n $adtName `
  --resource-type "Microsoft.DigitalTwins/digitalTwinsInstances" `
  --query id -o tsv

if ([string]::IsNullOrWhiteSpace($adtId)) {
  throw "Failed to resolve ADT resource id for $adtName"
}

# Identify current signed-in principal
$acct = az account show --query "{user:user.name, userType:user.type, tenant:tenantId}" -o json | ConvertFrom-Json

# user.type is usually: "user" or "servicePrincipal"
$principalObjectId = $null

if ($acct.userType -eq "servicePrincipal") {
  # When running under SP, user.name is often appId/clientId
  $principalObjectId = az ad sp show --id $acct.user --query id -o tsv 2>$null
} else {
  # When running as a user
  $principalObjectId = az ad signed-in-user show --query id -o tsv 2>$null
}

if ([string]::IsNullOrWhiteSpace($principalObjectId)) {
  throw "Could not determine signed-in principal objectId. Are you able to query Entra ID? (az ad ...)"
}

$roleName = "Azure Digital Twins Data Owner"

# Check if assignment already exists
$existing = az role assignment list `
  --assignee-object-id $principalObjectId `
  --scope $adtId `
  --query "[?roleDefinitionName=='$roleName'] | length(@)" -o tsv

if ($existing -eq "0") {
  Write-Host "Assigning role '$roleName' on scope: $adtId"
  az role assignment create `
    --assignee-object-id $principalObjectId `
    --assignee-principal-type User `
    --role $roleName `
    --scope $adtId | Out-Null

  # RBAC propagation can take a moment; this avoids immediate 403s in bootstrap
  Write-Host "Sleeping 10s to ensure RBAC propagation..."
  Start-Sleep -Seconds 10
} else {
  Write-Host "Role already assigned."
}

Write-Host ""
Write-Host "✅ RBAC configured."

# 7) Set ADT_SERVICE_URL using real ADT hostname (api.neu...)
Write-Host ""
Write-Host "Fetching ADT hostname..."
$adtHost = az resource show `
  -g $ResourceGroupName `
  -n $adtName `
  --resource-type "Microsoft.DigitalTwins/digitalTwinsInstances" `
  --query "properties.hostName" -o tsv

if ([string]::IsNullOrWhiteSpace($adtHost)) {
  throw "Failed to fetch ADT hostName for $adtName"
}

$adtServiceUrl = "https://$adtHost"
Write-Host "Fetching IoT Hub Event Hub-compatible settings..."

$ehConnStr = az iot hub connection-string show `
  --hub-name $iotHubName `
  --default-eventhub `
  --policy-name iothubowner `
  --query "connectionString" -o tsv

if ([string]::IsNullOrWhiteSpace($ehConnStr)) {
  throw "Failed to fetch Event Hub-compatible connection string for $iotHubName"
}

$csParts = @{}
foreach ($p in ($ehConnStr -split ';')) {
  if ($p -match '=') {
    $kv = $p -split '=', 2
    $csParts[$kv[0]] = $kv[1]
  }
}

$ehEntityPath = $csParts["EntityPath"]

if ([string]::IsNullOrWhiteSpace($ehEntityPath)) {
  throw "Failed to parse EntityPath from Event Hub-compatible connection string for $iotHubName"
}

Write-Host "Fetching Service Bus connection string..."
$sbConnStr = az servicebus namespace authorization-rule keys list `
  -g $ResourceGroupName `
  --namespace-name $sbNamespaceName `
  --name "RootManageSharedAccessKey" `
  --query "primaryConnectionString" -o tsv

if ([string]::IsNullOrWhiteSpace($sbConnStr)) {
  throw "Failed to resolve Service Bus connection string for $sbNamespaceName"
}

Write-Host ""
Write-Host "Updating Function App app settings..."

az functionapp config appsettings set `
  -g $ResourceGroupName -n $functionAppName `
  --settings `
    "ADT_SERVICE_URL=$adtServiceUrl" `
    "IOTHUB_EVENTHUB_NAME=$ehEntityPath" `
    "IOTHUB_EVENTHUB_CONNECTION=$ehConnStr" `
    "ALERTS_SERVICEBUS_CONNECTION=$sbConnStr" `
    "ALERTS_TOPIC_NAME=$alertsTopicName" | Out-Null

Write-Host ""
Write-Host "ADT_SERVICE_URL=$adtServiceUrl"
Write-Host "IOTHUB_EVENTHUB_NAME=$ehEntityPath"
Write-Host "IOTHUB_EVENTHUB_CONNECTION=$ehConnStr"
Write-Host "ALERTS_TOPIC_NAME=$alertsTopicName"
Write-Host ""
Write-Host "✅ App setting updated."

# 8) Optional: publish functions to Function App
if ($PublishFunctions) {
  $functionsDir = Split-Path -Parent $FunctionsProject
  if (-not (Test-Path $functionsDir)) {
    throw "Functions project directory not found: $functionsDir"
  }

  Write-Host ""
  Write-Host "Publishing Ilmari.Functions..."
  Push-Location $functionsDir
  try {
    func azure functionapp publish $functionAppName --dotnet-isolated
  } finally {
    Pop-Location
  }
}

# 9) Optional: run ADT bootstrapper
if ($RunAdtBootstrap) {
  if (-not (Test-Path $BootstrapProject)) {
    throw "BootstrapProject not found: $BootstrapProject"
  }

  Write-Host ""
  Write-Host "Running ADT bootstrapper..."
  $env:ADT_SERVICE_URL = $adtServiceUrl
  $env:ILMARI_ENV = $Env
  dotnet run --project $BootstrapProject
}

# 10) Optional: provision a simulated device
if ($DeploySim) {
  if (-not (Test-Path $DeploySimScript)) {
    throw "DeploySimScript not found: $DeploySimScript"
  }

  Write-Host ""
  Write-Host "Provisioning simulated device..."
  & $DeploySimScript -Env $Env -ProjectName $ProjectName -ResourceGroupName $ResourceGroupName -DeviceId $SimDeviceId
}

# 11) Optional: run functions locally (foreground)
if ($RunFunctions) {
  if (-not (Test-Path $FunctionsProject)) {
    throw "FunctionsProject not found: $FunctionsProject"
  }

  Write-Host ""
  Write-Host "Running functions locally (Ctrl+C to stop)..."
  $env:ADT_SERVICE_URL = $adtServiceUrl
  $env:IOTHUB_EVENTHUB_NAME = $ehEntityPath
  $env:IOTHUB_EVENTHUB_CONNECTION = $ehConnStr

  dotnet run --project $FunctionsProject
}

Write-Host ""
Write-Host "✅ Deployment completed."
Write-Host ""
Write-Host "List resources: az resource list -g $ResourceGroupName -o table"
