# Infrastructure

One resource group holding everything the central service needs:

| Resource | Purpose |
|---|---|
| Log Analytics + Application Insights | Logs and telemetry for all three apps |
| User-assigned managed identity | Shared by the apps: `AcrPull` on the registry, `Key Vault Secrets User` on the vault |
| Azure Container Registry (Basic) | Images for `nms-api`, `nms-portal`, `nms-worker` |
| Key Vault (RBAC) | `pg-connection`, `acs-connection`, `portal-client-secret` |
| PostgreSQL Flexible Server 16 (B2s) | `nms` database, `TIMESCALEDB` allow-listed and preloaded |
| Communication Services + Email (Azure-managed domain) | Outage emails |
| Container Apps environment | `ca-<name>-api` (external, WebSockets), `ca-<name>-portal` (external, sticky sessions), `ca-<name>-worker` (no ingress, 1 replica) |

Phase 2 will add an Azure Relay namespace with one Hybrid Connection per site.

## Deploy

```bash
az login
az group create -n rg-nms-dev -l eastus

export NMS_PG_PASSWORD='<strong password>'
export NMS_API_CLIENT_ID='<api app registration client id>'
export NMS_PORTAL_CLIENT_ID='<portal app registration client id>'
export NMS_PORTAL_CLIENT_SECRET='<portal client secret>'

az deployment group create -g rg-nms-dev -f infra/main.bicep -p infra/main.bicepparam
```

The first deploy uses a placeholder image so the apps come up before yours exist. Then build, push and point the apps at your images:

```bash
ACR=$(az deployment group show -g rg-nms-dev -n main --query properties.outputs.acrLoginServer.value -o tsv)
az acr login -n ${ACR%%.*}
for app in Nms.Api Nms.Portal Nms.Worker; do
  docker build -f src/$app/Dockerfile -t $ACR/$app:latest .
  docker push $ACR/$app:latest
done
az deployment group create -g rg-nms-dev -f infra/main.bicep -p infra/main.bicepparam \
  -p apiImage=$ACR/Nms.Api:latest -p portalImage=$ACR/Nms.Portal:latest -p workerImage=$ACR/Nms.Worker:latest
```

If the deployment fails with `The value of the 'Version' should be in: []`, Postgres Flexible Server provisioning is restricted in that region for your subscription. Check with the capabilities API and point `postgresLocation` at the paired region:

```bash
az rest --method get --url "https://management.azure.com/subscriptions/<sub>/providers/Microsoft.DBforPostgreSQL/locations/eastus2/capabilities?api-version=2024-08-01" --query "value[0].{reason:reason,versions:supportedServerVersions[].name}"
```

## TimescaleDB

The template allow-lists the extension (`azure.extensions = TIMESCALEDB`) and preloads it. Azure ships the **Apache-licensed** TimescaleDB build: hypertables and `time_bucket` work, but compression, retention policies and continuous aggregates are Community (TSL) features and are skipped with a warning on first start. The worker's `RetentionJob` prunes raw rows instead, and the API reads raw results when the 5-minute aggregate is missing. The API runs `CREATE EXTENSION IF NOT EXISTS timescaledb` and creates the hypertable, compression, retention and the 5-minute continuous aggregate on first start (`src/Nms.Data/TimescaleSql.cs`). To do it by hand:

```bash
psql "host=<fqdn> dbname=nms user=nmsadmin sslmode=require" -c "CREATE EXTENSION IF NOT EXISTS timescaledb;"
```

If `shared_preload_libraries` did not apply (it needs a server restart), run `az postgres flexible-server restart -g rg-nms-dev -n <server>`.

## Entra ID

Create two app registrations before deploying (the ids are parameters):

1. **nms-api**: Expose an API → scope `access_as_user`. App roles `Nms.Admin`, `Nms.Operator`, `Nms.Viewer` (allowed member type Users/Groups). Assign users or groups to roles under Enterprise applications.
2. **nms-portal**: Web platform, redirect URI `https://<portal fqdn>/signin-oidc` (fill in after the first deploy prints `portalUrl`), client secret, API permission `nms-api / access_as_user` with admin consent.

## Validate without deploying

```bash
az bicep build --file infra/main.bicep
az deployment group what-if -g rg-nms-dev -f infra/main.bicep -p infra/main.bicepparam
```

## Cost notes (dev)

B2s Postgres and a Basic registry are the only always-on charges of note; Container Apps consumption with min replicas 1 for three small apps stays low. Log Analytics retention is 30 days. Use `-p env=prod` with a larger `skuName` on the Postgres module when the fleet grows.
