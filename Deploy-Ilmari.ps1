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
  [string]$BootstrapProject = "./Ilmari.AdtBootstrap/Ilmari.AdtBootstrap.csproj"
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
az account show 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
  throw "Not logged in to Azure CLI. Run: az login"
}

# 2) Register required providers (safe to run repeatedly)
Write-Host "Registering resource providers (idempotent)..."
$providers = @(
  "Microsoft.DigitalTwins",
  "Microsoft.Devices",
  "Microsoft.ServiceBus",
  "Microsoft.Web",
  "Microsoft.Insights",
  "Microsoft.OperationalInsights",
  "Microsoft.Storage",
  "Microsoft.Authorization"
)

foreach ($p in $providers) {
  az provider register --namespace $p 1>$null
}

# 3) Create RG if missing
Write-Host "Ensuring resource group exists..."
$rgExists = az group exists --name $ResourceGroupName | ConvertFrom-Json
if (-not $rgExists) {
  az group create --name $ResourceGroupName --location $Location 1>$null
  Write-Host "Created resource group: $ResourceGroupName"
} else {
  Write-Host "Resource group already exists: $ResourceGroupName"
}

# 4) Optional: compile bicep for sanity
Write-Host "Compiling Bicep (sanity check)..."
az bicep build --file $TemplateFile 1>$null

# 5) Deploy
Write-Host "Deploying Bicep..."
$deploymentName = "deploy-$ProjectName-$Env-" + (Get-Date -Format "yyyyMMdd-HHmmss")

az deployment group create `
  --name $deploymentName `
  --resource-group $ResourceGroupName `
  --template-file $TemplateFile `
  --parameters env=$Env projectName=$ProjectName location=$Location

Write-Host ""
Write-Host "✅ Deployment completed."

# 6) Discover DT + Function names (by resource type + CAF prefix)
Write-Host "Discovering Azure Digital Twins + Function App resources..."

$adtName = az resource list -g $ResourceGroupName `
  --resource-type "Microsoft.DigitalTwins/digitalTwinsInstances" `
  --query "[?starts_with(name, 'dt-$ProjectName-$Env-')].name | [0]" -o tsv

if ([string]::IsNullOrWhiteSpace($adtName)) {
  throw "Could not find ADT instance in RG $ResourceGroupName with prefix dt-$ProjectName-$Env-"
}

$functionAppName = az resource list -g $ResourceGroupName `
  --resource-type "Microsoft.Web/sites" `
  --query "[?starts_with(name, 'func-$ProjectName-$Env-')].name | [0]" -o tsv

if ([string]::IsNullOrWhiteSpace($functionAppName)) {
  throw "Could not find Function App in RG $ResourceGroupName with prefix func-$ProjectName-$Env-"
}

Write-Host "ADT instance:     $adtName"
Write-Host "Function App:     $functionAppName"

# 7) Set ADT_SERVICE_URL using real ADT hostname (api.neu...)
Write-Host "Fetching ADT hostname..."
$adtHost = az dt show -g $ResourceGroupName -n $adtName --query hostName -o tsv
if ([string]::IsNullOrWhiteSpace($adtHost)) {
  throw "Failed to fetch ADT hostName for $adtName"
}

$adtServiceUrl = "https://$adtHost"
Write-Host "Setting Function App appsetting ADT_SERVICE_URL=$adtServiceUrl"

az functionapp config appsettings set `
  -g $ResourceGroupName -n $functionAppName `
  --settings "ADT_SERVICE_URL=$adtServiceUrl" | Out-Null

Write-Host "✅ App setting updated."

# 8) Optional: run ADT bootstrapper
if ($RunAdtBootstrap) {
  if (-not (Test-Path $BootstrapProject)) {
    throw "BootstrapProject not found: $BootstrapProject"
  }

  Write-Host "Running ADT bootstrapper..."
  $env:ADT_SERVICE_URL = $adtServiceUrl
  $env:ILMARI_ENV = $Env

  dotnet run --project $BootstrapProject
}
else {
  Write-Host "Tip: run bootstrapper with -RunAdtBootstrap once you've added it."
}

Write-Host "Done. List resources: az resource list -g $ResourceGroupName -o table"