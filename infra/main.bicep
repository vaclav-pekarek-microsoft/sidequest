targetScope = 'resourceGroup'

@description('Approved Azure region for application, employee data, backups and logs.')
param location string

@allowed(['staging', 'production'])
param environmentName string

@description('Named operational owner or managed team alias. This does not substitute for deployment approval.')
@minLength(1)
param operationalOwner string

@description('Approved network ranges; no organizational address space is assumed by the template.')
@minLength(1)
param virtualNetworkAddressPrefix string
@minLength(1)
param applicationSubnetAddressPrefix string
@minLength(1)
param privateEndpointSubnetAddressPrefix string

@description('Workforce tenant, OIDC app and explicit bootstrap administrator approved by the tenant owner.')
@minLength(36)
@maxLength(36)
param workforceTenantId string
@minLength(36)
@maxLength(36)
param workforceClientId string
@minLength(1)
param workforceRole string
@minLength(36)
@maxLength(36)
param bootstrapAdministratorObjectId string

@description('Approved Entra administrator GROUP for database bootstrap and schema deployment, not the application identity.')
@minLength(1)
param sqlAdministratorGroupName string
@minLength(36)
@maxLength(36)
param sqlAdministratorGroupObjectId string

@description('Approved recovery windows. Blob soft deletion retains one extra day required by point-in-time restore.')
@minValue(1)
@maxValue(364)
param blobRestoreDays int

@minValue(1)
@maxValue(35)
param sqlPointInTimeRetentionDays int

@description('Approved operational log retention; no arbitrary content-retention cleanup is deployed.')
@allowed([30, 31, 60, 90, 120, 180, 270, 365, 550, 730])
param logRetentionDays int

@description('Initially leave disabled. Enable only after identity grants, secrets, migrations and release approvals are verified.')
param enableApplication bool = false

@description('Approved bounded plan sizing. Acceptance at 300 users must be measured on the chosen size.')
@allowed(['P1v3', 'P2v3', 'P3v3'])
param appServiceSku string = 'P1v3'

@description('Approved SQL size; this template does not claim a measured load or recovery result.')
@allowed(['S0', 'S1', 'S2', 'S3'])
param sqlSku string = 'S1'

var suffix = uniqueString(subscription().subscriptionId, resourceGroup().id, environmentName)
var prefix = 'sq-${environmentName}'
var tags = {
  application: 'Sidequest'
  environment: environmentName
  operationalOwner: operationalOwner
  managedBy: 'Bicep'
}
var blobContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var secretReader = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var keyWrapper = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'e147488a-f6f5-4113-8e2d-b22465e65bf6')

resource outboundAddress 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: '${prefix}-egress'
  location: location
  tags: tags
  sku: { name: 'Standard' }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
  }
}

resource outboundGateway 'Microsoft.Network/natGateways@2024-05-01' = {
  name: '${prefix}-egress'
  location: location
  tags: tags
  sku: { name: 'Standard' }
  properties: {
    idleTimeoutInMinutes: 10
    publicIpAddresses: [{ id: outboundAddress.id }]
  }
}

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${prefix}-network'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [virtualNetworkAddressPrefix]
    }
    subnets: [
      {
        name: 'application'
        properties: {
          addressPrefix: applicationSubnetAddressPrefix
          natGateway: { id: outboundGateway.id }
          delegations: [
            {
              name: 'app-service'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
        }
      }
      {
        name: 'private-endpoints'
        properties: {
          addressPrefix: privateEndpointSubnetAddressPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

var privateServices = [
  { name: 'blob', zone: 'privatelink.blob.${environment().suffixes.storage}', groupId: 'blob', resourceId: storage.id }
  { name: 'sql', zone: 'privatelink${environment().suffixes.sqlServerHostname}', groupId: 'sqlServer', resourceId: sql.id }
  { name: 'vault', zone: 'privatelink${replace(environment().suffixes.keyvaultDns, '.vault.', '.vaultcore.')}', groupId: 'vault', resourceId: vault.id }
]

resource privateZones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for service in privateServices: {
  name: service.zone
  location: 'global'
  tags: tags
}]

resource zoneLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (service, index) in privateServices: {
  parent: privateZones[index]
  name: '${prefix}-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: { id: network.id }
  }
}]

resource endpoints 'Microsoft.Network/privateEndpoints@2024-05-01' = [for service in privateServices: {
  name: '${prefix}-${service.name}'
  location: location
  tags: tags
  properties: {
    subnet: { id: '${network.id}/subnets/private-endpoints' }
    privateLinkServiceConnections: [
      {
        name: service.name
        properties: {
          privateLinkServiceId: service.resourceId
          groupIds: [service.groupId]
        }
      }
    ]
  }
}]

resource zoneGroups 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = [for (service, index) in privateServices: {
  parent: endpoints[index]
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: service.name
        properties: { privateDnsZoneId: privateZones[index].id }
      }
    ]
  }
}]

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'sq${suffix}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_ZRS' }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    networkAcls: { defaultAction: 'Deny', bypass: 'None' }
    encryption: {
      keySource: 'Microsoft.Storage'
      requireInfrastructureEncryption: true
      services: { blob: { enabled: true, keyType: 'Account' } }
    }
  }
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    isVersioningEnabled: true
    changeFeed: { enabled: true }
    deleteRetentionPolicy: { enabled: true, days: blobRestoreDays + 1 }
    containerDeleteRetentionPolicy: { enabled: true, days: blobRestoreDays + 1 }
    restorePolicy: { enabled: true, days: blobRestoreDays }
  }
}

