// Pulsar (Centralized NMS) - Azure environment (resource group scope).
// Deploy: az deployment group create -g <rg> -f infra/main.bicep -p infra/main.bicepparam
targetScope = 'resourceGroup'

@description('Short environment name used in resource names, e.g. dev, prod.')
@minLength(2)
@maxLength(8)
param env string = 'dev'

@description('Azure region. Container Apps, Postgres Flexible and ACS Email must all be available there.')
param location string = resourceGroup().location

@description('Region for PostgreSQL. Defaults to location; override when Flexible Server provisioning is restricted there (use the paired region).')
param postgresLocation string = location

@description('Base name for resources.')
@minLength(3)
@maxLength(12)
param baseName string = 'nms'

@description('PostgreSQL administrator login.')
param pgAdminLogin string = 'nmsadmin'

@secure()
@description('PostgreSQL administrator password.')
param pgAdminPassword string

@description('Container image for the API. Defaults to a placeholder so the first deploy works before your images exist.')
param apiImage string = 'mcr.microsoft.com/k8se/quickstart:latest'
param portalImage string = 'mcr.microsoft.com/k8se/quickstart:latest'
param workerImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Entra ID tenant id for portal/API sign-in.')
param tenantId string = subscription().tenantId
@description('App registration (client) id of the API.')
param apiClientId string = ''
@description('App registration (client) id of the portal.')
param portalClientId string = ''
@secure()
@description('Client secret of the portal app registration.')
param portalClientSecret string = ''

@description('Data location for Azure Communication Services (e.g. United States, Europe).')
param acsDataLocation string = 'United States'

var suffix = toLower(uniqueString(resourceGroup().id))
var name = '${baseName}-${env}'
var tags = { app: 'pulsar', env: env }

// ---------------------------------------------------------------------------------------------
// Observability
// ---------------------------------------------------------------------------------------------
module logging 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: { name: name, location: location, tags: tags }
}

// ---------------------------------------------------------------------------------------------
// Identity, registry, secrets
// ---------------------------------------------------------------------------------------------
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}'
  location: location
  tags: tags
}

module acr 'modules/acr.bicep' = {
  name: 'acr'
  params: {
    name: 'acr${baseName}${env}${suffix}'
    location: location
    tags: tags
    pullPrincipalId: identity.properties.principalId
  }
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    name: 'kv-${name}-${take(suffix, 6)}'
    location: location
    tags: tags
    readerPrincipalId: identity.properties.principalId
  }
}

// ---------------------------------------------------------------------------------------------
// Data
// ---------------------------------------------------------------------------------------------
module postgres 'modules/postgres.bicep' = {
  name: 'postgres'
  params: {
    name: 'pg-${name}-${take(suffix, 6)}'
    location: postgresLocation
    tags: tags
    adminLogin: pgAdminLogin
    adminPassword: pgAdminPassword
    databaseName: 'nms'
  }
}

// ---------------------------------------------------------------------------------------------
// Email
// ---------------------------------------------------------------------------------------------
module acs 'modules/communication.bicep' = {
  name: 'communication'
  params: { name: name, tags: tags, dataLocation: acsDataLocation }
}

// Secrets consumed by the apps. Values flow through Key Vault only.
module secrets 'modules/secrets.bicep' = {
  name: 'secrets'
  params: {
    keyVaultName: keyVault.outputs.name
    secrets: {
      'pg-connection': 'Host=${postgres.outputs.fqdn};Port=5432;Database=nms;Username=${pgAdminLogin};Password=${pgAdminPassword};SSL Mode=Require;Trust Server Certificate=true'
      'acs-connection': acs.outputs.connectionString
      'portal-client-secret': empty(portalClientSecret) ? 'replace-me' : portalClientSecret
    }
  }
}

// ---------------------------------------------------------------------------------------------
// Container Apps
// ---------------------------------------------------------------------------------------------
resource cae 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${name}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logging.outputs.workspaceCustomerId
        sharedKey: logging.outputs.workspaceSharedKey
      }
    }
    workloadProfiles: [ { name: 'Consumption', workloadProfileType: 'Consumption' } ]
  }
}

