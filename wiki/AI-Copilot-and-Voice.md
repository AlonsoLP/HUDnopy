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

## Streaming, ReAct Queries, Voice Macros & Safe Actions

- **Real-Time SSE Streaming:** Gemini, OpenAI, Groq, and Ollama stream responses sentence-by-sentence directly into chat and TTS so COVAS starts speaking on Sentence 1.
- **2-Step Read-Only Queries (`[[QUERY:...]]`):** COVAS can query live local telemetry (`MATERIAL`, `EXPLORATION`, `MISSIONS`, `REPORT`, `PROFILE`, `FLEET`, `STATION`), historical logs (`LOGBOOK`), EDSM (`SYSTEM`), and Spansh (`SPANSH`) on demand.
- **Instant Zero-LLM Voice Macros:** Configure custom trigger phrases (with 4 built-in combat/escape/stealth/landing presets) that execute `.binds` + `DELAY` sequences in 0 ms without calling any LLM.
- **6-Language Zero-Token Tactical Announcements:** Optional instant spoken alerts for High-G approaches (`>= 1.5G`), valuable FSS signals (HGE, Thargoid, Guardian, Megaship), mining prospector hits, player/pirate radio chatter, express system arrival briefings, and voice timers (`[[ACTION:TIMER|...]]`).
- **Safe Actions & Persistent Memory:** When **Allow safe actions** is enabled, COVAS can execute whitelisted `.binds` actions, Spansh searches/routes, EDSY/Coriolis exports, GalNet read-aloud, clipboard copies, surface bookmarks, and persistent commander memory (`REMEMBER` / `FORGET`).

## Voice Input Modes

In **Activity → AI Copilot**:
- **🎤 Speak (Push-to-Talk) & Global Hotkey:** Click to record 16 kHz mono audio via NAudio, or use the configurable **Global PTT Hotkey** (default `Ctrl+Alt+V`) directly from fullscreen/VR without `Alt+Tab`.
- **🎙 Hands-Free (VAD) & Wake Word:** Continuous Voice Activity Detection with a 600 ms pre-buffer, 0.9 s end-of-speech silence detection, trailing-silence trimming, optional **Wake Word** filtering, and automatic microphone pause while COVAS is speaking (`AlertSpeechService.IsSpeaking`).
- **Web Chat:** Available in the Web HUD (`POST /api/ai/ask`) with browser Web Speech API dictation, Pre-Flight Check, and Markdown chat export.

## Piper Neural TTS & Windows SAPI

Open **Settings → Voice & Audio**:
- **Windows SAPI:** Uses installed Windows speech voices immediately.
- **Piper Neural TTS:** Free, offline neural speech synthesis using open-source ONNX models (`rhasspy/piper-voices`) running as a **persistent warm `piper.exe --json-input` daemon** (~100–150 ms per sentence).
  - Selecting Piper automatically downloads the portable Windows engine to `%LocalAppData%\HUDnopy\Piper\` (or reuses an existing valid installation with `espeak-ng-data` and `onnxruntime.dll`).
  - Choose from **11 curated voices** across all 6 languages (or **Automatic by UI language**), download individual voices or the complete 6-language pack, and test directly with **▶**.
  - Multi-codepoint phoneme map entries in `.onnx.json` configs are automatically sanitized for Windows `piper.exe` compatibility.
  - Speech speed (`-5` to `+5`) uses a unified exponential curve (`Math.Pow(0.87, rate)`) matching Windows SAPI's perceived range, and responses are normalized for natural spoken numbers and units.
