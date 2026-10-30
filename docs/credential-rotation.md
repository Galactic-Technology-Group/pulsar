# Credential rotation register

Running list of credentials for the Pulsar dev environment, why each one needs rotating, and how.
**Never put a secret value in this file.** Add a row whenever a secret is exposed (pasted in chat, logged,
committed, shared over email) or when a routine rotation falls due; move it to *Rotated* when done.

## Pending rotation

| # | Credential | Where it lives | Why it needs rotating | How to rotate | Added |
|---|---|---|---|---|---|
| 1 | Pulsar-Portal client secret (Entra app `3ffd96e1-…`) | Key Vault `kv-nms-dev-2ytr5j` → `portal-client-secret`; local `pulsar-dev.env` (`NMS_PORTAL_CLIENT_SECRET`) | Pasted into the Claude Code chat on 2026-10-29 (transcript on disk) | Entra ID → App registrations → Pulsar-Portal → Certificates & secrets → New client secret; update `pulsar-dev.env`; redeploy `infra/main.bicep` (writes Key Vault and restarts the portal); then delete the old secret in Entra | 2026-10-29 |
| 2 | Postgres admin password (`nmsadmin` on `pg-nms-dev-6ms735`) | Key Vault `pg-connection` (inside the connection string); local `pulsar-dev.env` (`NMS_PG_PASSWORD`) | Pasted into the Claude Code chat on 2026-10-30 after the recovery reset | Azure portal → the flexible server → Reset password (or `az postgres flexible-server update --admin-password`); update `pulsar-dev.env`; redeploy; `az containerapp revision restart` the worker so it drops pooled connections | 2026-10-30 |
| 3 | Agent `pulsaragent` enrolment token | Hash only, in `agents.enrollment_token_hash` | Was shown in the portal and consumed; single-use and 24 h, no action needed unless re-issued and left unused | Portal → site → agent → Re-issue token; unused tokens expire on their own | 2026-10-30 |

## Routine schedule

| Credential | Interval | Notes |
|---|---|---|
| Pulsar-Portal client secret | 12 months (Entra expiry) | Set a calendar reminder a month before expiry; Entra shows the end date |
| Postgres admin password | 12 months, or on any exposure | Apps read it from Key Vault at revision start, so a redeploy is the rotation |
| Per-agent secrets (`agents.secret_hash`) | On host compromise or decommission only | Portal → Revoke; the agent clears its credentials and exits. Re-enrol with a new token |
| ACS email connection string | Rotate from the ACS resource → Keys; redeploy writes `acs-connection` | Key 1 / Key 2 allow a zero-downtime swap |
| Container Apps managed identity | n/a | No secret to rotate; Key Vault access is RBAC |

## Rotated

| Credential | Rotated on | By | Notes |
|---|---|---|---|
| Postgres admin password | 2026-10-30 | user | Recovery reset after a blind redeploy overwrote `pg-connection`; the new value was then pasted in chat, hence row 2 above |

## Rules

- Secret values live in Key Vault and in the operator's local `pulsar-dev.env` (and a password manager), nowhere else.
- When a value is pasted into a chat, ticket or email, add it to *Pending rotation* the same day.
- Redeploys must source `pulsar-dev.env`; `infra/main.bicepparam` refuses to compile without the four variables.