resource covers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'covers'
  properties: { publicAccess: 'None' }
}

resource protectionKeys 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'data-protection'
  properties: { publicAccess: 'None' }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'sq-${suffix}'
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enablePurgeProtection: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Disabled'
    networkAcls: { defaultAction: 'Deny', bypass: 'None' }
  }
}

resource wrappingKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: vault
  name: 'data-protection'
  properties: {
    kty: 'RSA'
    keySize: 3072
    keyOps: ['wrapKey', 'unwrapKey']
    attributes: { enabled: true }
  }
}

resource sql 'Microsoft.Sql/servers@2023-08-01' = {
  name: '${prefix}-${suffix}'
  location: location
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      login: sqlAdministratorGroupName
      sid: sqlAdministratorGroupObjectId
      tenantId: tenant().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sql
  name: 'Sidequest'
  location: location
  tags: tags
  sku: { name: sqlSku, tier: 'Standard' }
  properties: {
    requestedBackupStorageRedundancy: 'Zone'
    zoneRedundant: false
  }
}

resource databaseRetention 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01' = {
  parent: database
  name: 'default'
  properties: { retentionDays: sqlPointInTimeRetentionDays }
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: logRetentionDays
    features: { enableLogAccessUsingOnlyResourcePermissions: true }
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-insights'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: true
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${prefix}-plan'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: appServiceSku, tier: 'PremiumV3', capacity: 1 }
  properties: { reserved: true }
}

resource web 'Microsoft.Web/sites@2024-04-01' = {
  name: '${prefix}-${suffix}'
  location: location
  tags: tags
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    enabled: enableApplication
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: true
    virtualNetworkSubnetId: '${network.id}/subnets/application'
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      webSocketsEnabled: true
      vnetRouteAllEnabled: true
      healthCheckPath: '/health/ready'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
    }
  }
}

resource ftpPublishing 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: web
  name: 'ftp'
  properties: { allow: false }
}

resource scmPublishing 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: web
  name: 'scm'
  properties: { allow: false }
}

resource coverRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: covers
  name: guid(covers.id, web.id, blobContributor)
  properties: { principalId: web.identity.principalId, principalType: 'ServicePrincipal', roleDefinitionId: blobContributor }
}

resource protectionRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: protectionKeys
  name: guid(protectionKeys.id, web.id, blobContributor)
  properties: { principalId: web.identity.principalId, principalType: 'ServicePrincipal', roleDefinitionId: blobContributor }
}

resource secretRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, web.id, secretReader)
  properties: { principalId: web.identity.principalId, principalType: 'ServicePrincipal', roleDefinitionId: secretReader }
}

resource wrappingRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: wrappingKey
  name: guid(wrappingKey.id, web.id, keyWrapper)
  properties: { principalId: web.identity.principalId, principalType: 'ServicePrincipal', roleDefinitionId: keyWrapper }
}

resource telemetryRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: insights
  name: guid(insights.id, web.id, 'monitoring-metrics-publisher')
  properties: {
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
  }
}

resource settings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: web
  name: 'appsettings'
  properties: {
    ASPNETCORE_ENVIRONMENT: 'Production'
    SCM_DO_BUILD_DURING_DEPLOYMENT: 'false'
    Authentication__Mode: 'Entra'
    Authentication__WorkforceRole: workforceRole
    Authentication__BootstrapAdministrator__TenantId: workforceTenantId
    Authentication__BootstrapAdministrator__ObjectId: bootstrapAdministratorObjectId
    AzureAd__TenantId: workforceTenantId
    AzureAd__Instance: environment().authentication.loginEndpoint
    AzureAd__ClientId: workforceClientId
    AzureAd__ClientSecret: '@Microsoft.KeyVault(SecretUri=${vault.properties.vaultUri}secrets/entra-client-secret)'
    AllowedHosts: web.properties.defaultHostName
    ConnectionStrings__Sidequest: 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=${database.name};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;'
    Media__Storage__ServiceUri: storage.properties.primaryEndpoints.blob
    Media__Storage__ContainerName: covers.name
    Hosting__Azure__Enabled: 'true'
    Hosting__DataProtection__ApplicationName: 'Sidequest:${environmentName}:${suffix}'
    Hosting__DataProtection__BlobUri: '${storage.properties.primaryEndpoints.blob}${protectionKeys.name}/keys.xml'
    Hosting__DataProtection__KeyUri: '${vault.properties.vaultUri}keys/${wrappingKey.name}'
    Operations__Monitoring__Enabled: 'true'
    APPLICATIONINSIGHTS_CONNECTION_STRING: insights.properties.ConnectionString
    APPLICATIONINSIGHTS_STATSBEAT_DISABLED: 'true'
    APPLICATIONINSIGHTS_SDKSTATS_DISABLED: 'true'
  }
}

output applicationName string = web.name
output applicationUrl string = 'https://${web.properties.defaultHostName}'
output applicationPrincipalId string = web.identity.principalId
output sqlServerName string = sql.name
output sqlServerHost string = sql.properties.fullyQualifiedDomainName
output databaseName string = database.name
output vaultName string = vault.name
output storageAccountName string = storage.name
output applicationIsEnabled bool = enableApplication
