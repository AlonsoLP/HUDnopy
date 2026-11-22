# AI Copilot and Voice (COVAS & Piper TTS)

HUDnopy includes a built-in conversational AI Copilot (**COVAS**) with live ship telemetry awareness, optional safe game actions, voice input (Push-to-Talk and Hands-Free VAD), and local neural speech synthesis via **Piper TTS**.

## AI Provider Setup

Open **Settings → AI Copilot** and enable the master toggle. Exactly **one** provider is active at a time:

1. **Local (Ollama / LM Studio):**
   - Point **Endpoint** to your local OpenAI-compatible server (default: `http://localhost:11434/v1`).
   - Click **🔄** to query `/v1/models` and pick an installed model.
   - Voice input uses Windows built-in speech recognition (`System.Speech.Recognition`).
2. **Cloud: Google Gemini API:**
   - Default model: `gemini-flash-lite-latest`. Paste your Gemini API key and click **🔄** to list available `generateContent` models.
   - Supports configurable **Thinking Mode** (`no`, `auto`, `yes`) with a subtle `COVAS ⚡` / `COVAS 🧠` indicator in `auto` mode.
   - Voice input sends captured 16 kHz mono WAV audio directly to Gemini's multimodal streaming endpoint.
3. **Cloud: OpenAI / Groq / OpenRouter:**
   - Configure the base endpoint URL, API key, and model via **🔄**.
   - Voice input transcribes captured audio automatically via `/audio/transcriptions` (Whisper).

## Streaming, ReAct Queries, Screen Vision, Voice Macros & Safe Actions

- **Real-Time SSE Streaming & 429/503 Retry:** Gemini, OpenAI, Groq, and Ollama stream responses sentence-by-sentence directly into chat and TTS so COVAS starts speaking on Sentence 1, with automatic backoff retry on transient HTTP `429`/`503` responses.
- **Multimodal Cockpit Screen Vision:** Click **👁 Vision** (Desktop & Web HUD `POST /api/ai/vision`) or ask by voice (*"look at my screen"*) to capture a JPEG cockpit screenshot and analyze it with your multimodal LLM.
- **Instant Zero-LLM Cockpit Queries & Voice Macros:** Common telemetry questions (fuel, hull, system, credits, landing pad, cargo, jump range) answer in **0 ms without calling an LLM**. Custom voice macros use filler-word stripping and **Levenshtein fuzzy matching**, plus 6-language **Silence** and **Repeat** voice commands and a hands-free safety guard blocking destructive bindings (`EjectAllCargo`, `SilentRunning`).
- **2-Step Read-Only Queries (`[[QUERY:...]]`) with 60 s Cache:** COVAS can query live local telemetry (`MATERIAL`, `EXPLORATION`, `MISSIONS`, `REPORT`, `PROFILE`, `FLEET`, `STATION`, `SYNTHESIS`, `ODYSSEY_GEAR`, `EXOBIO_VALUE`, `RECENT_EVENTS`), historical logs (`LOGBOOK`), Frontier cAPI (`CAPI_JOURNAL`), EDSM (`SYSTEM`, `EDSM_STATIONS`, `EDSM_NEARBY`), Ardent (`ARDENT`), and Spansh (`SPANSH`, `SPANSH_BODIES`, `SPANSH_CARRIERS`).
- **6-Language Zero-Token Tactical Announcements:** Optional instant spoken alerts for High-G approaches (`>= 1.5G`), valuable FSS signals (HGE, Thargoid, Guardian, Megaship), mining prospector hits, player/pirate radio chatter, BioInsights genetic-sample distance reached, `ScoopingFuel` tank full, `Undocked` missing limpets, `Interdicted`, `Docked` actionable summary, `FSSAllBodiesFound`, `SAASignalsFound` bio counts, express system arrival briefings, and voice timers (`[[ACTION:TIMER|...]]`).
- **Safe Actions & Persistent Memory:** When **Allow safe actions** is enabled, COVAS can execute whitelisted `.binds` actions, `PIP_PRESET`, `MINING_FILTER`, `SURFACE_TARGET`, `WISHLIST_CLEAR`, `RESET_SESSION`, Spansh searches/routes, EDSY/Coriolis exports, GalNet read-aloud, clipboard copies, surface bookmarks, Discord sharing (`DISCORD_SCREENSHOT`, `DISCORD_CARRIER`, `DISCORD_BUILD`), and persistent commander memory (`REMEMBER` / `FORGET`).

## Voice Input Modes & NAudio 3.3 Conditioning

In **Activity → AI Copilot**:
- **🎤 Speak (Push-to-Talk) & Global Hotkey:** Click to record 16 kHz mono audio via **NAudio 3.3**, or use the configurable **Global PTT Hotkey** (default `Ctrl+Alt+V`) directly from fullscreen/VR without `Alt+Tab`.
- **🎙 Hands-Free (VAD) & Wake Word:** Adaptive EMA noise-floor + Zero-Crossing Rate (ZCR) Voice Activity Detection with `600 ms` endpointing, sustained-voice barge-in, 18-second hands-free follow-up window, **NAudio 3.3 `EffectChain`** (`GateEffect` + `CompressorEffect`) voice conditioning before Whisper/Gemini transcription, and **automatic microphone hot-swap reconnection** via `MMDeviceEnumerator.CreateNotificationClient()`.
- **Web Chat:** Available in the Web HUD (`POST /api/ai/ask` and `POST /api/ai/vision`) with browser Web Speech API dictation, Pre-Flight Check, and Markdown chat export.

## Piper Neural TTS, NAudio 3.3 WASAPI & Windows SAPI

Open **Settings → Voice & Audio**:
- **NAudio 3.3 `WasapiPlayer`:** Alerts, Piper TTS, and SAPI audio play through shared-mode `WasapiPlayer` (`WasapiPlayerBuilder`) with automatic Windows default-device routing (seamlessly following headphone/speaker switches), `IAudioClient3` low latency, and MMCSS audio thread priority (with automatic `WaveOut` fallback).
- **Windows SAPI:** Uses installed Windows speech voices immediately.
- **Piper Neural TTS:** Free, offline neural speech synthesis using open-source ONNX models (`rhasspy/piper-voices`) running as a **persistent warm `piper.exe --json-input` daemon** with **parallel pre-synthesis of the next queued sentence** (~100–150 ms per sentence).
  - Selecting Piper automatically downloads the portable Windows engine to `%LocalAppData%\HUDnopy\Piper\` (or reuses an existing valid installation with `espeak-ng-data` and `onnxruntime.dll`).
  - Choose from **11 curated voices** across all 6 languages (or **Automatic by UI language**), download individual voices or the complete 6-language pack, and test directly with **▶**.
  - Multi-codepoint phoneme map entries in `.onnx.json` configs are automatically sanitized for Windows `piper.exe` compatibility.
  - Speech speed (`-5` to `+5`) uses a unified exponential curve (`Math.Pow(0.87, rate)`) matching Windows SAPI's perceived range, and responses are normalized for natural spoken numbers and units.
