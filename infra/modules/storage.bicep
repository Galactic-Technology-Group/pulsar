param name string
param location string
param tags object
@description('Principal that gets Storage Blob Data Contributor (the apps\' managed identity).')
param writerPrincipalId string

// Storage Blob Data Contributor
var blobContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

// Holds the portal's ASP.NET Core Data Protection key ring so auth cookies survive restarts and
// are readable by every replica. Tiny: a handful of XML files.
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false   // identity-only access
    supportsHttpsTrafficOnly: true
  }
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 30 }
    containerDeleteRetentionPolicy: { enabled: true, days: 30 }
  }
}

resource dataProtection 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'dataprotection'
  properties: { publicAccess: 'None' }
}

resource writer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, writerPrincipalId, blobContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobContributorRoleId)
    principalId: writerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output dataProtectionBlobUri string = '${storage.properties.primaryEndpoints.blob}dataprotection/keys.xml'
