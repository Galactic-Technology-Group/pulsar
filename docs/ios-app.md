# Pulsar for iOS

A native SwiftUI app (iOS 17+, iPhone and iPad) in `ios/`. It signs in with Entra ID and reads the same REST API as the
web portal; there is no app-only backend.

## What it does (0.1.0)

| Screen | Shows |
|---|---|
| Sites | Every site, worst first: up/down/degraded check counts, agent online or offline, open incidents. Search by name. |
| Site | Open incidents, agents (status, version, last seen), the gateway check, devices sorted by state. Search by name or address. |
| Device | Address, MAC, VLAN, tags, and each check with its state and last response time. |
| Check | State and since when, response-time chart (6 h / 24 h / 7 d), and for SNMP checks the latest readings, grouped by interface or probe. Tap a reading for its history chart. |
| Incidents | Open incidents across all sites, or the recent history. Swipe to acknowledge. |
| Account | Who is signed in, their role, the server, sign out. |

Lists refresh every 30 seconds while on screen and on pull-to-refresh. Admins and operators can acknowledge incidents and
run a check now; viewers only read (the API enforces this, the app just hides the buttons). Adding and changing sites,
devices, credentials and alert rules stays in the web portal.

Not in this version: push notifications (needs an APNs key, a `Push` channel type in the worker and a device-token
registry in the API), widgets, Face ID lock, offline cache. See `docs/roadmap.md`.

## One-time setup in Entra ID

The app is a public client: no secret, sign-in with authorization code and PKCE in the system browser sheet.

1. Entra admin center → App registrations → **New registration**. Name `Pulsar iOS`, single tenant.
2. **Authentication** → Add a platform → **Mobile and desktop applications** → custom redirect URI
   `msauth.company.galactic.pulsar://auth`.
3. **API permissions** → Add a permission → My APIs → the Pulsar API → delegated `access_as_user` → Grant admin consent.
4. On the **Pulsar API** registration → Expose an API → Authorized client applications → add the new app's client ID for
   `access_as_user` (skips the consent prompt on the phone).

Users need the same app role as for the portal (`Nms.Admin`, `Nms.Operator` or `Nms.Viewer`, assigned on the Pulsar API
enterprise application). Nothing changes on the API or in Azure: bearer tokens from a native app are not subject to CORS.

## Build and run (needs a Mac with Xcode 16 or later)

1. Put your values in `ios/Config/Local.xcconfig` (ignored by git; it overrides `Pulsar.xcconfig`):

   ```
   PULSAR_API_BASE_URL = https:/$()/your-api-host
   PULSAR_TENANT_ID = <directory (tenant) ID>
   PULSAR_IOS_CLIENT_ID = <client ID of the Pulsar iOS registration>
   PULSAR_API_CLIENT_ID = <client ID of the Pulsar API registration>
   ```

   Write URLs as `https:/$()/host`: a plain `//` starts a comment in xcconfig files.
2. `brew install xcodegen`, then in `ios/`: `xcodegen generate` and `open Pulsar.xcodeproj`. The project file is
   generated from `project.yml` and is not committed; re-run `xcodegen generate` after adding or removing files.
3. Pick a simulator or your phone and Run. For a real device, choose your team under Signing & Capabilities. If you
   change the bundle identifier, change the redirect URI in Entra to `msauth.<bundle id>://auth` as well.

Distribution to other phones goes through TestFlight or an MDM and needs an Apple Developer Program membership.

## CI

`.github/workflows/ios.yml` builds the app for the simulator on a macOS runner whenever `ios/` changes. It does not
sign, test on a device or upload.

## Layout

| Path | Contents |
|---|---|
| `ios/project.yml` | XcodeGen spec: target, Info.plist keys, URL scheme |
| `ios/Config/Pulsar.xcconfig` | API address and Entra IDs (placeholders in git) |
| `ios/Pulsar/Auth` | Entra sign-in (PKCE), token refresh, refresh token in the keychain |
| `ios/Pulsar/API` | API client and the JSON shapes it reads |
| `ios/Pulsar/Views` | SwiftUI screens |
