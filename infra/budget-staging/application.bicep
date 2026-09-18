targetScope = 'resourceGroup'

@description('Single-tenant Entra registration; credentials are supplied separately through Key Vault.')
@minLength(36)
@maxLength(36)
param clientId string

@description('Dedicated app-role value, never the production workforce role.')
@allowed(['Sidequest.Hackathon.Participant'])
param participantRole string = 'Sidequest.Hackathon.Participant'

@description('Initial owner only. Expanding admission requires an explicit reviewed policy/configuration change.')
@allowed(['1250fe10-b814-4735-801f-ea5a0a4c1219'])
param ownerObjectId string = '1250fe10-b814-4735-801f-ea5a0a4c1219'

var location = 'westus3'
var tenantId = '99e674a6-6773-4f53-90a3-e3ab8c37c856'
var suffix = uniqueString(subscription().subscriptionId, resourceGroup().id)
var tags = { application: 'Sidequest', environment: 'staging', managedBy: 'Bicep' }
var blobContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var secretReader = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var keyWrapper = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'e147488a-f6f5-4113-8e2d-b22465e65bf6')

resource sqlIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: 'sidequest-app'
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'sqh${suffix}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    networkAcls: { defaultAction: 'Allow', bypass: 'None' }
  }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    isVersioningEnabled: true
    deleteRetentionPolicy: { enabled: true, days: 7 }
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}
resource covers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'covers'
  properties: { publicAccess: 'None' }
}
resource keyring 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'data-protection'
  properties: { publicAccess: 'None' }
}
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'sqh-${suffix}'
  location: location
  tags: tags
  properties: {
    tenantId: tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enablePurgeProtection: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
    networkAcls: { defaultAction: 'Allow', bypass: 'None' }
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
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'sidequest-hackathon-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json('0.1') }
    features: { enableLogAccessUsingOnlyResourcePermissions: true }
  }
}
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'sidequest-hackathon-insights'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: true
  }
}
resource email 'Microsoft.Communication/emailServices@2023-03-31' = {
  name: 'sidequest-hackathon-email'
  location: 'global'
  tags: tags
  properties: { dataLocation: 'United States' }
}
resource domain 'Microsoft.Communication/emailServices/domains@2023-03-31' = {
  parent: email
  name: 'AzureManagedDomain'
  location: 'global'
  tags: tags
  properties: {
    domainManagement: 'AzureManaged'
    userEngagementTracking: 'Disabled'
  }
}
resource communication 'Microsoft.Communication/communicationServices@2023-03-31' = {
  name: 'sidequest-hackathon-${suffix}'
  location: 'global'
  tags: tags
  properties: {
    dataLocation: 'United States'
    linkedDomains: [domain.id]
  }
}
// ACS documents Read + Write for managed-identity email, not a nonexistent Email Sender role.
// The definition is RG-bound and the actual assignment is only on this ACS resource.
resource emailRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, 'sidequest-hackathon-acs-read-write')
  properties: {
    roleName: 'Sidequest hackathon ACS read-write'
    description: 'Documented ACS managed-identity email permissions on the dedicated hackathon resource.'
    type: 'CustomRole'
    assignableScopes: [resourceGroup().id]
    permissions: [{
      actions: [
        'Microsoft.Communication/CommunicationServices/Read'
        'Microsoft.Communication/CommunicationServices/Write'
      ]
      notActions: []
      dataActions: []
      notDataActions: []
    }]
  }
}
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'sidequest-hackathon-plan'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: 'B1', tier: 'Basic', capacity: 1 }
  properties: { reserved: true }
}
resource web 'Microsoft.Web/sites@2024-04-01' = {
  name: 'sidequest-hackathon-${suffix}'
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: { '${sqlIdentity.id}': {} }
  }
  properties: {
    enabled: false
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet /home/site/wwwroot/Sidequest.Web.dll'
      alwaysOn: true
      webSocketsEnabled: true
      healthCheckPath: '/health/ready'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
    }
  }
}
resource ftp 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: web
  name: 'ftp'
  properties: { allow: false }
}
resource scm 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: web
  name: 'scm'
  properties: { allow: false }
}
resource coverRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: covers
  name: guid(covers.id, web.id, blobContributor)
  properties: { principalId: web.identity.principalId, principalType: 'ServicePrincipal', roleDefinitionId: blobContributor }
}
resource keyringRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyring
  name: guid(keyring.id, web.id, blobContributor)
  properties: { principalId: web.identity.principalId, principalType: 'ServicePrincipal', roleDefinitionId: blobContributor }
}
resource secretRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, web.id, secretReader)
  properties: { principalId: web.identity.principalId, principalType: 'ServicePrincipal', roleDefinitionId: secretReader }
}
resource wrapRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: wrappingKey
  name: guid(wrappingKey.id, web.id, keyWrapper)
  properties: { principalId: web.identity.principalId, principalType: 'ServicePrincipal', roleDefinitionId: keyWrapper }
}
resource metricsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: insights
  name: guid(insights.id, web.id, 'metrics')
  properties: {
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
  }
}
resource senderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: communication
  name: guid(communication.id, web.id, emailRole.id)
  properties: { principalId: web.identity.principalId, principalType: 'ServicePrincipal', roleDefinitionId: emailRole.id }
}
resource settings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: web
  name: 'appsettings'
  properties: {
    ASPNETCORE_ENVIRONMENT: 'Staging'
    ASPNETCORE_URLS: 'http://0.0.0.0:8080'
    SCM_DO_BUILD_DURING_DEPLOYMENT: 'false'
    WEBSITE_RUN_FROM_PACKAGE: '1'
    Authentication__Mode: 'Entra'
    Authentication__AdmissionPolicy: 'hackathon-assigned-users'
    Authentication__HackathonRole: participantRole
    Authentication__HackathonParticipants__0: ownerObjectId
    Authentication__BootstrapAdministrator__TenantId: tenantId
    Authentication__BootstrapAdministrator__ObjectId: ownerObjectId
    AzureAd__TenantId: tenantId
    AzureAd__Instance: environment().authentication.loginEndpoint
    AzureAd__ClientId: clientId
    AzureAd__ClientSecret: '@Microsoft.KeyVault(SecretUri=${vault.properties.vaultUri}secrets/entra-client-secret)'
    AllowedHosts: web.properties.defaultHostName
    ConnectionStrings__Sidequest: 'Server=tcp:sidequest-sql-b7ljjkoqcaedc${environment().suffixes.sqlServerHostname},1433;Database=sidequest;Authentication=Active Directory Managed Identity;User Id=${sqlIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False;'
    Directory__Graph__TenantId: tenantId
    Directory__Credentials__TenantId: tenantId
    Directory__Credentials__ClientId: clientId
    Directory__Credentials__ClientSecret: '@Microsoft.KeyVault(SecretUri=${vault.properties.vaultUri}secrets/entra-client-secret)'
    Delivery__Email__Endpoint: 'https://${communication.properties.hostName}'
    Delivery__Email__SenderAddress: 'DoNotReply@${domain.properties.fromSenderDomain}'
    Media__Storage__ServiceUri: storage.properties.primaryEndpoints.blob
    Media__Storage__ContainerName: covers.name
    Hosting__Azure__Enabled: 'true'
    Hosting__Azure__AppServiceProxyEnabled: 'true'
    Hosting__DataProtection__ApplicationName: 'Sidequest:hackathon:${suffix}'
    Hosting__DataProtection__BlobUri: '${storage.properties.primaryEndpoints.blob}${keyring.name}/keys.xml'
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
output applicationIsEnabled bool = false
output vaultName string = vault.name
output storageAccountName string = storage.name
output communicationName string = communication.name
output senderAddress string = 'DoNotReply@${domain.properties.fromSenderDomain}'
