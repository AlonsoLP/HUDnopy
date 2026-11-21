# HUDnopy Wiki

> **Current Version:** `v0.2.0` *(Open-source release with 8-tab Desktop ↔ Web HUD parity, streaming COVAS AI Copilot, warm Piper Neural TTS, and 15 automated CI test suites).*

Welcome to the official documentation for **HUDnopy** — an open-source Elite Dangerous telemetry HUD, Web MFD, Fleet Carrier manager, Spansh toolkit, and voice-enabled AI Copilot.

## Documentation Index

1. **[Architecture](Architecture.md)**
   - Codebase layout, telemetry ingestion pipeline, event bus, thread safety, and security architecture.
2. **[Installation and Configuration](Installation-and-Configuration.md)**
   - Building from source, game folder paths, themes, Mini-HUD overlay, alert thresholds, and custom sound libraries.
3. **[AI Copilot and Voice (COVAS & Piper TTS)](AI-Copilot-and-Voice.md)**
   - Configuring Ollama/LM Studio, Google Gemini, or OpenAI-compatible providers; Push-to-Talk; Hands-Free VAD; dynamic event commentary; and Piper Neural TTS.
4. **[Frontier cAPI and Community Sync](Frontier-cAPI-and-Community-Sync.md)**
   - Connecting Frontier Companion API (OAuth 2.0 + PKCE + DPAPI) and configuring EDDN, EDSM, and Inara.
5. **[Web HUD and Security](Web-HUD-and-Security.md)**
   - Embedded Kestrel HTTPS/WebSocket server, access modes, session codes, VPN binding, certificate fingerprint verification, and QR pairing.
6. **[Spansh and Commander Tools](Spansh-and-Commander-Tools.md)**
   - Route plotting (Neutron, Road to Riches, Exomastery, Fleet Carrier), CSV import with next-jump clipboard copy, trade routes, pristine mining rings, services, and tritium calculator.
7. **[Keybindings and Control Deck](Keybindings-and-Control-Deck.md)**
   - `.binds` profile parsing, automatic Ship/SRV/On-Foot/DSS/SLF context switching, and multi-step macros.
