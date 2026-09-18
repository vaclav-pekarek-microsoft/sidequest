targetScope = 'resourceGroup'

@description('Verified Entra SQL administrator group; no credentials or account bootstrap are accepted.')
@minLength(36)
@maxLength(36)
param sqlAdministratorGroupObjectId string

@description('Display name of the independently verified security group.')
@minLength(1)
param sqlAdministratorGroupName string

var location = 'westus3'
var tags = {
  application: 'Sidequest'
  environment: 'staging'
  managedBy: 'Bicep'
  budgetProfile: 'low-cost-hackathon'
}

resource applicationIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'sidequest-app'
  location: location
  tags: tags
}

resource migrationIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'sidequest-migration'
  location: location
  tags: tags
}

resource server 'Microsoft.Sql/servers@2023-08-01' = {
  name: 'sidequest-sql-${uniqueString(subscription().subscriptionId, resourceGroup().id)}'
  location: location
  tags: tags
  properties: {
    version: '12.0'
    publicNetworkAccess: 'Enabled'
    minimalTlsVersion: '1.2'
    restrictOutboundNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      principalType: 'Group'
      login: sqlAdministratorGroupName
      sid: sqlAdministratorGroupObjectId
      tenantId: subscription().tenantId
    }
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: server
  name: 'sidequest'
  location: location
  tags: tags
  sku: { name: 'Basic', tier: 'Basic', capacity: 5 }
  properties: {
    maxSizeBytes: 2147483648
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Local'
  }
}

resource retention 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01' = {
  parent: database
  name: 'default'
  properties: { retentionDays: 7 }
}

output serverName string = server.name
output databaseName string = database.name
output applicationIdentityResourceId string = applicationIdentity.id
output migrationIdentityResourceId string = migrationIdentity.id
