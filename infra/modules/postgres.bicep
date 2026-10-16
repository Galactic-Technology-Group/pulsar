param name string
param location string
param tags object
param adminLogin string
@secure()
param adminPassword string
param databaseName string = 'nms'
@description('Burstable B2s is enough for ~50 sites at 1-minute checks. Scale up when ingest exceeds ~500 rows/s.')
param skuName string = 'Standard_B2s'

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2023-12-01-preview' = {
  name: name
  location: location
  tags: tags
  sku: { name: skuName, tier: 'Burstable' }
  properties: {
    version: '16'
    administratorLogin: adminLogin
    administratorLoginPassword: adminPassword
    storage: { storageSizeGB: 64, autoGrow: 'Enabled' }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
    network: { publicNetworkAccess: 'Enabled' }
  }
}

// Allow Azure services (Container Apps consumption egress) to reach the server.
resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2023-12-01-preview' = {
  parent: server
  name: 'AllowAllAzureServicesAndResourcesWithinAzureIps'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

// Allow-list the TimescaleDB extension; CREATE EXTENSION is run by the API on first start.
resource extensions 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2023-12-01-preview' = {
  parent: server
  name: 'azure.extensions'
  properties: { value: 'TIMESCALEDB', source: 'user-override' }
  dependsOn: [ allowAzure ]
}

resource preload 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2023-12-01-preview' = {
  parent: server
  name: 'shared_preload_libraries'
  properties: { value: 'timescaledb', source: 'user-override' }
  dependsOn: [ extensions ]
}

resource db 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-12-01-preview' = {
  parent: server
  name: databaseName
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
  dependsOn: [ preload ]
}

output fqdn string = server.properties.fullyQualifiedDomainName
output serverName string = server.name
