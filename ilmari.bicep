@description('Deployment location')
param location string = resourceGroup().location

@allowed([ 'dev', 'prod' ])
param env string = 'dev'

@description('Project name')
param projectName string = 'ilmari'

@description('Short unique suffix length (recommended 4-6)')
param suffixLength int = 5

var tags = {
  project: projectName
  environment: env
}

// short suffix for globally-unique names (and to avoid collisions)
var suffix = toLower(take(uniqueString(subscription().id, resourceGroup().id, projectName, env), suffixLength))

// Storage keys for AzureWebJobsStorage (inline; no user-defined function)
var storageKeys = storage.listKeys().keys
var adtDataOwnerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'bcd981a7-7f74-457b-83e1-cceb9e632ffe')

// CAF abbreviations (Microsoft Learn)
var nameLogAnalytics = 'log-${projectName}-${env}'
var nameAppInsights  = 'appi-${projectName}-${env}'
var nameAsp          = 'asp-${projectName}-${env}'

// Common “global DNS-ish” resources: add suffix
var nameAdt          = 'dt-${projectName}-${env}'
var nameIotHub       = 'iot-${projectName}-${env}'
var nameSbns         = 'sbns-${projectName}-${env}'
var nameFunc         = 'func-${projectName}-${env}'
// -${suffix}

// Storage: no hyphens, lowercase/alnum, 3-24 chars, global unique
var nameStorage      = toLower('st${projectName}${env}')

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: nameLogAnalytics
  location: location
  tags: tags
  properties: {
    retentionInDays: 30
    sku: { name: 'PerGB2018' }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: nameAppInsights
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: nameStorage
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource sb 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: nameSbns
  location: location
  tags: tags
  sku: { name: 'Standard', tier: 'Standard' }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource sbQueueEvents 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: sb
  name: 'sbq-${projectName}-${env}'
  properties: {
    lockDuration: 'PT1M'
    maxSizeInMegabytes: 1024
    deadLetteringOnMessageExpiration: true
  }
}

resource iotHub 'Microsoft.Devices/IotHubs@2023-06-30' = {
  name: nameIotHub
  location: location
  tags: tags
  sku: { name: 'S1', capacity: 1 }
  properties: {
    publicNetworkAccess: 'Enabled'
    features: 'None'
  }
}
// ADT is fucked on student sub. The resource has no overlapping allowed regions with Studen Sub system policy. RIP.
resource adt 'Microsoft.DigitalTwins/digitalTwinsInstances@2023-01-31' = {
  name: nameAdt
  location: location
  tags: tags
  properties: {
    publicNetworkAccess: 'Enabled'
  }
}

resource funcPlan 'Microsoft.Web/serverfarms@2022-09-01' = {
  name: nameAsp
  location: location
  tags: tags
  sku: { name: 'Y1', tier: 'Dynamic' }
}

resource func 'Microsoft.Web/sites@2022-09-01' = {
  name: nameFunc
  location: location
  tags: tags
  kind: 'functionapp'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: funcPlan.id
    httpsOnly: true
    siteConfig: {
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'AzureWebJobsStorage', value: 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storageKeys[0].value};EndpointSuffix=${environment().suffixes.storage}' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'ADT_SERVICE_URL', value: 'https://${adt.name}.api.neu.digitaltwins.azure.net' }
        { name: 'SB_NAMESPACE', value: sb.name }
        { name: 'SB_QUEUE', value: sbQueueEvents.name }
      ]
    }
  }
}

resource adtRoleAssign 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(adt.id, func.id, adtDataOwnerRoleId)
  scope: adt
  properties: {
    roleDefinitionId: adtDataOwnerRoleId
    principalId: func.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output adtName string = adt.name
output adtResourceId string = adt.id
