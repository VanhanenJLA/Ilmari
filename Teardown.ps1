param(
    [string]$Env = "dev"
)

$project = "ilmari"
$rgName = "rg-$project-$Env"

Write-Host "========================================"
Write-Host " Ilmari IIoT teardown"
Write-Host " Environment : $Env"
Write-Host " ResourceGrp : $rgName"
Write-Host "========================================"

az resource list --resource-group $rgName --output table

# Safety rails
if ($Env -ne "dev") {
    Write-Error "Refusing to delete non-dev environment ($Env)."
    Write-Error "If you REALLY want this, remove the safety check."
    exit 1
}

$confirm = Read-Host "Type '$rgName' to confirm deletion"
if ($confirm -ne $rgName) {
    Write-Error "Confirmation failed. Aborting."
    exit 1
}

Write-Host "Deleting resource group..."
az group delete `
    --name $rgName `
    --yes `
    --no-wait

Write-Host "Delete initiated."
Write-Host "Resources will be removed asynchronously."
