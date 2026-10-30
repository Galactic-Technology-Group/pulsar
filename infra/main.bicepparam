using './main.bicep'

param env = 'dev'
param baseName = 'nms'
// Postgres Flexible provisioning is restricted in East US on this subscription and Burstable
// capacity was refused in East US 2 (SkuNotAvailable), so the database lives in Central US.
param postgresLocation = 'centralus'
param pgAdminLogin = 'nmsadmin'
// Supply at deploy time:  -p pgAdminPassword=<strong password>
param pgAdminPassword = readEnvironmentVariable('NMS_PG_PASSWORD', '')
param apiClientId = readEnvironmentVariable('NMS_API_CLIENT_ID', '00000000-0000-0000-0000-000000000000')
param portalClientId = readEnvironmentVariable('NMS_PORTAL_CLIENT_ID', '00000000-0000-0000-0000-000000000000')
param portalClientSecret = readEnvironmentVariable('NMS_PORTAL_CLIENT_SECRET', '')
param acsDataLocation = 'United States'
