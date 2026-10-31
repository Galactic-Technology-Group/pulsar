using './main.bicep'

// Production parameters. Deploy into a separate resource group with its own Entra app registrations:
//   set -a; . pulsar-prod.env; set +a
//   az deployment group create -g rg-pulsar-prod -f infra/main.bicep -p infra/main.prod.bicepparam
// Secret parameters have no defaults on purpose (see README: "Redeploying: the secret parameters are mandatory").
param env = 'prod'
param baseName = 'nms'
param postgresLocation = 'centralus'
param pgAdminLogin = 'nmsadmin'
param pgSkuName = 'Standard_D2ds_v4'
param pgTier = 'GeneralPurpose'
param pgBackupRetentionDays = 35
param pgGeoRedundantBackup = true
// Flip to 'ZoneRedundant' once a standby is worth its cost (roughly doubles the Postgres compute line).
param pgHighAvailability = 'Disabled'
param pgAdminPassword = readEnvironmentVariable('NMS_PG_PASSWORD')
param apiClientId = readEnvironmentVariable('NMS_API_CLIENT_ID')
param portalClientId = readEnvironmentVariable('NMS_PORTAL_CLIENT_ID')
param portalClientSecret = readEnvironmentVariable('NMS_PORTAL_CLIENT_SECRET')
param acsDataLocation = 'United States'
