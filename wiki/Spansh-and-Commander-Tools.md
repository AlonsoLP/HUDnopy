# Spansh and Commander Tools

HUDnopy integrates directly with the **Spansh** API (`https://spansh.co.uk/api/`) and includes local calculators to streamline exploration, trading, mining, colonisation, and Fleet Carrier logistics.

## Spansh Route Plotters (**Navigation → Route**)

- **Supported Plotters:**
  - **Neutron Router** (`/api/route`)
  - **Road to Riches** (`/api/riches/route`)
  - **Exomastery** (`/api/exomastery/route`)
  - **Fleet Carrier Router** (`/api/fleetcarrier/route`)
  - **Ammonia Worlds, Tourist & Galaxy Routers** (`/api/ammonia/route`, `/api/tourist/route`, `/api/generic/route`)
- **Spansh Deep Search:** Search landable bodies by surface materials (`/api/bodies/search`) and nearby Fleet Carriers by required service (`/api/stations/search`), plus **EDAstro DSSA** nearest deep-space carriers and **Ardent Insight** live commodity markets.
- **CSV Import:** Import any exported Spansh CSV route directly into the route table.
- **Auto-Advance & Auto-Copy Next Jump:** As you jump (`FSDJump`), HUDnopy automatically advances the active route index and copies the next waypoint system name to your Windows clipboard for instant pasting into the Galaxy Map.

## Spansh Search Tools

- **Nearest Services & Ship/Module Finder (**Navigation → Spansh Services**):** Locate the closest Material Trader, Tech Broker, Interstellar Factors, or Tritium seller, or search stations stocking a specific ship or module with an optional **Li Yong-Rui (-15% discount)** filter.
- **Trade Route Plotter (**Inventory → Market**):** Plans multi-hop trade routes using your current cargo capacity and credit balance.
- **Pristine Mining Rings & Hotspots (**Operations → Mining**):** Finds nearby pristine rings and hotspots for a target mineral.
- **Colonisation Supplier Finder (**Operations → Colonisation**):** Locates nearby Large-pad (`L`) stations supplying the selected or next pending colonisation commodity.
- **Engineering Wishlist (**Inventory → Engineers & Wishlist**):** Tracks curated ship and Odyssey blueprints against your live materials inventory with 1-click Material Trader lookup.
- **EDSY / Coriolis Export (**Flight → Ship Modules**):** Exports your current `Loadout` directly to EDSY or Coriolis in one click.

## Commander Operations & Flight Utilities

- **Landing Pad Locator (**Dashboard**):** Resolves all 45 starport landing pads to clock hour, bay depth row, and pad size (`S`/`M`/`L`), displays a visual bay diagram card on both Desktop and Web HUD, and announces pad guidance via TTS (automatically cleared on undock or `FSDJump`).
- **Elite Server Status & Galaxy BGS Tick (**Dashboard** & **Navigation → Current System**):** Polls EDSM server status and the latest EliteBGS galaxy tick every 10 minutes with a manual refresh button.
- **Live Surface Compass & Exo Sample Target (**Navigation → Surface**):** Spherical bearing, turn arrows (`◀◀`/`◀`/`▲`/`▶`/`▶▶`), ASCII compass tape, and a 1-click **Last Exo Sample** button targeting your latest exobiology sample coordinates.
- **Cargo Profit & Colonisation Cross-Reference (**Inventory → Cargo**):** Compares live hold contents against docked `Market.json` prices (estimated sell value & profit) and active Colonisation resource needs.
- **Missions Grouped by System (**Operations → Missions**):** Groups active contracts by destination system, displays pending-cargo shortage warnings, and shows active Frontier cAPI Community Goals.
- **Historical Expedition Analytics (**Activity → Milestones**):** Scans up to 100 recent Journal logs asynchronously to compute jumps, LY distance, unique systems, ELW/WW/AW counts, exobiology species, missions, and earnings, with Markdown report export.

## Fleet Carrier Tritium Calculator & Jump Countdown (**Navigation → Fleet Carrier**)

- Live `T-MM:SS` Fleet Carrier jump countdown with automatic TTS alerts at 10 min, 3 min (lockdown), and 1 min before jump, plus weekly/monthly upkeep runway calculation.
- Calculates estimated tritium consumption over any target distance in light-years, accounting for your carrier's **total live loaded mass** (`Capacity - FreeSpace + Fuel` depot tritium) and deducting burned fuel step-by-step across each 500 LY jump.

## GalNet News (**Activity → GalNet**)

- Fetches official multi-language GalNet articles from Frontier's CMS matching your selected UI language.
- Supports one-click read-aloud playback through your configured speech engine (Piper Neural TTS or Windows SAPI) and AI summarization via COVAS.