var kvUri = keyVault.outputs.uri
var commonEnv = [
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: logging.outputs.appInsightsConnectionString }
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
]

module api 'modules/containerapp.bicep' = {
  name: 'ca-api'
  dependsOn: [ secrets ]
  params: {
    name: 'ca-${name}-api'
    location: location
    tags: tags
    environmentId: cae.id
    identityId: identity.id
    image: apiImage
    registryServer: acr.outputs.loginServer
    external: true
    targetPort: 8080
    minReplicas: 1
    maxReplicas: 3
    secrets: [
      { name: 'pg-connection', keyVaultUrl: '${kvUri}secrets/pg-connection', identity: identity.id }
    ]
    env: concat(commonEnv, [
      { name: 'ConnectionStrings__Nms', secretRef: 'pg-connection' }
      { name: 'Auth__Mode', value: 'EntraId' }
      { name: 'AzureAd__TenantId', value: tenantId }
      { name: 'AzureAd__ClientId', value: apiClientId }
      { name: 'AzureAd__Audience', value: 'api://${apiClientId}' }
      { name: 'Cors__Origins__0', value: 'https://ca-${name}-portal.${cae.properties.defaultDomain}' }
    ])
  }
}

module portal 'modules/containerapp.bicep' = {
  name: 'ca-portal'
  dependsOn: [ secrets ]
  params: {
    name: 'ca-${name}-portal'
    location: location
    tags: tags
    environmentId: cae.id
    identityId: identity.id
    image: portalImage
    registryServer: acr.outputs.loginServer
    external: true
    targetPort: 8080
    minReplicas: 1
    maxReplicas: 2
    // Blazor Server circuits are stateful: keep a client on one replica.
    stickySessions: true
    secrets: [
      { name: 'portal-client-secret', keyVaultUrl: '${kvUri}secrets/portal-client-secret', identity: identity.id }
    ]
    env: concat(commonEnv, [
      { name: 'Auth__Mode', value: 'EntraId' }
      { name: 'AzureAd__TenantId', value: tenantId }
      { name: 'AzureAd__ClientId', value: portalClientId }
      { name: 'AzureAd__ClientSecret', secretRef: 'portal-client-secret' }
      { name: 'Api__BaseUrl', value: 'https://${api.outputs.fqdn}' }
      { name: 'Api__Scope', value: 'api://${apiClientId}/access_as_user' }
    ])
  }
}

module worker 'modules/containerapp.bicep' = {
  name: 'ca-worker'
  dependsOn: [ secrets ]
  params: {
    name: 'ca-${name}-worker'
    location: location
    tags: tags
    environmentId: cae.id
    identityId: identity.id
    image: workerImage
    registryServer: acr.outputs.loginServer
    external: false
    ingressEnabled: false
    minReplicas: 1
    maxReplicas: 1
    secrets: [
      { name: 'pg-connection', keyVaultUrl: '${kvUri}secrets/pg-connection', identity: identity.id }
      { name: 'acs-connection', keyVaultUrl: '${kvUri}secrets/acs-connection', identity: identity.id }
    ]
    env: concat(commonEnv, [
      { name: 'ConnectionStrings__Nms', secretRef: 'pg-connection' }
      { name: 'Email__ConnectionString', secretRef: 'acs-connection' }
      { name: 'Email__From', value: 'DoNotReply@${acs.outputs.mailFromDomain}' }
      { name: 'Worker__PortalBaseUrl', value: 'https://${portal.outputs.fqdn}' }
    ])
  }
}

output apiUrl string = 'https://${api.outputs.fqdn}'
output portalUrl string = 'https://${portal.outputs.fqdn}'
output acrLoginServer string = acr.outputs.loginServer
output keyVaultName string = keyVault.outputs.name
output postgresFqdn string = postgres.outputs.fqdn
output identityClientId string = identity.properties.clientId
