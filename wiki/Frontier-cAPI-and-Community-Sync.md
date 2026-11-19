# Frontier cAPI and Community Sync

All external integrations are opt-in and live under **Settings → Connectivity & Integrations**.

---

## Frontier Companion API (cAPI)

HUDnopy integrates with Frontier's official Companion API (`https://companion.orerve.net`) using **OAuth 2.0 + PKCE (`S256`)** and a static HTTPS callback relay (`docs/callback.html` on GitHub Pages) that forwards the temporary authorization code to HUDnopy's local loopback listener (`http://127.0.0.1:5055/callback`).

### Security & Token Storage
- No Frontier password is ever entered in HUDnopy — authentication happens in your default browser at `https://auth.frontierstore.net`.
- The temporary authorization code cannot be exchanged without the ephemeral PKCE `code_verifier` held only in HUDnopy's memory.
- Resulting `access_token` and `refresh_token` are encrypted with **Windows DPAPI** (`CryptProtectData`, `CurrentUser` scope) and stored in `%LocalAppData%\HUDnopy\frontier_capi.dat`.
- Tokens refresh automatically before expiration.

### Data Enriched by Frontier cAPI
- **`/profile`:** Live credits and your full **Stored Ships** fleet (displayed in **Operations → Shipyard** and injected into COVAS AI context).
- **`/fleetcarrier`:** Complete Fleet Carrier telemetry (name, callsign, location, fuel, space, finances) plus the **Fleet Carrier Cargo Hold** table in **Navigation → Fleet Carrier**, without needing to open the in-game Carrier Management menu.
- **`/communitygoals`:** Active **Community Goals**, location, global progress, personal contribution, and expiry (displayed in **Activity → Milestones** and injected into COVAS AI context).
- **`/market` & `/shipyard`:** Enriches docked station market and shipyard snapshots when `Docked`.

### Sync Behavior
- Automatically syncs on `LoadGame` and `Docked` events with a **5-minute cooldown**.
- Handles Frontier maintenance (`HTTP 418`) and rate limiting (`HTTP 429`) gracefully.
- A manual **🔄 Sync cAPI** button is always available in Settings.

---

## Community Sync (EDDN, EDSM, Inara)

Each community service has an independent toggle and collapsible configuration panel:

### EDDN (Elite Dangerous Data Network)
- Uploads anonymized real-time telemetry (`SHA-256` hashed `uploaderID`) with official `gameversion` / `gamebuild` headers and `_Localised` / private key stripping.
- Supports **14 official schemas**: `journal/1`, `fssdiscoveryscan/1`, `fssallbodiesfound/1`, `fssbodysignals/1`, `approachsettlement/1`, `codexentry/1`, `scanbarycentre/1`, `navbeaconscan/1`, `dockinggranted/1`, `commodity/3`, `outfitting/2`, `shipyard/2`, `fcmaterials_journal/1`, and `navroute/1`.
- Automatically routes Beta clients to `/test` and blocks Legacy galaxy uploads.

### EDSM (Elite Dangerous Star Map)
- Batches live Journal events to `POST https://www.edsm.net/api-journal-v1`, dynamically honoring `/api-journal-v1/discard` and retrying on network/5xx errors.
- Bidirectional system intelligence in **Navigation → Current System**: fetches system traffic, PvP deaths, and personal system comments (`get-comment` / `set-comment`).
- Leaving the Commander Name blank automatically uses your live Journal commander name.

### Inara
- Sends structured commander updates to `POST https://inara.cz/inapi/v1/` on a **35-second batching timer** (respecting Inara's 2 requests/minute limit) with dirty tracking for materials and ship loadouts, Legacy blocking, and automatic 5xx retry.
- Note: Inara requires new third-party `appName` identifiers to be whitelisted by the Inara administrator (`inara.cz/elite/inara-api/`).
