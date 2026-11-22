# Frontier cAPI and Community Sync

All external integrations are opt-in and live under **Settings → Integrations**.

---

## Frontier Companion API (cAPI)

HUDnopy integrates with Frontier's official Companion API (`https://companion.orerve.net`) using **OAuth 2.0 + PKCE (`S256`)** and a static HTTPS callback relay (`docs/callback.html` on GitHub Pages) that forwards the temporary authorization code to HUDnopy's local loopback listener (`http://127.0.0.1:5055/callback`).

### Security & Token Storage
- No Frontier password is ever entered in HUDnopy — authentication happens in your default browser at `https://auth.frontierstore.net`.
- The temporary authorization code cannot be exchanged without the ephemeral PKCE `code_verifier` held only in HUDnopy's memory.
- Resulting `access_token` and `refresh_token` are encrypted with **Windows DPAPI** (`CryptProtectData`, `CurrentUser` scope) and stored in `%LocalAppData%\HUDnopy\frontier_capi.dat`.
- Tokens refresh automatically before expiration.

### Data Enriched by Frontier cAPI
- **`/profile`:** Live credits, **Stored Ships**, and **Stored Modules** (with engineering details) displayed in **Inventory** and injected into COVAS AI context.
- **`/fleetcarrier`:** Complete Fleet Carrier telemetry (name, callsign, location, fuel, space, finances, **Weekly Upkeep**, **Active Carrier Services**, **Market Orders**, and remote **Bartender** micro-resources) plus the **Fleet Carrier Cargo Hold** table in **Navigation → Fleet Carrier**, without needing to open the in-game Carrier Management menu.
- **`/communitygoals`:** Active **Community Goals**, location, global progress, personal contribution, and expiry (displayed in **Operations → Missions** and injected into COVAS AI context).
- **`/market`, `/shipyard` & `/journal`:** Enriches docked station market and shipyard snapshots when `Docked`, and supports on-demand Frontier `/journal` daily summaries via COVAS (`[[QUERY:CAPI_JOURNAL]]`).

### Sync Behavior
- Automatically syncs on `LoadGame` and `Docked` events with a **5-minute cooldown**.
- Handles Frontier maintenance (`HTTP 418`) and rate limiting (`HTTP 429`) gracefully.
- A manual **🔄 Sync cAPI** button is always available in Settings.

---

## Community Sync (EDDN, EDSM, Inara)

Each community service has an independent toggle and collapsible configuration panel:

### EDDN (Elite Dangerous Data Network)
- Uploads anonymized real-time telemetry (`SHA-256` hashed `uploaderID`) with official `gameversion` / `gamebuild` headers, recursive stripping of `_Localised` and personal keys (`Taxi`, `Multicrew`, `InSRV`, `OnFoot`, etc.), `Latitude`/`Longitude` preservation on `ApproachSettlement`, strict `StarPos` schema enforcement, and **SHA-256 snapshot deduplication** so repeated file reads never spam EDDN.
- Supports **14 official schemas**: `journal/1`, `fssdiscoveryscan/1`, `fssallbodiesfound/1`, `fssbodysignals/1`, `approachsettlement/1`, `codexentry/1`, `scanbarycentre/1`, `navbeaconscan/1`, `dockinggranted/1`, `commodity/3`, `outfitting/2`, `shipyard/2`, `fcmaterials_journal/1`, and `navroute/1`.
- Automatically routes Beta clients to `/test` and blocks Legacy galaxy uploads.

### EDSM (Elite Dangerous Star Map)
- Batches live Journal events to `POST https://www.edsm.net/api-journal-v1` on a non-overlapping `PeriodicTimer`, dynamically honoring `/api-journal-v1/discard` and retrying on network/5xx errors.
- Bidirectional system intelligence in **Navigation → Current System**: automatically fetches discovery status (with optional 6-language **First Discovery** voice announcement), system traffic, PvP deaths, factions, and personal system comments (`get-comment` / `set-comment`).
- **Automatic EDSM Shipyard Fallback:** When EDSM is enabled, works around the Elite Dangerous Odyssey `Shipyard.json` bug (where `PriceList` contains 0 or 1 ship) by fetching the full station shipyard catalog from EDSM (`api-system-v1/stations/shipyard`) when docked.
- Leaving the Commander Name blank automatically uses your live Journal commander name.

### Inara
- Sends structured commander updates to `POST https://inara.cz/inapi/v1/` on a **35-second batching `PeriodicTimer`** (respecting Inara's 2 requests/minute limit) with dirty tracking for materials and ship loadouts, Legacy blocking, and automatic 5xx retry.
- Automatically loads your **Inara Commander Profile** card (Desktop & Web HUD) and Community Goals fallback on startup when enabled.
- Note: Inara requires new third-party `appName` identifiers to be whitelisted by the Inara administrator (`inara.cz/elite/inara-api/`).

---

## Additional Opt-In Integrations (`v0.3.0`)

All new integrations are strictly opt-in (zero background network calls when disabled):

### Discord (Rich Presence & Webhooks)
- **Contextual Rich Presence:** Connects asynchronously to the local Discord client over Named Pipe IPC (`discord-ipc-*`) and displays live flight activity (Docked, Supercruise, Surface/SRV, On-Foot, Combat, Fleet Carrier) with session elapsed time.
- **Webhook Embeds:** Optional rich embeds for **Earth-Like World** scans, completed **3/3 Exobiology** analyses, **Fleet Carrier jumps** (with dynamic `<t:UNIX:R>` Discord countdowns and cancellation alerts), **EliteBGS conflict** alerts, plus on-demand **COVAS AI** sharing (`[[ACTION:DISCORD_SCREENSHOT]]`, `[[ACTION:DISCORD_CARRIER]]`, `[[ACTION:DISCORD_BUILD]]`).

### EliteBGS v5 Watchlist
- Polls up to 8 user-configured minor factions every 15 minutes (`PeriodicTimer`) and displays live system presence, influence %, and active states (Desktop & Web HUD), raising alerts on new wars/elections/retreats.

### Ardent Insight Market API
- Live commodity buy/sell market search with automatic fallback handling and integration with COVAS AI (`[[QUERY:ARDENT|...]]`).

### EDAstro DSSA & Canonn R&D
- **EDAstro DSSA:** Locates the nearest operational Deep Space Support Array Fleet Carriers sorted by exact 3D galactic distance from your live `StarPos`.
- **Canonn R&D:** Opt-in scientific telemetry upload for `CodexEntry`, `ScanOrganic`, `FSSSignalDiscovered`, and `SAASignalsFound` (Live galaxy only).
