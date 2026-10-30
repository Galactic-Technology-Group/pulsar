using './main.bicep'

param env = 'dev'
param baseName = 'nms'
// Postgres Flexible provisioning is restricted in East US on this subscription and Burstable
// capacity was refused in East US 2 (SkuNotAvailable), so the database lives in Central US.
param postgresLocation = 'centralus'
param pgAdminLogin = 'nmsadmin'
// Every value below MUST be present in the environment: with a default, a missing variable would silently
// redeploy with an empty password (overwriting pg-connection in Key Vault), zero client ids and a dummy
// portal secret. Without a default the deployment fails to compile instead. Source the env file first.
param pgAdminPassword = readEnvironmentVariable('NMS_PG_PASSWORD')
param apiClientId = readEnvironmentVariable('NMS_API_CLIENT_ID')
param portalClientId = readEnvironmentVariable('NMS_PORTAL_CLIENT_ID')
param portalClientSecret = readEnvironmentVariable('NMS_PORTAL_CLIENT_SECRET')
param acsDataLocation = 'United States'
