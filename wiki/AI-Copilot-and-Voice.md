# AI Copilot and Voice (COVAS & Piper TTS)

HUDnopy includes a built-in conversational AI Copilot (**COVAS**) with live ship telemetry awareness, optional safe game actions, voice input (Push-to-Talk and Hands-Free VAD), and local neural speech synthesis via **Piper TTS**.

## AI Provider Setup

Open **Settings → Connectivity & Integrations → AI Copilot (COVAS)** and enable the master toggle. Exactly **one** provider is active at a time:

1. **Local (Ollama / LM Studio):**
   - Point **Endpoint** to your local OpenAI-compatible server (default: `http://localhost:11434/v1`).
   - Click **🔄** to query `/v1/models` and pick an installed model.
   - Voice input uses Windows built-in speech recognition (`System.Speech.Recognition`).
2. **Cloud: Google Gemini API:**
   - Paste your Gemini API key and click **🔄** to list available `generateContent` models.
   - Voice input sends captured 16 kHz mono WAV audio directly to Gemini's multimodal endpoint.
3. **Cloud: OpenAI / Groq / OpenRouter:**
   - Configure the base endpoint URL, API key, and model via **🔄**.
   - Voice input transcribes captured audio automatically via `/audio/transcriptions` (Whisper).

## Telemetry Context & Safe Actions

Every COVAS prompt automatically includes a structured snapshot of:
- Ship state, fuel, hull, shields, cargo, credits, unsold cartography/exobiology, and current system/station.
- Active **Fleet Carrier** state (location, fuel, scheduled jump, space, finances) clearly separated from your active ship.
- Stored ship fleet and active **Community Goals** (when Frontier cAPI is connected).
- Latest downloaded **GalNet** headlines (fetched on demand if you ask about GalNet).

When **Allow safe actions** is enabled, COVAS can trigger whitelisted HUD actions (`[[ACTION:BIND|...]]`, `[[ACTION:SPANSH|...]]`, `[[ACTION:NOTE|...]]`).

## Voice Input Modes

In **Activity → AI Copilot**:
- **🎤 Speak (Push-to-Talk):** Click once to record 16 kHz mono audio via NAudio, click again to send.
- **🎙 Hands-Free (VAD):** Continuous Voice Activity Detection with a 600 ms pre-buffer, RMS speech threshold, automatic submission after 1.3 s of silence, and automatic microphone pause while COVAS is speaking (`AlertSpeechService.IsSpeaking`) to prevent echo loops.
- **Dynamic Event Commentary:** Optionally let COVAS automatically comment on hazardous jumps (`StartJump`), high-value planets (`Scan`), wanted high-bounty targets (`ShipTargeted`), completed exobiology samples (`ScanOrganic`), attacks (`UnderAttack`), and cockpit breaches (`CockpitBreached`) with a configurable cooldown.
- **Web Chat:** Available in the Web HUD (`POST /api/ai/ask`) with browser Web Speech API dictation.

## Piper Neural TTS & Windows SAPI

Open **Settings → Alerts → Local Voice**:
- **Windows SAPI:** Uses installed Windows speech voices immediately.
- **Piper Neural TTS:** Free, offline neural speech synthesis using open-source ONNX models (`rhasspy/piper-voices`).
  - Selecting Piper automatically downloads the portable Windows engine to `%LocalAppData%\HUDnopy\Piper\` (or reuses an existing valid installation with `espeak-ng-data` and `onnxruntime.dll`).
  - Choose from **11 curated voices** across all 6 languages (or **Automatic by UI language**), download individual voices or the complete 6-language pack, and test directly with **▶**.
  - Multi-codepoint phoneme map entries in `.onnx.json` configs are automatically sanitized for Windows `piper.exe` compatibility.
  - Speech speed (`-5` to `+5`) uses a unified exponential curve (`Math.Pow(0.87, rate)`) matching Windows SAPI's perceived range, and responses are normalized for natural spoken numbers and units.
