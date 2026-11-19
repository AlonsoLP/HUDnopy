# Spansh and Commander Tools

HUDnopy integrates directly with the **Spansh** API (`https://spansh.co.uk/api/`) and includes local calculators to streamline exploration, trading, mining, colonisation, and Fleet Carrier logistics.

## Spansh Route Plotters (**Navigation → Route**)

- **Supported Plotters:**
  - **Neutron Router** (`/api/route`)
  - **Road to Riches** (`/api/riches/route`)
  - **Exomastery** (`/api/exomastery/route`)
  - **Fleet Carrier Router** (`/api/fleetcarrier/route`)
- **CSV Import:** Import any exported Spansh CSV route directly into the route table.
- **Auto-Advance & Auto-Copy Next Jump:** As you jump (`FSDJump`), HUDnopy automatically advances the active route index and copies the next waypoint system name to your Windows clipboard for instant pasting into the Galaxy Map.

## Spansh Search Tools

- **Nearest Services (**Navigation → Current System**):** Locate the closest Material Trader, Tech Broker, Interstellar Factors, or Tritium seller relative to your live coordinates. Double-click any row to copy the system name.
- **Ship & Module Finder (**Navigation → Current System**):** Search stations stocking a specific ship or module with an optional **Li Yong-Rui (-15% discount)** filter.
- **Trade Route Plotter (**Operations → Market**):** Plans multi-hop trade routes using your current cargo capacity and credit balance.
- **Pristine Mining Rings & Hotspots (**Operations → Mining**):** Finds nearby pristine rings and hotspots for a target mineral.
- **Colonisation Supplier Finder (**Activity → Colonisation**):** Locates nearby Large-pad (`L`) stations supplying the selected or next pending colonisation commodity.

## Fleet Carrier Tritium Calculator (**Navigation → Fleet Carrier**)

- Calculates estimated tritium consumption over any target distance in light-years.
- Accounts for your carrier's **total live loaded mass** (`Capacity - FreeSpace + Fuel` depot tritium) and deducts burned fuel step-by-step across each 500 LY jump.

## GalNet News (**Activity → GalNet**)

- Fetches official multi-language GalNet articles from Frontier's CMS matching your selected UI language.
- Supports one-click read-aloud playback through your configured speech engine (Piper Neural TTS or Windows SAPI) and AI summarization via COVAS.
