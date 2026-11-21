# Keybindings and Control Deck

HUDnopy parses your active Elite Dangerous `.binds` XML profile (`Services/BindingsService.cs`) and turns it into an interactive desktop and Web HUD control deck (`Services/InputSimulatorService.cs`).

## `.binds` Profile Parsing

- Automatically discovers `.binds` files in `%LocalAppData%\Frontier Developments\Elite Dangerous\Options\Bindings` (configurable in **Settings → Game, Files & Controls**).
- Displays human-readable key labels and modifiers localized across all 6 languages.
- Supports filtering out unassigned actions (**Hide unassigned bindings**) and instant hot-reloading after in-game binding edits.

## Auto-Context Deck Switching

When **Auto-context deck** is enabled, HUDnopy inspects live `Status.json` flags (`Flags` / `Flags2`) and automatically switches the active control deck category between:
- **Ship (Flight / Combat / Systems)**
- **SRV (Surface Recon Vehicle)**
- **On-Foot (Odyssey)**
- **DSS (Detailed Surface Scanner)**
- **SLF (Ship-Launched Fighter)**

## Macros, Voice Macros & Window Focus

- Supports multi-step **Deck Macros** (`DeckMacroDefinition`) combining sequential keybind actions with configurable delays.
- Supports **Zero-LLM Voice Macros** (`Settings → AI Copilot`) that match custom spoken phrases locally in 0 ms and execute `.binds` + `DELAY` sequences without calling any AI provider.
- Supports a system-wide **Global Push-to-Talk Hotkey** (default `Ctrl+Alt+V`) for COVAS voice input in fullscreen or VR without `Alt+Tab`.
- When **Focus game window on input** is enabled, `InputSimulatorService` brings the Elite Dangerous window to the foreground before dispatching Windows `SendInput` scancodes, ensuring reliable execution from both the desktop deck and the remote Web HUD.
