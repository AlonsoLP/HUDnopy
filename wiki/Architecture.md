# Architecture

HUDnopy is structured as a modular **.NET 10 WPF** desktop application hosting an embedded **ASP.NET Core Kestrel** server. All background services are composed in `Services/ApplicationServices.cs` and communicate through an in-process event hub (`Services/GameEventHub.cs`).

## High-Level Data Flow

```text
Elite Dangerous Files (Status.json, Journal.*.log, Cargo/Market/NavRoute/...)
        │
        ▼
Services/Telemetry/EliteFileWatcherService (partial classes)
   ├── Core        : Directory watchers, process detection, state publication, cAPI merge
   ├── Snapshots   : Status.json, Cargo, Market, Outfitting, Shipyard, NavRoute, Modules, Locker
   ├── Journal     : Streaming Journal parser & gameplay event handlers
   └── Exploration : FSS/DSS body history, exobiology tracking, unsold credit accounting
        │
        ├──► GameEventHub (JournalEventReceived / SnapshotFileUpdated)
        │       ├──► AlertService ──► AlertSoundService / AlertSpeechService (Piper / SAPI)
        │       ├──► CommunitySyncService (EDDN / EDSM / Inara)
        │       ├──► FrontierCapiService (Auto-sync on LoadGame / Docked)
        │       ├──► SpanshSystemDataService (System intelligence enrichment)
        │       └──► AiCopilotService (Dynamic immersion commentary)
        │
        └──► StateUpdated(ShipState snapshot)
                ├──► MainWindow (WPF Desktop UI & Mini-HUD)
                └──► WebServerService (HTTPS + WebSocket broadcast to Web/index.html)
```

## Directory Layout

| Directory | Responsibility |
|---|---|
| `Localization/` | Strongly-typed translation catalogs backed by embedded JSON resources with startup parity checks. |
| `Models/` | Immutable-friendly state snapshots and domain models split by gameplay area (`ShipState`, `ExplorationModels`, `OperationsModels`, `CombatMiningModels`, `FleetAndGoalsModels`, `CarrierState`, `AppConfig`). |
| `Resources/Languages/` | Complete JSON translation packs for `es-ES`, `en-GB`, `fr-FR`, `de-DE`, `ru-RU`, and `pt-BR`. |
| `Services/` | Independent domain services (AI, Alerts, Audio, Piper TTS, Bindings, Input Simulation, Frontier cAPI, Community Sync, Spansh, GalNet, Web Server). |
| `Services/Telemetry/` | Modular `partial class EliteFileWatcherService` implementation. |
| `UI/` | Modular `partial class MainWindow` files separated by feature area (`Telemetry`, `Operations`, `Spansh`, `AiCopilot`, `Integrations`, `Localization`, `SettingsAndAlerts`). |
| `UI/Controls/` | Reusable WPF controls and attached properties (`UiText`, `TableHeader`, `SectionNavigationPanel`). |
| `Web/` | Single-page responsive Web MFD (`index.html`) served over HTTPS. |
| `docs/` | Static GitHub Pages OAuth 2.0 callback relay (`callback.html`) for Frontier cAPI. |

## Concurrency & State Safety

- `EliteFileWatcherService` serializes state mutations behind an internal lock and publishes thread-safe snapshots (`ShipState.Snapshot()`) to subscribers so UI threads, WebSocket broadcasts, and AI prompt builders never race on mutable collections.
- External enrichment (such as Frontier cAPI `/profile`, `/fleetcarrier`, `/communitygoals`, `/market`, `/shipyard`) merges safely via `EliteFileWatcherService.ApplyCapiEnrichment(...)`.
- Background network operations (EDDN, EDSM, Inara, Spansh, Frontier cAPI, GalNet, AI providers) run asynchronously with dedicated HTTP clients, timeouts, and rate-limit cooldowns.

## Security Model

- **Zero plaintext OAuth tokens:** Frontier cAPI access and refresh tokens are encrypted with **Windows DPAPI** (`CryptProtectData`, `CurrentUser` scope) in `%LocalAppData%\HUDnopy\frontier_capi.dat`.
- **Self-signed HTTPS certificate:** The Web HUD certificate is stored DPAPI-encrypted in `web-certificate.dat` next to the executable and is never installed into the Windows root certificate store.
- **OAuth PKCE (`S256`):** Frontier cAPI uses Authorization Code Flow with PKCE and anti-CSRF `state` tokens; no client secret is stored or required.
- **No external telemetry by default:** EDDN, EDSM, Inara, Spansh, Frontier cAPI, AI Copilot, and the Web Server are all opt-in and independently configurable.
