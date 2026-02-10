param(
    [string]$Env = 'dev',
    [string]$ProjectName = 'ilmari',
    [string]$ResourceGroupName = "rg-$ProjectName-$Env",
    [string]$DeviceId = "ilmari-sim-01"
)

$ErrorActionPreference = "Stop"

Write-Host "Provisioning simulated IoT device..."
$iotHubName = az resource list -g $ResourceGroupName `
  --resource-type "Microsoft.Devices/IotHubs" `
  --query "[?starts_with(name, 'iot-$ProjectName-$Env')].name | [0]" -o tsv

if ([string]::IsNullOrWhiteSpace($iotHubName)) {
    throw "IoT Hub not found in RG $ResourceGroupName"
}

Write-Host "IoT Hub: $iotHubName"
Write-Host "DeviceId: $DeviceId"

$exists = az iot hub device-identity show `
  --hub-name $iotHubName `
  --device-id $DeviceId `
  --resource-group $ResourceGroupName | Out-Null

if ($exists) {
    Write-Host "Device already exists. Skipping create."
}
else {
    az iot hub device-identity create `
    --hub-name $iotHubName `
    --device-id $DeviceId `
    --resource-group $ResourceGroupName | Out-Null
    Write-Host "✅ Device created."
}

$connStr = az iot hub device-identity connection-string show `
  --hub-name $iotHubName `
  --device-id $DeviceId `
  --resource-group $ResourceGroupName `
  -o tsv

Write-Host ""
Write-Host "Device connection string:"
Write-Host $connStr
Write-Host ""
Write-Host "Saving to .NET User Secrets for Ilmari.Simulator..."
dotnet user-secrets set "IOTHUB_DEVICE_CONNECTION_STRING" "$connStr" --project "./Ilmari.Simulator/Ilmari.Simulator.csproj"
