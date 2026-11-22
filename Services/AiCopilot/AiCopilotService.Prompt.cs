// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/AiCopilot/AiCopilotService.Prompt.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Prefix-cache-friendly prompt builder, compact JSON telemetry, pre-injection, and thinking config.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Speech.Recognition;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HUDnopy.Models;

namespace HUDnopy.Services;

public sealed partial class AiCopilotService
{
    /// <summary>
    /// Builds a prefix-cache-friendly system prompt: static rules, query/action schemas, and memory go FIRST
    /// (enabling automatic Ollama KV cache and Gemini/OpenAI implicit prefix caching), followed by compact
    /// filtered JSON live telemetry and contextually relevant sections + 1-turn pre-injected analytics.
    /// </summary>
    private string BuildSystemPrompt(AppConfig cfg, ShipState s, string? userPrompt = null)
    {
        string personality = cfg.AiPersonality switch
        {
            "tactical" => "Eres un oficial táctico militar conciso y directo a bordo de una nave de Elite Dangerous.",
            "scientist" => "Eres un oficial científico entusiasta especializado en astrofísica y exobiología en Elite Dangerous.",
            "sarcastic" => "Eres un copiloto veterano con humor seco y ligeramente sarcástico, pero siempre leal y útil.",
            "custom" when !string.IsNullOrWhiteSpace(cfg.AiCustomPersonality) => cfg.AiCustomPersonality.Trim(),
            _ => "Eres COVAS, el ordenador de a bordo profesional, claro y conciso de Elite Dangerous."
        };

        string verbosityRule = (cfg.AiVerbosity ?? "normal").Trim().ToLowerInvariant() switch
        {
            "brief" => "Extremadamente breve: máximo 1 o 2 frases cortas, estilo radio militar de combate.",
            "detailed" => "Detallado y explicativo, ofreciendo contexto útil de exploración o estrategia cuando aporte valor.",
            _ => "Conciso (2 a 4 frases claras, ideal para escuchar mientras se pilota)."
        };

        string langName = cfg.InterfaceLanguage switch
        {
            "en-GB" => "English",
            "fr-FR" => "Français",
            "de-DE" => "Deutsch",
            "ru-RU" => "Русский",
            "pt-BR" => "Português",
            _ => "Español"
        };

        // =====================================================================
        // 1. STATIC PREFIX (100% invariant between turns -> KV / Implicit Cache)
        // =====================================================================
        var sb = new StringBuilder(2048);
        sb.AppendLine($"{personality} Responde siempre en {langName}. {verbosityRule}");
        sb.AppendLine("Estilo de voz: texto natural sin Markdown ni emojis. Escribe unidades completas (créditos, toneladas, años luz, segundos luz, por ciento).");
        sb.AppendLine("Nunca confundas la nave actual con el portanaves (Fleet Carrier).");

        if (cfg.AiCommanderMemory is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("=== MEMORIA DEL COMANDANTE ===");
            foreach (string mem in cfg.AiCommanderMemory.TakeLast(15))
                sb.AppendLine($"- {mem}");
        }

        sb.AppendLine();
        sb.AppendLine("=== CONSULTAS EN VIVO (2 PASOS, SOLO SI FALTA EL DATO) ===");
        sb.AppendLine("Si falta un dato exacto abajo, responde ÚNICAMENTE con 1-3 etiquetas [[QUERY:...]] y recibirás el dato real:");
        sb.AppendLine("- Materiales/Odyssey: [[QUERY:MATERIAL|<nombre|g1|g2|g3|g4|g5|full|full:g5|full:raw|full:manufactured|full:encoded|full:odyssey|lowest|lowest:g5|lowest:raw|lowest:manufactured|lowest:encoded|lowest:odyssey|summary>]] (usa 'g1'..'g5' para un grado concreto, 'full' para llenos/100%/tope y 'lowest' para los que menos tiene)");
        sb.AppendLine("- Exploración/Sistema: [[QUERY:EXPLORATION|<bodies|bio|materials:<elem>|gravity|route|factions>]]");
        sb.AppendLine("- Misiones/Masacres: [[QUERY:MISSIONS|<summary|urgent|rewards|massacre|cargo>]]");
        sb.AppendLine("- Informes: [[QUERY:REPORT|<colonisation|session|combat|mining|ship>]]");
        sb.AppendLine("- Perfil/Ingenieros: [[QUERY:PROFILE|<ranks|engineers|all>]]");
        sb.AppendLine("- Flota/Carrier: [[QUERY:FLEET|<ships|ships:<nombre>|modules|modules:<nombre>|carrier|orders|services|bartender|cargo>]]");
        sb.AppendLine("- Carga/Wishlist/Superficie/Pasajeros/CG/Servidor: [[QUERY:CARGO_PROFIT|now]] | [[QUERY:WISHLIST|now]] | [[QUERY:SURFACE|now]] | [[QUERY:PASSENGERS|now]] | [[QUERY:COMMUNITY_GOALS|now]] | [[QUERY:SERVER|status]]");
        sb.AppendLine("- Alertas/Aterrizaje/Combustible/Expedición/WebHUD: [[QUERY:ALERTS|active]] | [[QUERY:LANDING_PAD|now]] | [[QUERY:FUEL_EMERGENCY|now]] | [[QUERY:EXPEDITION|now]] | [[QUERY:WEB_HUD|now]]");
        sb.AppendLine("- Síntesis/Odyssey/Valor sin vender/Eventos recientes: [[QUERY:SYNTHESIS|now]] | [[QUERY:ODYSSEY_GEAR|now]] | [[QUERY:EXOBIO_VALUE|now]] | [[QUERY:RECENT_EVENTS|<filtro>]]");
        sb.AppendLine("- Otro sistema EDSM: [[QUERY:SYSTEM|<NombreSistema>]] | Tráfico/Peligro EDSM: [[QUERY:EDSM_TRAFFIC|<NombreSistema>]] | Distancia 3D: [[QUERY:EDSM_DISTANCE|<Origen>|<Destino>]] | Descubrimiento EDSM: [[QUERY:EDSM_DISCOVERY|<NombreSistema>]] | Estaciones EDSM: [[QUERY:EDSM_STATIONS|<NombreSistema>]] | Sistemas cercanos EDSM: [[QUERY:EDSM_NEARBY|<Sistema>|<RadioAL>]] | Dump Spansh actual: [[QUERY:SPANSH_SYSTEM|now]]");
        sb.AppendLine("- Spansh servicios/cuerpos/carriers/comercio/minería: [[QUERY:SPANSH|<material_trader|tech_broker|interstellar_factors|tritium>]] | [[QUERY:SPANSH_BODIES|<Material>]] | [[QUERY:SPANSH_CARRIERS|<Servicio>]] | [[QUERY:SPANSH_TRADE|now]] | [[QUERY:SPANSH_MINING|<Mineral>]]");
        sb.AppendLine("- Mercados Ardent / Mejor venta bodega / DSSA / EliteBGS / Inara: [[QUERY:ARDENT|<Mercancía>|<sell|buy>|<opcional:carriers>]] | [[QUERY:CARGO_BEST_SELL|now]] | [[QUERY:DSSA|nearest]] | [[QUERY:BGS|watchlist]] | [[QUERY:INARA|<CMDR|cgs>]]");
        sb.AppendLine("- Bitácora Journals: [[QUERY:LOGBOOK|<summary|system:Nombre>]] | Diario oficial Frontier: [[QUERY:CAPI_JOURNAL|<today|YYYY-MM-DD>]] | Estación local: [[QUERY:STATION|<shipyard|outfitting|market>|<filtro>]]");

        if (cfg.AiAllowActions)
        {
            sb.AppendLine();
            sb.AppendLine("=== ACCIONES (OPCIONAL, AL FINAL) ===");
            sb.AppendLine($"- Control/Macro: [[ACTION:BIND|<ActionId>]] → {BuildDynamicBindingsPromptList(s)}");
            sb.AppendLine("- GalNet voz completa: [[ACTION:GALNET_READ|<1..N>]] | Spansh: [[ACTION:SPANSH|<material_trader|tech_broker|interstellar_factors|tritium>]]");
            sb.AppendLine("- Ruta Spansh: [[ACTION:SPANSH_ROUTE|<neutron|riches|exomastery|carrier|ammonia|tourist|galaxy>|<Destino>]] | Ítem: [[ACTION:SPANSH_ITEM|<Nombre>|<true|false LYR>]]");
            sb.AppendLine("- Comercio/Minería/Colonización: [[ACTION:SPANSH_TRADE|now]] | [[ACTION:SPANSH_MINING|<Mineral>]] | [[ACTION:COLONISATION_SUPPLIER|now]]");
            sb.AppendLine("- Integraciones y alertas: [[ACTION:CAPI_SYNC|now]] | [[ACTION:CAPI_JOURNAL|today]] | [[ACTION:BGS_REFRESH|now]] | [[ACTION:BGS_WATCH_ADD|<Facción>]] | [[ACTION:BGS_WATCH_REMOVE|<Facción>]] | [[ACTION:GALAXY_REFRESH|now]] | [[ACTION:ALERTS_CLEAR|now]] | [[ACTION:INARA_OPEN|<system|station|commodity|commander>|<Objetivo>]]");
            sb.AppendLine("- Activar/Desactivar APIs: [[ACTION:TOGGLE_INTEGRATION|<frontier|eddn|edsm|inara|spansh|bgs|ardent|dssa|canonn|discord>|<on|off>]]");
            sb.AppendLine("- Wishlist: [[ACTION:WISHLIST_ADD|<fsd_range_g5|fsd_mass_manager|thrusters_dirty_g5|pp_armoured_g5|pp_overcharged_g5|pd_charge_g5|shield_thermal_g5|mc_overcharged_g5|hull_heavy_g5|odyssey_suit_g5|odyssey_weapon_g5>]] | [[ACTION:WISHLIST_CLEAR|now]]");
            sb.AppendLine("- Pips/Minería/Sesión/Superficie: [[ACTION:PIP_PRESET|<combat|tank|escape|explore|mining|balanced>]] | [[ACTION:MINING_FILTER|<minerales>|<porcentaje>]] | [[ACTION:RESET_SESSION|<combat|mining>]] | [[ACTION:SURFACE_TARGET|<lat>|<lon>]]");
            sb.AppendLine("- Memoria/Timer/Voz/Pausa/Discord: [[ACTION:REMEMBER|<dato>]] | [[ACTION:FORGET|<texto>]] | [[ACTION:TIMER|<min>|<motivo>]] | [[ACTION:TRITIUM_CALC|<AL>]] | [[ACTION:VOICE_RATE|<+1|-1|0>]] | [[ACTION:DISCORD|<Mensaje>]] | [[ACTION:DISCORD_SUMMARY|<session|ship>]] | [[ACTION:DISCORD_EXPEDITION|now]] | [[ACTION:DISCORD_SCREENSHOT|<Pie de foto>]] | [[ACTION:DISCORD_CARRIER|now]] | [[ACTION:DISCORD_BUILD|now]] | [[ACTION:DELAY|<ms>]]");
            sb.AppendLine("- Utilidades/Ventana: [[ACTION:NOTE|<texto>]] | [[ACTION:COPY|<Sistema>]] | [[ACTION:BOOKMARK|<Etiqueta>]] | [[ACTION:NAV_BOOKMARK|<Etiqueta|EXO>]] | [[ACTION:EXPORT_SHIP|<edsy|coriolis>]] | [[ACTION:HUD_MODE|<minihud|fullscreen|normal>]] | [[ACTION:UI|<dashboard|flight|navigation|operations|inventory|activity|controls|minihud>]]");
        }

        // =====================================================================
        // 2. COMPACT FILTERED JSON TELEMETRY (Dynamic suffix, pruned by intent)
        // =====================================================================
        sb.AppendLine();
        sb.AppendLine("=== TELEMETRÍA EN VIVO (JSON COMPACTO) ===");
        sb.AppendLine(BuildCompactTelemetryJson(cfg, s, userPrompt));

        // =====================================================================
        // 3. PRE-INJECTED 1-TURN ANALYTICS (Eliminates 2nd LLM round-trip)
        // =====================================================================
        string preInjected = BuildPreInjectedAnalytics(s, userPrompt);
        if (!string.IsNullOrWhiteSpace(preInjected))
        {
            sb.AppendLine();
            sb.AppendLine("=== DATOS ANALÍTICOS PRE-CARGADOS PARA ESTA CONSULTA ===");
            sb.AppendLine(preInjected);
        }

        if (!string.IsNullOrWhiteSpace(_lastExternalQuerySummary) &&
            DateTimeOffset.UtcNow - _lastExternalQueryUtc <= TimeSpan.FromMinutes(5))
        {
            sb.AppendLine();
            sb.AppendLine("=== ÚLTIMA CONSULTA EXTERNA (REFERENCIA PARA SEGUIMIENTO) ===");
            sb.AppendLine(_lastExternalQuerySummary);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Serializes only non-empty, contextually relevant ShipState fields into minified JSON
    /// to cut prompt token usage by 60-70% without losing precision.
    /// </summary>
    private string BuildCompactTelemetryJson(AppConfig cfg, ShipState s, string? userPrompt)
    {
        string p = (userPrompt ?? "").ToLowerInvariant();
        // Voice audio path (userPrompt == null) keeps core telemetry ultra-compact so Turn 1 stays fast
        bool general = !string.IsNullOrWhiteSpace(p) &&
            ContainsAny(p, "estado", "resumen", "status", "report", "informe", "chequeo", "pre-vuelo", "preflight", "sesión", "sesion", "todo");

        bool wantGalnet = ContainsAny(p, "galnet", "notic", "news", "lore", "thargoid", "bolet", "actualidad", "artíc", "artic");
        bool wantCarrier = general || ContainsAny(p, "carrier", "portanaves", "tritio", "tritium", "flota", "almacen", "bartender", "cg", "comunit");
        bool wantMarketCargo = general || ContainsAny(p, "carga", "bodega", "vend", "compr", "mercad", "ofert", "astillero", "equipamiento", "módulo", "modulo", "shipyard", "outfitting", "nave");
        bool wantMissions = general || ContainsAny(p, "misi", "masacre", "pasajer", "vip", "recompensa", "caduc", "combate", "bajas", "miner", "coloniz");
        bool wantExplore = general || ContainsAny(p, "explor", "planeta", "cuerpo", "mapear", "bio", "exobio", "gravedad", "ruta", "salto", "facci", "bgs", "tick", "servidor", "powerplay");
        bool wantEng = general || ContainsAny(p, "ingenier", "wishlist", "material", "módulo", "modulo", "fsd", "arma", "escudo", "motor", "dañ");

        string activeMode = s.OnFoot ? "OnFoot"
            : s.InSRV ? "SRV"
            : s.InFighter ? "Fighter"
            : s.Supercruise ? "Supercruise"
            : s.Docked ? "Docked"
            : s.Landed ? "Landed"
            : "NormalSpace";

        var root = new Dictionary<string, object?>
        {
            ["cmdr"] = s.CommanderName,
            ["cr"] = s.Credits,
            ["mode"] = activeMode,
            ["ship"] = $"{s.ShipType} ({s.ShipName}/{s.ShipIdent})",
            ["hull"] = Math.Round(s.HullHealth, 1),
            ["shld"] = s.ShieldsUp ? "ON" : "OFF",
            ["fuel"] = $"{s.FuelMain:0.1}/{s.FuelMax:0.1}t",
            ["cargo"] = $"{s.CargoUsed}/{s.CargoCapacity}t",
            ["sys"] = s.CurrentSystem
        };

        string loc = string.IsNullOrWhiteSpace(s.StationName) ? s.BodyName : s.StationName;
        if (!string.IsNullOrWhiteSpace(loc)) root["loc"] = loc;
        if (s.LimpetCount > 0) root["limpets"] = s.LimpetCount;
        if (!string.IsNullOrWhiteSpace(s.SecurityLevel)) root["sec"] = s.SecurityLevel;
        if (!string.IsNullOrWhiteSpace(s.Economy)) root["eco"] = s.Economy;
        if (!string.IsNullOrWhiteSpace(s.CurrentSystemNote)) root["note"] = s.CurrentSystemNote;

        if (s.NavRoute.Count > 0)
            root["route"] = $"{s.NavRoute.Count} jumps, nextFuel:{(s.RouteHasNoFuelStar ? "NONE" : (s.JumpsToNextFuelStar?.ToString() ?? "?"))}";

        if (s.AssignedLandingPad is int padNum && padNum > 0)
            root["pad"] = LandingPadLocatorService.Resolve(padNum, s.AssignedLandingStationType).FormatGuidance(cfg.InterfaceLanguage);

        var damaged = s.ShipModules.Where(static m => m.HealthPercent is > 0 and < 85).OrderBy(static m => m.HealthPercent).Take(4)
            .Select(static m => $"{m.DisplayName}:{m.HealthPercent:0}%").ToList();
        if (damaged.Count > 0) root["dmg"] = damaged;

        if (s.ExobiologyTracker is { } exo)
            root["exo"] = $"{exo.Species}@{exo.BodyName} ({exo.SamplesCollected}/3, {exo.CurrentDistanceMeters:0}/{exo.RequiredDistanceMeters}m)";

        if (s.CurrentTarget is { } t && !string.IsNullOrWhiteSpace(t.Ship))
            root["target"] = $"{t.Ship} ({t.PilotName},{t.LegalStatus},{t.Bounty:N0}CR,S:{t.ShieldHealth}% H:{t.HullHealth}%)";

        if (wantMarketCargo)
        {
            if (s.CargoInventory.Count > 0)
                root["cargoItems"] = s.CargoInventory.OrderByDescending(static c => c.Count).Take(6).Select(static c => $"{c.Name}:{c.Count}t").ToList();
            if (s.Market?.Items is { Count: > 0 })
            {
                var bargains = s.Market.Items
                    .Where(static m => m.Stock > 50 && m.BuyPrice > 0 && m.MeanPrice > m.BuyPrice)
                    .OrderByDescending(static m => m.MeanPrice - m.BuyPrice)
                    .Take(3)
                    .Select(static m => $"{m.DisplayName}({m.BuyPrice:N0}/{m.MeanPrice:N0}CR)").ToList();
                if (bargains.Count > 0) root["bargains"] = bargains;
            }
            if (s.Shipyard?.Available == true && s.Shipyard.Items.Count > 0)
                root["shipyard"] = $"{s.Shipyard.StationName} ({s.Shipyard.Items.Count}): {string.Join(", ", s.Shipyard.Items.Take(20).Select(static sh => sh.DisplayName))}";
            if (s.Outfitting?.Available == true && s.Outfitting.Items.Count > 0)
                root["outfittingCount"] = $"{s.Outfitting.StationName} ({s.Outfitting.Items.Count} mods)";
        }

        if (wantEng)
        {
            var engMods = s.ShipModules
                .Where(static m => m.IsVital || !string.IsNullOrWhiteSpace(m.Engineering))
                .Take(10)
                .Select(static m => string.IsNullOrWhiteSpace(m.Engineering) ? m.DisplayName : $"{m.DisplayName}[{m.Engineering}]")
                .ToList();
            if (engMods.Count > 0) root["modules"] = engMods;

            if (cfg.EngineeringWishlistBlueprints is { Count: > 0 })
            {
                var missing = EngineeringWishlistService.Evaluate(cfg.EngineeringWishlistBlueprints, s)
                    .Where(static r => r.Missing > 0).Take(6)
                    .Select(static r => $"{r.MaterialName}:-{r.Missing}").ToList();
                root["wishlist"] = missing.Count > 0 ? string.Join(",", missing) : "READY";
            }
        }

        if (wantMissions)
        {
            if (s.ActiveMissions.Count > 0)
                root["missions"] = $"{s.ActiveMissions.Count} active (massacreStacks:{s.MassacreStacks.Count})";
            if (s.PassengerManifest.Count > 0)
                root["pax"] = s.PassengerManifest.Count;
            if (s.CombatSession.TotalKills > 0)
                root["combat"] = $"{s.CombatSession.TotalKills} kills, {s.CombatSession.PendingCredits:N0}CR";
            if (s.MiningSession.TotalRefinedTons > 0)
                root["mining"] = $"{s.MiningSession.TotalRefinedTons}t ({s.MiningSession.TonsPerHour:0.0}t/h)";
            if (s.Colonisation.Resources.Count > 0)
            {
                long rem = s.Colonisation.Resources.Sum(static r => Math.Max(0, r.RequiredAmount - r.ProvidedAmount));
                root["colonisationRemTons"] = rem;
            }
        }

        if (wantExplore)
        {
            if (s.UnsoldCartographyCredits > 0 || s.UnsoldExobiologyCredits > 0)
                root["unsold"] = $"carto:{s.UnsoldCartographyCredits:N0}CR, exo:{s.UnsoldExobiologyCredits:N0}CR";
            var unmapped = s.ExplorationBodies
                .Where(static b => b.WasMapped != true && ((b.EstimatedCredits ?? 0) >= 500_000 || b.IsVirginBioLandable))
                .OrderByDescending(static b => b.EstimatedCredits ?? 0)
                .Take(4)
                .Select(static b => $"{b.BodyName}({b.BodyType}{(b.BiologicalSignalCount > 0 ? $",{b.BiologicalSignalCount}bio" : "")})").ToList();
            if (unmapped.Count > 0) root["unmappedValuable"] = unmapped;
            if (s.SystemFactions.Count > 0)
                root["factions"] = s.SystemFactions.OrderByDescending(static f => f.InfluencePercent).Take(3)
                    .Select(static f => $"{f.Name}:{f.InfluenceDisplay}({f.FactionState})").ToList();
            if (s.EdsmDiscoveryIntel is { } edsmDisc && !string.IsNullOrWhiteSpace(edsmDisc.SystemName))
                root["edsmDiscovery"] = edsmDisc.DiscoveryBadge;
            if (s.WatchedBgsFactions.Count > 0)
                root["bgsWatch"] = s.WatchedBgsFactions.Take(4)
                    .Select(static f => $"{f.FactionName}@{f.SystemName}:{f.InfluenceDisplay}({f.State})").ToList();
            var gSnap = _galaxyStatus.Current;
            if (!string.IsNullOrWhiteSpace(gSnap.ServerMessage) || gSnap.LastBgsTickUtc.HasValue)
                root["server"] = $"{gSnap.ServerMessage ?? "?"} | BGS:{(gSnap.LastBgsTickUtc.HasValue ? gSnap.LastBgsTickUtc.Value.ToString("yyyy-MM-dd HH:mm") + "Z" : "?")}";
            if (!string.IsNullOrWhiteSpace(s.PowerplaySummary))
                root["powerplay"] = s.PowerplaySummary;
        }

        if (wantCarrier)
        {
            if (s.Carrier is { } fc)
            {
                root["carrier"] = new Dictionary<string, object?>
                {
                    ["name"] = $"{fc.Name} [{fc.Callsign}]",
                    ["sys"] = fc.System,
                    ["fuel"] = fc.Fuel,
                    ["freeSpace"] = fc.FreeSpace,
                    ["balance"] = fc.Balance,
                    ["dest"] = string.IsNullOrWhiteSpace(fc.Destination) ? null : fc.Destination
                }.Where(static kv => kv.Value is not null).ToDictionary(static kv => kv.Key, static kv => kv.Value);
            }
            else
            {
                root["carrier"] = "NO_DATA";
            }
            if (s.StoredShips.Count > 0)
                root["storedShips"] = s.StoredShips.Count;
            if (s.StoredModules.Count > 0)
                root["storedModules"] = s.StoredModules.Count;
            if (s.CarrierMarketOrders.Count > 0)
                root["carrierOrders"] = s.CarrierMarketOrders.Count;
            if (s.CarrierServices.Count > 0)
                root["carrierServices"] = string.Join(", ", s.CarrierServices);
            if (s.CommunityGoals.Count > 0)
                root["cg"] = s.CommunityGoals.Take(2).Select(static c => $"{c.Title}@{c.StarSystem}").ToList();
            if (s.InaraProfile is { } ip && !string.IsNullOrWhiteSpace(ip.CommanderName))
                root["inara"] = $"{ip.CommanderName} ({ip.SquadronName})";
        }

        if (wantGalnet && _galnetService.Articles.Count > 0)
        {
            var articles = new List<string>();
            for (int i = 0; i < Math.Min(4, _galnetService.Articles.Count); i++)
            {
                var a = _galnetService.Articles[i];
                string excerpt = a.Body.Length > 240 ? a.Body[..240] + "..." : a.Body;
                articles.Add($"[#{i + 1}{(i == 0 ? " LATEST" : "")}] ({a.DateDisplay}) {a.Title}: {excerpt}");
            }
            root["galnet"] = articles;
        }

        if (general)
        {
            root["session"] = $"net:{s.SessionStats.NetCredits:+#,0;-#,0;0}CR, sys:{s.SessionStats.SystemsVisited}({s.SessionStats.TotalDistanceLy:0.0}LY), scans:{s.SessionStats.BodiesScanned}, exo:{s.SessionStats.OrganicsAnalysed}";
        }

        return JsonSerializer.Serialize(root);
    }

    /// <summary>
    /// Detects common analytical intents before Turn 1 and pre-injects the exact local report
    /// so COVAS answers in 1 single LLM round-trip instead of 2.
    /// </summary>
    private string BuildPreInjectedAnalytics(ShipState s, string? userPrompt)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
            return "";

        string p = NormalizeVoiceMatchText(userPrompt, null);
        var chunks = new List<string>();

        // 1. Materials ranking / scarcity / fullness / specific grade (G1-G5)
        if (ContainsAny(p, "material", "taquilla", "odyssey", "mochila"))
        {
            string filter = ContainsAny(p, "g5", "grado 5", "grado cinco", "grade 5", "grade five") ? ":g5"
                : ContainsAny(p, "g4", "grado 4", "grado cuatro", "grade 4", "grade four") ? ":g4"
                : ContainsAny(p, "g3", "grado 3", "grado tres", "grade 3", "grade three") ? ":g3"
                : ContainsAny(p, "g2", "grado 2", "grado dos", "grade 2", "grade two") ? ":g2"
                : ContainsAny(p, "g1", "grado 1", "grado uno", "grade 1", "grade one") ? ":g1"
                : ContainsAny(p, "bruto", "raw") ? ":raw"
                : ContainsAny(p, "manufactur") ? ":manufactured"
                : ContainsAny(p, "codific", "encoded", "datos") ? ":encoded"
                : ContainsAny(p, "odyssey", "taquilla") ? ":odyssey"
                : "";

            if (ContainsAny(p, "menos", "escas", "faltan", "bajo", "minimo", "poco", "lowest", "least"))
                chunks.Add(QueryMaterialInventory("lowest" + filter));
            else if (ContainsAny(p, "lleno", "100", "completo", "tope", "mas tengo", "maximo", "sobra", "trader", "full", "highest", "most"))
                chunks.Add(QueryMaterialInventory("full" + filter));
            else if (!string.IsNullOrEmpty(filter))
                chunks.Add(QueryMaterialInventory(filter.TrimStart(':')));
            else if (ContainsAny(p, "resumen", "cuantos", "inventario", "summary", "como ando", "como voy"))
                chunks.Add(QueryMaterialInventory("summary"));
        }

        // 2. Missions urgency / rewards / massacre / cargo
        if (ContainsAny(p, "mision", "masacre", "caduc", "vence"))
        {
            if (ContainsAny(p, "caduc", "vence", "urgente", "antes", "expir"))
                chunks.Add(QueryMissionsReport("urgent"));
            else if (ContainsAny(p, "masacre", "massacre", "bajas"))
                chunks.Add(QueryMissionsReport("massacre"));
            else if (ContainsAny(p, "recompensa", "dinero", "pagan", "creditos", "reward"))
                chunks.Add(QueryMissionsReport("rewards"));
            else if (ContainsAny(p, "carga", "bodega", "falta", "cargo"))
                chunks.Add(QueryMissionsReport("cargo"));
            else
                chunks.Add(QueryMissionsReport("summary"));
        }

        // 3. Colonisation trips & progress
        if (ContainsAny(p, "coloniz", "obra", "construccion") && s.Colonisation.Resources.Count > 0)
            chunks.Add(QueryOperationalReport("colonisation"));

        // 4. Exploration gravity / bio / route / factions
        if (ContainsAny(p, "gravedad", "gravity"))
            chunks.Add(QueryExplorationReport("gravity"));
        if (ContainsAny(p, "exobio", "biolog", "plantas", "especies"))
            chunks.Add(QueryExplorationReport("bio"));
        if (ContainsAny(p, "ruta", "route") && ContainsAny(p, "salto", "repost", "estrella", "informe", "como"))
            chunks.Add(QueryExplorationReport("route"));
        if (ContainsAny(p, "faccion", "bgs", "influencia"))
            chunks.Add(QueryExplorationReport("factions"));

        // 5. Profile ranks & engineers
        if (ContainsAny(p, "rango", "rank", "perfil"))
            chunks.Add(QueryProfileReport("ranks"));
        if (ContainsAny(p, "escuadron", "squadron", "inara") && s.InaraProfile is { } ip && !string.IsNullOrWhiteSpace(ip.CommanderName))
            chunks.Add($"Inara: {ip.CommanderName} | Escuadrón: {(string.IsNullOrWhiteSpace(ip.SquadronName) ? "ninguno" : $"{ip.SquadronName} ({ip.SquadronRank})")} | Lealtad: {ip.PreferredAllegiance}");
        if (ContainsAny(p, "ingeniero", "engineer") && ContainsAny(p, "grado", "desbloque", "falta", "subir", "pendiente", "tengo"))
            chunks.Add(QueryProfileReport("engineers"));

        // 6. Fleet & Carrier reports (prioritize exact ship name match before generic fleet dump)
        var matchedStoredShip = s.StoredShips.FirstOrDefault(sh =>
            !string.IsNullOrWhiteSpace(sh.ShipName) &&
            sh.ShipName.Length >= 3 &&
            p.Contains(NormalizeVoiceMatchText(sh.ShipName, null), StringComparison.OrdinalIgnoreCase));
        if (matchedStoredShip is not null)
        {
            chunks.Add(QueryFleetReport($"ships:{matchedStoredShip.ShipName}"));
        }
        else if (!string.IsNullOrWhiteSpace(s.ShipName) &&
                 s.ShipName.Length >= 3 &&
                 p.Contains(NormalizeVoiceMatchText(s.ShipName, null), StringComparison.OrdinalIgnoreCase))
        {
            chunks.Add(QueryFleetReport($"ships:{s.ShipName}"));
        }
        else if (ContainsAny(p, "flota", "almacenad", "aparcad", "donde tengo", "cuantas naves", "stored"))
        {
            chunks.Add(QueryFleetReport("ships"));
        }
        if (ContainsAny(p, "modulos almacenados", "modulo guardado", "donde guarde", "stored modules"))
            chunks.Add(QueryFleetReport("modules"));
        if (ContainsAny(p, "ordenes de mercado", "ordenes del portanaves", "carrier orders"))
            chunks.Add(QueryFleetReport("orders"));
        if (ContainsAny(p, "servicios del portanaves", "servicios activos", "carrier services"))
            chunks.Add(QueryFleetReport("services"));
        if (ContainsAny(p, "bartender"))
            chunks.Add(QueryFleetReport("bartender"));
        if (ContainsAny(p, "portanaves", "carrier") && ContainsAny(p, "mantenimiento", "semanas", "autonomia", "saldo", "finanz", "informe"))
            chunks.Add(QueryFleetReport("carrier"));

        // 7. Local Station Shipyard / Outfitting / Cargo Profit
        if (ContainsAny(p, "astillero", "shipyard", "naves disponibles", "que naves", "comprar nave", "venden naves"))
            chunks.Add(QueryStationSnapshot("shipyard"));
        if (ContainsAny(p, "equipamiento", "outfitting"))
            chunks.Add(QueryStationSnapshot("outfitting"));
        if (ContainsAny(p, "vale mi carga", "vender mi carga", "beneficio de la carga", "cargo profit", "valor de la bodega"))
            chunks.Add(QueryCargoProfitReport());

        // 8. Wishlist, Surface, Passengers, Server & Integrations status
        if (ContainsAny(p, "wishlist", "lista de deseos", "que me falta para ingenier"))
            chunks.Add(QueryWishlistReport());
        if (ContainsAny(p, "coordenada", "latitud", "longitud", "marcador", "superficie", "bookmark"))
            chunks.Add(QuerySurfaceReport());
        if (ContainsAny(p, "pasajero", "cabina", "passenger"))
            chunks.Add(QueryPassengersReport());
        if (ContainsAny(p, "servidor", "tick", "integracion", "apis activas", "server status"))
            chunks.Add(QueryServerAndIntegrationsStatus());
        if (ContainsAny(p, "virgen", "descubierto", "first discovery", "conocido en edsm") && s.EdsmDiscoveryIntel is { } edsmD)
            chunks.Add($"{edsmD.SystemName}: {edsmD.DiscoveryBadge}");
        if (ContainsAny(p, "watchlist", "facciones seguidas", "conflicto", "guerra", "eleccion") && s.WatchedBgsFactions.Count > 0)
            chunks.Add($"EliteBGS Watchlist: {string.Join(" | ", s.WatchedBgsFactions.Take(6).Select(static f => $"{f.FactionName}@{f.SystemName}: {f.InfluenceDisplay} ({f.State})"))}");
        if (ContainsAny(p, "dssa") && s.NearestDssaCarriers.Count > 0)
            chunks.Add($"DSSA cercanos en caché: {string.Join(" | ", s.NearestDssaCarriers.Take(4).Select(static c => $"{c.Callsign} {c.Name} en {c.SystemName} ({c.DistanceDisplay})"))}");
        if (ContainsAny(p, "plataforma", "landing pad", "donde aterrizo", "bahia"))
            chunks.Add(QueryLandingPadReport());
        if (ContainsAny(p, "alerta", "aviso", "peligro activo", "active alerts"))
            chunks.Add(QueryActiveAlertsReport());
        if (ContainsAny(p, "web hud", "web mfd", "codigo de sesion", "url del servidor"))
            chunks.Add(QueryWebHudStatus());
        if (ContainsAny(p, "sintesis", "sintetizar", "synthesis", "inyeccion fsd", "fsd injection", "oxigeno", "afmu"))
            chunks.Add(QuerySynthesisReadiness());
        if (ContainsAny(p, "a pie", "traje", "on foot", "suit", "mochila", "backpack"))
            chunks.Add(QueryOdysseyGearReport());
        if (ContainsAny(p, "sin vender", "en riesgo", "unsold", "valor exobio", "cartografia acumulada"))
            chunks.Add(QueryExobioAndCartoValueReport());
        if (ContainsAny(p, "acaba de pasar", "ultimo evento", "ultimos eventos", "recent events", "what happened"))
            chunks.Add(QueryRecentEventsReport(""));

        return string.Join("\n", chunks.Take(4));
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        foreach (string t in terms)
        {
            if (text.Contains(t, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Resolves the Gemini thinking token budget according to user config ("no", "auto", "yes")
    /// and prompt complexity in dynamic ("auto") mode.
    /// </summary>
    private static int ResolveThinkingBudget(AppConfig cfg, string? userPrompt)
    {
        string mode = (cfg.AiThinkingMode ?? "auto").Trim().ToLowerInvariant();
        int activeBudget = string.Equals(cfg.AiVerbosity, "detailed", StringComparison.OrdinalIgnoreCase) ? 2048 : 1024;
        return mode switch
        {
            "no" => 0,
            "yes" => activeBudget,
            _ => IsComplexPrompt(userPrompt) ? activeBudget : 0
        };
    }

    internal static bool IsComplexPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        string p = prompt.Trim();
        if (p.Length >= 95)
            return true;

        int qMarks = 0;
        foreach (char c in p)
        {
            if (c == '?') qMarks++;
        }
        if (qMarks >= 2)
            return true;

        return ContainsAny(p,
            "compara", "estrategia", "recomienda", "optimiza", "planifica", "calcula",
            "por qué", "por que", "explica", "analiza", "ventajas", "desventajas",
            "mejor build", "configuración", "compare", "strategy", "recommend",
            "optimize", "plan", "explain", "analyze", "why");
    }

    private static bool SupportsGeminiThinking(string model)
    {
        string m = (model ?? "").Trim().ToLowerInvariant();
        return m.Contains("2.5") || m.Contains("flash-lite-latest") || m.Contains("flash-latest") || m.Contains("pro-latest") || m.Contains("gemini-3");
    }

    private static int GetVerbosityMaxTokens(AppConfig cfg) =>
        (cfg.AiVerbosity ?? "normal").Trim().ToLowerInvariant() switch
        {
            "brief" => 320,
            "detailed" => 1100,
            _ => 600
        };

    private static Dictionary<string, object> BuildGeminiGenerationConfig(AppConfig cfg, string model, int thinkingBudget, bool includeThinking = true)
    {
        int baseMax = GetVerbosityMaxTokens(cfg);
        var gen = new Dictionary<string, object>
        {
            ["temperature"] = 0.6,
            ["maxOutputTokens"] = thinkingBudget > 0 ? Math.Max(baseMax, thinkingBudget + 400) : baseMax
        };

        if (includeThinking && SupportsGeminiThinking(model))
        {
            string m = (model ?? "").Trim().ToLowerInvariant();
            bool isFlashLite = m.Contains("flash-lite");
            bool isPro = m.Contains("-pro") || m.Contains("pro-latest");
            bool isGemini3 = m.Contains("gemini-3");

            // Flash-Lite has thinking OFF by default and rejects explicit thinkingBudget=0 with HTTP 400.
            // Pro and Gemini 3 also reject thinkingBudget=0. Only send thinkingBudget=0 on standard gemini-2.5-flash.
            if (thinkingBudget > 0)
            {
                gen["thinkingConfig"] = new { thinkingBudget };
            }
            else if (!isFlashLite && !isPro && !isGemini3 && m.Contains("2.5-flash"))
            {
                gen["thinkingConfig"] = new { thinkingBudget = 0 };
            }
        }

        return gen;
    }

    private static string? ExtractGeminiReplyText(JsonElement root, bool preserveWhitespace = false)
    {
        if (!root.TryGetProperty("candidates", out var cands) ||
            cands.ValueKind != JsonValueKind.Array ||
            cands.GetArrayLength() == 0 ||
            !cands[0].TryGetProperty("content", out var cObj) ||
            !cObj.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? fallback = null;
        foreach (var part in parts.EnumerateArray())
        {
            if (!part.TryGetProperty("text", out var txtProp) || txtProp.ValueKind != JsonValueKind.String)
                continue;

            string raw = txtProp.GetString() ?? "";
            string text = preserveWhitespace ? raw : raw.Trim();
            if (string.IsNullOrEmpty(text))
                continue;

            bool isThought = part.TryGetProperty("thought", out var thProp) && thProp.ValueKind == JsonValueKind.True;
            if (!isThought)
                return text;

            fallback ??= text;
        }

        return fallback;
    }

}