# HUDnopy

[![CI Build & Tests](https://github.com/AlonsoLP/HUDnopy/actions/workflows/ci.yml/badge.svg)](https://github.com/AlonsoLP/HUDnopy/actions/workflows/ci.yml)
[![Version](https://img.shields.io/badge/Version-0.3.0-blue.svg)](#hudnopy)
[![.NET 10](https://img.shields.io/badge/.NET-10.0--windows-512BD4)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20WPF%20%2B%20Web%20HUD-0078D6)](#)
[![License: GPL-3.0-only](https://img.shields.io/badge/License-GPL--3.0--only-orange.svg)](LICENSE)
[![Languages](https://img.shields.io/badge/Languages-EN%20%7C%20ES%20%7C%20FR%20%7C%20DE%20%7C%20RU%20%7C%20PT-brightgreen)](#localization)

> [!NOTE]
> **HUDnopy `v0.3.0`** — Active open-source release with full 8-tab Desktop ↔ Web HUD parity, streaming COVAS AI Copilot with Cockpit Screen Vision, NAudio 3.3 WASAPI audio, 10 opt-in community integrations (cAPI, EDDN, EDSM, Inara, Spansh, EliteBGS, Ardent, EDAstro DSSA, Canonn, Discord), privacy-safe 1-click Support ZIP diagnostics, and **30 automated CI test suites**. Feedback and bug reports are always welcome.

**HUDnopy** is an open-source desktop telemetry HUD, responsive Web MFD, Fleet Carrier manager, Spansh route/trade assistant, and voice-enabled AI Copilot (COVAS) for **Elite Dangerous**.

Built with **.NET 10 / WPF** and an embedded **Kestrel HTTPS + WebSocket** server, HUDnopy runs locally on Windows alongside the game with zero cloud lock-in and full multi-device support for tablets and second monitors.

---

## Key Features

- **Live Telemetry, Landing Pad & Contextual Mini-HUD**
  - Real-time parsing of `Status.json`, Journal logs, `Cargo.json`, `Market.json`, `Outfitting.json`, `Shipyard.json` (with automatic EDSM Shipyard fallback), `NavRoute.json`, `ModulesInfo.json`, `ShipLocker.json`, `Backpack.json`, and `FCMaterials.json`.
  - **45-pad Starport Landing Pad Locator** (clock hour, bay depth diagram, and TTS guidance), live **Elite Server Status & Galaxy BGS Tick**, **1-click EDSY / Coriolis** ship loadout export, and a **contextual Mini-HUD** overlay.
- **Exploration, Exobiology & Surface Compass**
  - FSS/DSS body scanner table with estimated **Universal Cartographics** payouts, **Exobiology** genus predictions, 3-sample genetic diversity tracker, **live spherical Surface Compass** with 1-click *Last Exo Sample* targeting, and **Historical Expedition Analytics** with Markdown export.
- **Engineering Wishlist, Inventory & Fleet Carrier**
  - Curated ship & Odyssey **Engineering Wishlist** with live missing-material calculation, instant docked **Cargo sell-value & colonisation cross-reference**, and official multilingual in-game module names across all 6 languages.
  - Full **Fleet Carrier** dashboard (jump countdown with TTS warnings, financial runway, bartender market, cAPI cargo hold, and mass-aware **Tritium calculator**).
- **Combat, Mining, Missions & Spansh / GalNet**
  - Live target telemetry, combat session stats, massacre stack tracker, missions grouped by system with pending-cargo warnings, asteroid prospector, BGS/Powerplay, colonisation tracker, and historical Journal viewer.
  - Built-in **Spansh** plotters (Neutron, Road to Riches, Exomastery, Fleet Carrier, Trade, Pristine Mining Rings, Nearest Services, Colonisation Suppliers, and Li Yong-Rui Ship/Module search) plus multi-language **GalNet News**.
- **COVAS AI Copilot (Streaming, ReAct & Screen Vision) & NAudio 3.3 Audio**
  - Supports **Local LLMs (Ollama / LM Studio)**, **Google Gemini** (`gemini-flash-lite-latest` default), and **OpenAI / Groq / OpenRouter** with real-time **SSE sentence-by-sentence streaming**, **Multimodal Cockpit Screen Vision**, configurable **Thinking Mode** (`no`/`auto`/`yes`), instant **zero-LLM cockpit telemetry queries**, 2-step read-only `[[QUERY]]` analytics, fuzzy **zero-LLM Voice Macros**, **6-language zero-token tactical announcements**, Global Push-to-Talk hotkey, Wake Word, adaptive noise-floor + ZCR Hands-Free VAD with barge-in, persistent memory, and voice timers.
  - Built-in **Piper Neural TTS** running as a **persistent warm daemon** with parallel sentence pre-synthesis (~100–150 ms per sentence, 11 voices across 6 languages), powered by **NAudio 3.3** shared-mode `WasapiPlayer` (automatic device routing), `EffectChain` voice conditioning, and automatic microphone hot-swap reconnection.
- **Frontier cAPI, 10 Opt-In Community Integrations, Secure Web HUD & Diagnostics**
  - Official **Frontier Companion API (cAPI)** via OAuth 2.0 + PKCE and **Windows DPAPI** encrypted tokens, plus strictly opt-in integrations for **EDDN** (14 schemas + SHA-256 dedup), **EDSM**, **Inara**, **Spansh**, **EliteBGS v5**, **Ardent Insight**, **EDAstro DSSA**, **Canonn R&D**, and **Discord** (async Named Pipe Rich Presence + rich Webhook embeds).
  - Self-signed **HTTPS Web MFD** (7 tabs + **Split MFD** mode, Brotli/GZip pre-compressed assets, per-socket parallel WebSocket broadcasts) with ISO-standard QR-code pairing, session codes, VPN binding, and auto-context `.binds` Control Deck.
  - Built-in **Diagnostics & Support ZIP** (`Settings → Diagnostics → 📦 Export Support ZIP`): daily rolling logs, 40-entry breadcrumbs, Web HUD JS error capture, and automatic removal of all personal data (API keys, tokens, webhooks, commander names, notes, and Windows usernames).

---

## Requirements & Quick Start

### Prerequisites
- **Windows 10 / 11**
- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**
- **Elite Dangerous** (PC — Live/Odyssey/Horizons)

### Build & Run
```powershell
git clone https://github.com/AlonsoLP/HUDnopy.git
cd HUDnopy
dotnet run --project HUDnopy.csproj
```

For a release build and automated regression tests:
```powershell
dotnet build HUDnopy.csproj -c Release
dotnet run --project HUDnopy.Tests/HUDnopy.Tests.csproj -c Release
```

---

## Project Structure

```text
HUDnopy/
├── HUDnopy.Tests/         # Zero-dependency automated regression test suite (30 suites, CI + JUnit XML)
├── Localization/          # Strongly-typed FrozenDictionary translation catalogs and parity validators
├── Models/                # Domain models (ShipState, Exploration, Operations, ModuleDisplayCatalog, AppConfig)
├── Resources/Languages/   # 6-language JSON packs (en-GB default, es-ES, fr-FR, de-DE, ru-RU, pt-BR)
├── Services/
│   ├── AiCopilot/         # Modular AiCopilotService partials (Prompt, Queries, Providers)
│   ├── Integrations/      # Opt-in external integrations (Discord, EliteBGS, Ardent, EDAstro/Canonn)
│   ├── Telemetry/         # EliteFileWatcherService partials (Core, Snapshots, Journal, Exploration)
│   └── *.cs               # Alerts, Audio (NAudio 3.3), Piper TTS, Diagnostics, cAPI, Community Sync, Spansh, Web Server
├── UI/
│   ├── Controls/          # Reusable WPF controls and attached localization properties
│   └── MainWindow.*.cs    # Modular partial classes for MainWindow by functional domain
├── Web/                   # Modular responsive HTTPS Web HUD (index.html, styles.css, app.js)
├── docs/                  # Static GitHub Pages OAuth callback relay (callback.html)
└── wiki/                  # Full technical and user documentation (GitHub Wiki source)
```

---

## Localization

HUDnopy ships with **100% key- and placeholder-verified localization** across 6 languages (default: **English**):
- **English** (`en-GB`, default)
- **Spanish** (`es-ES`)
- **French** (`fr-FR`)
- **German** (`de-DE`)
- **Russian** (`ru-RU`)
- **Portuguese (Brazil)** (`pt-BR`)

---

## Documentation (Wiki)

Detailed setup guides and architectural documentation live in the [`wiki/`](wiki/Home.md) directory:

- **[Wiki Home](wiki/Home.md)** — Full documentation index
- **[Architecture](wiki/Architecture.md)** — Event bus, telemetry pipeline, threading, and security design
- **[Installation & Configuration](wiki/Installation-and-Configuration.md)** — Paths, alerts, custom sounds, themes, and Mini-HUD
- **[AI Copilot & Voice](wiki/AI-Copilot-and-Voice.md)** — Ollama/Gemini/OpenAI setup, Hands-Free VAD, and Piper Neural TTS
- **[Frontier cAPI & Community Sync](wiki/Frontier-cAPI-and-Community-Sync.md)** — OAuth 2.0 + PKCE, EDDN, EDSM, and Inara
- **[Web HUD & Security](wiki/Web-HUD-and-Security.md)** — HTTPS certificates, access modes, VPN binding, and QR pairing
- **[Spansh & Commander Tools](wiki/Spansh-and-Commander-Tools.md)** — Route plotters, trade, mining rings, and Fleet Carrier tools
- **[Keybindings & Control Deck](wiki/Keybindings-and-Control-Deck.md)** — `.binds` profiles, auto-context deck, and macros

---

## License & Disclaimer

HUDnopy is free software licensed under **GPL-3.0-only** — see [LICENSE](LICENSE) for full terms.

*Elite Dangerous* is a registered trademark of Frontier Developments plc. HUDnopy is an independent community project and is not affiliated with or endorsed by Frontier Developments.
