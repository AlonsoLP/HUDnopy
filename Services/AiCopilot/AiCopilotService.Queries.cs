// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/AiCopilot/AiCopilotService.Queries.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Embedded actions, instant voice macros, ReAct 2-step loop, and analytical/remote queries.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Frozen;
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
using HUDnopy.Services.Integrations;
using static HUDnopy.Services.JsonElementExtensions;

namespace HUDnopy.Services;

public sealed partial class AiCopilotService
{
    [GeneratedRegex(@"\[{1,2}\s*QUERY\s*[:|][^\]]*\]{1,2}", RegexOptions.IgnoreCase)]
    private static partial Regex QueryTagRegex();

    [GeneratedRegex(@"\[{1,2}\s*ACTION\s*[:|][^\]]*\]{1,2}", RegexOptions.IgnoreCase)]
    private static partial Regex ActionTagRegex();

    [GeneratedRegex(@"\[{1,2}\s*ACTION\s*[:|]\s*([A-Z_]+)\s*[:|]\s*[""']?([^\]""']+)[""']?\s*\]{1,2}", RegexOptions.IgnoreCase)]
    private static partial Regex ActionCaptureRegex();

    [GeneratedRegex(@"\[{1,2}\s*QUERY\s*[:|]\s*([A-Z_]+)(?:\s*[:|]\s*[""']?([^\]""']*)[""']?)?\s*\]{1,2}", RegexOptions.IgnoreCase)]
    private static partial Regex QueryCaptureRegex();

    [GeneratedRegex(@"\[\[(?:QUERY|ACTION)[:|][^\]]*\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex DoubleBracketTagRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiWhitespaceRegex();

    internal static string StripInternalTags(string rawReply)
    {
        if (string.IsNullOrEmpty(rawReply))
            return "";
        string noQuery = QueryTagRegex().Replace(rawReply, "");
        return ActionTagRegex().Replace(noQuery, "").Trim();
    }

    private static readonly FrozenSet<string> DangerousHandsFreeBindings = new[]
    {
        "EjectAllCargo",
        "SilentRunning",
        "ToggleButtonUpInput",
        "Macro:SilentEscape"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private async Task<string> ProcessEmbeddedActionsAsync(AppConfig cfg, string rawReply, bool isHandsFree = false)
    {
        // Always strip any residual [[QUERY:...]] tags so internal syntax never leaks to chat or TTS
        string sanitized = QueryTagRegex().Replace(rawReply, "").Trim();

        if (!cfg.AiAllowActions || !sanitized.Contains("ACTION", StringComparison.OrdinalIgnoreCase))
            return StripInternalTags(sanitized);

        var matches = ActionCaptureRegex().Matches(sanitized);
        string cleanText = ActionTagRegex().Replace(sanitized, "").Trim();
        var actionLogs = new List<string>();

        foreach (Match m in matches)
        {
            string kind = m.Groups[1].Value.Trim().ToUpperInvariant();
            string param = m.Groups[2].Value.Trim();

            switch (kind)
            {
                case "BIND":
                    if (isHandsFree && DangerousHandsFreeBindings.Contains(param))
                    {
                        actionLogs.Add($"🛡 Seguridad manos libres: '{param}' bloqueado (usa Push-to-Talk o teclado para confirmar).");
                        break;
                    }
                    var (ok, msg) = await _inputSimulator.ExecuteActionAsync(param);
                    actionLogs.Add(ok ? $"⚡ {msg}" : $"⚠ {msg}");
                    break;

                case "GALNET_READ":
                    {
                        if (_galnetService.Articles.Count == 0)
                            await _galnetService.RefreshAsync(cfg.InterfaceLanguage);
                        int idx = int.TryParse(param, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n - 1 : 0;
                        if (idx >= 0 && idx < _galnetService.Articles.Count)
                        {
                            var art = _galnetService.Articles[idx];
                            string fullText = $"{art.Title}. {art.Body}";
                            if (cfg.AiSpeakReplies)
                                _speechService.SpeakCustomText(NormalizeTextForSpeech(fullText, cfg.InterfaceLanguage));
                            actionLogs.Add($"📰 GalNet #{idx + 1}: {art.Title}\n{art.Body}");
                        }
                        else
                        {
                            actionLogs.Add($"⚠ Artículo GalNet #{param} no encontrado.");
                        }
                        break;
                    }

                case "SPANSH":
                    if (!_configService.Current.EnableSpanshSystemData)
                    {
                        actionLogs.Add("⚠ Spansh está desactivado en Ajustes -> Integraciones.");
                        break;
                    }
                    SpanshServiceRequested?.Invoke(param);
                    var state = _fileWatcher.State;
                    var (sOk, results, sMsg) = await _spanshService.SearchNearestServicesAsync(param, state.CurrentSystem, state.StarPos);
                    if (sOk && results.Count > 0)
                    {
                        var top = results[0];
                        actionLogs.Add($"🛰 {top.SystemName} — {top.StationName} ({top.DistanceDisplay}, Pad {top.LandingPad})");
                    }
                    else
                    {
                        actionLogs.Add($"⚠ {sMsg}");
                    }
                    break;

                case "SPANSH_ROUTE":
                    string[] parts = param.Split(new[] { '|', ':' }, 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        SpanshRouteRequested?.Invoke(parts[0].ToLowerInvariant(), parts[1]);
                        actionLogs.Add($"🧭 Ruta Spansh ({parts[0]}) → {parts[1]} solicitada.");
                    }
                    break;

                case "SPANSH_ITEM":
                    string[] itemParts = param.Split('|', 2, StringSplitOptions.TrimEntries);
                    if (itemParts.Length >= 1 && !string.IsNullOrWhiteSpace(itemParts[0]))
                    {
                        bool lyr = itemParts.Length == 2 && bool.TryParse(itemParts[1], out bool b) && b;
                        SpanshItemRequested?.Invoke(itemParts[0], lyr);
                        actionLogs.Add($"🛰 Buscando {itemParts[0]}{(lyr ? " (Li Yong-Rui -15%)" : "")} en Spansh...");
                    }
                    break;

                case "COPY":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        CopySystemRequested?.Invoke(param);
                        actionLogs.Add($"📋 Copiado al portapapeles: {param}");
                    }
                    break;

                case "BOOKMARK":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        SurfaceBookmarkSaveRequested?.Invoke(param);
                        actionLogs.Add($"📍 Marcador de superficie solicitado: {param}");
                    }
                    break;

                case "NAV_BOOKMARK":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        SurfaceNavigateRequested?.Invoke(param);
                        actionLogs.Add($"🧭 Brújula de superficie dirigida a: {param}");
                    }
                    break;

                case "EXPORT_SHIP":
                    if (param.Equals("edsy", StringComparison.OrdinalIgnoreCase) || param.Equals("coriolis", StringComparison.OrdinalIgnoreCase))
                    {
                        ExportShipRequested?.Invoke(param.ToLowerInvariant());
                        actionLogs.Add($"🔗 Abriendo nave en {param.ToUpperInvariant()}...");
                    }
                    break;

                case "UI":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        UiNavigationRequested?.Invoke(param.ToLowerInvariant());
                        actionLogs.Add($"🖥 Vista HUD: {param}");
                    }
                    break;

                case "SPANSH_TRADE":
                    SpanshTradeRequested?.Invoke();
                    actionLogs.Add("💰 Planificando ruta comercial en Spansh...");
                    break;

                case "SPANSH_MINING":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        SpanshMiningRequested?.Invoke(param);
                        actionLogs.Add($"⛏ Buscando anillos prístinos de {param} en Spansh...");
                    }
                    break;

                case "COLONISATION_SUPPLIER":
                    ColonisationSupplierRequested?.Invoke();
                    actionLogs.Add("🏗 Buscando proveedor cercano de colonización en Spansh...");
                    break;

                case "WISHLIST_ADD":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        WishlistAddRequested?.Invoke(param);
                        actionLogs.Add($"★ Plano añadido a Wishlist: {param}");
                    }
                    break;

                case "WISHLIST_CLEAR":
                    _configService.Current.EngineeringWishlistBlueprints?.Clear();
                    _configService.Save();
                    WishlistAddRequested?.Invoke("__CLEAR__");
                    actionLogs.Add("★ Lista de deseos de ingeniería vaciada.");
                    break;

                case "PIP_PRESET":
                    {
                        string preset = (param ?? "balanced").Trim().ToLowerInvariant();
                        string[] seq = preset switch
                        {
                            "combat" or "combate" or "attack" => ["ResetPowerDistribution", "IncreaseWeaponsPower", "IncreaseWeaponsPower", "IncreaseSystemsPower", "IncreaseWeaponsPower"],
                            "tank" or "shields" or "escudos" => ["ResetPowerDistribution", "IncreaseSystemsPower", "IncreaseSystemsPower", "IncreaseWeaponsPower", "IncreaseSystemsPower"],
                            "escape" or "huida" or "flee" => ["ResetPowerDistribution", "IncreaseSystemsPower", "IncreaseSystemsPower", "IncreaseEnginesPower", "IncreaseEnginesPower"],
                            "explore" or "exploracion" or "speed" => ["ResetPowerDistribution", "IncreaseEnginesPower", "IncreaseEnginesPower", "IncreaseSystemsPower", "IncreaseEnginesPower"],
                            "mining" or "mineria" => ["ResetPowerDistribution", "IncreaseWeaponsPower", "IncreaseWeaponsPower", "IncreaseEnginesPower", "IncreaseWeaponsPower"],
                            _ => ["ResetPowerDistribution"]
                        };
                        foreach (string step in seq)
                        {
                            await _inputSimulator.ExecuteActionAsync(step);
                            await Task.Delay(65);
                        }
                        actionLogs.Add($"⚡ Pips configurados: {preset}");
                        break;
                    }

                case "MINING_FILTER":
                    {
                        string[] mfParts = param.Split('|', 2, StringSplitOptions.TrimEntries);
                        if (mfParts.Length >= 1 && !string.IsNullOrWhiteSpace(mfParts[0]))
                            _configService.Current.MiningTargetMinerals = mfParts[0];
                        if (mfParts.Length == 2 && int.TryParse(mfParts[1].TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int minPct))
                            _configService.Current.MiningMinPercent = Math.Clamp(minPct, 1, 100);
                        _configService.Save();
                        actionLogs.Add($"⛏ Filtro minería: {_configService.Current.MiningTargetMinerals} (≥{_configService.Current.MiningMinPercent}%)");
                        break;
                    }

                case "RESET_SESSION":
                    {
                        string targetSess = (param ?? "").Trim().ToLowerInvariant();
                        if (targetSess is "mining" or "mineria")
                        {
                            _fileWatcher.ResetMiningSession();
                            actionLogs.Add("⛏ Sesión de minería reiniciada.");
                        }
                        else
                        {
                            _fileWatcher.ResetCombatSession();
                            actionLogs.Add("⚔ Sesión de combate reiniciada.");
                        }
                        break;
                    }

                case "SURFACE_TARGET":
                    {
                        string[] coordParts = param.Split(new[] { '|', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (coordParts.Length >= 2 &&
                            double.TryParse(coordParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                            double.TryParse(coordParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) &&
                            lat is >= -90 and <= 90 && lon is >= -180 and <= 180)
                        {
                            string latS = lat.ToString("0.0000", CultureInfo.InvariantCulture);
                            string lonS = lon.ToString("0.0000", CultureInfo.InvariantCulture);
                            SurfaceNavigateRequested?.Invoke($"COORDS:{latS}|{lonS}");
                            actionLogs.Add($"🧭 Brújula de superficie fijada en {latS}°, {lonS}°");
                        }
                        break;
                    }

                case "REMEMBER":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        cfg.AiCommanderMemory ??= new List<string>();
                        if (!cfg.AiCommanderMemory.Contains(param, StringComparer.OrdinalIgnoreCase))
                        {
                            cfg.AiCommanderMemory.Add(param);
                            if (cfg.AiCommanderMemory.Count > 20)
                                cfg.AiCommanderMemory.RemoveAt(0);
                            _configService.Save();
                            MemoryChanged?.Invoke();
                        }
                        actionLogs.Add($"🧠 Recordado: {param}");
                    }
                    break;

                case "FORGET":
                    if (!string.IsNullOrWhiteSpace(param) && cfg.AiCommanderMemory is { Count: > 0 })
                    {
                        int removed = cfg.AiCommanderMemory.RemoveAll(m => m.Contains(param, StringComparison.OrdinalIgnoreCase));
                        if (removed > 0)
                        {
                            _configService.Save();
                            MemoryChanged?.Invoke();
                            actionLogs.Add($"🧹 Olvidado ({removed}): {param}");
                        }
                    }
                    break;

                case "TIMER":
                    string[] tParts = param.Split('|', 2, StringSplitOptions.TrimEntries);
                    if (tParts.Length >= 1 && double.TryParse(tParts[0].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double mins) && mins is > 0 and <= 180)
                    {
                        string reason = tParts.Length == 2 && !string.IsNullOrWhiteSpace(tParts[1]) ? tParts[1] : "Temporizador completado";
                        ScheduleVoiceTimer(TimeSpan.FromMinutes(mins), reason, cfg.InterfaceLanguage);
                        actionLogs.Add($"⏱ Temporizador programado: {mins:0.#} min — {reason}");
                    }
                    break;

                case "TRITIUM_CALC":
                    if (double.TryParse(param.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double ly) && ly > 0)
                    {
                        TritiumCalcRequested?.Invoke(ly);
                        actionLogs.Add($"⛽ Calculadora de tritio actualizada: {ly:N0} AL");
                    }
                    break;

                case "VOICE_RATE":
                    if (int.TryParse(param, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delta))
                    {
                        VoiceRateAdjustRequested?.Invoke(delta);
                        actionLogs.Add($"🔊 Velocidad de voz ajustada ({(delta > 0 ? $"+{delta}" : delta.ToString())}).");
                    }
                    break;

                case "DELAY":
                    if (int.TryParse(param, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms))
                        await Task.Delay(Math.Clamp(ms, 100, 5000));
                    break;

                case "NOTE":
                    string sys = _fileWatcher.State.CurrentSystem;
                    if (!string.IsNullOrWhiteSpace(sys) && !string.IsNullOrWhiteSpace(param))
                    {
                        _configService.Current.SystemNotes[sys] = param;
                        _configService.Save();
                        _fileWatcher.SyncCurrentSystemNote();
                        if (_configService.Current.EnableEdsmSync)
                            _ = _communitySync.SetEdsmSystemCommentAsync(sys, param);
                        actionLogs.Add($"📝 Nota guardada en {sys}.");
                    }
                    break;

                case "DISCORD":
                    if (_discord is not null && !string.IsNullOrWhiteSpace(param))
                    {
                        var (dOk, dMsg) = await _discord.SendWebhookAsync("🛰 COVAS Report", param);
                        actionLogs.Add(dOk ? $"📨 {dMsg}" : $"⚠ {dMsg}");
                    }
                    break;

                case "DISCORD_SUMMARY":
                    if (_discord is not null)
                    {
                        var st = _fileWatcher.State;
                        string mode = (param ?? "session").Trim().ToLowerInvariant();
                        string title = mode == "ship" ? $"🚀 {st.CommanderName} — Ship Status" : $"📊 {st.CommanderName} — Session Summary";
                        string body = mode == "ship"
                            ? $"**Ship:** {st.ShipType} ({st.ShipName} / {st.ShipIdent})\n**System:** `{st.CurrentSystem}`{(string.IsNullOrWhiteSpace(st.StationName) ? "" : $" — {st.StationName}")}\n**Hull:** {st.HullHealth:0}% | **Fuel:** {st.FuelMain:0.0}/{st.FuelMax:0.0} t | **Cargo:** {st.CargoUsed}/{st.CargoCapacity} t | **Credits:** {st.Credits:N0} CR"
                            : $"**System:** `{st.CurrentSystem}` | **Ship:** {st.ShipType}\n**Net Credits:** {st.SessionStats.NetCredits:+#,0;-#,0;0} CR ({st.SessionStats.CreditsPerHour:N0} CR/h)\n**Systems:** {st.SessionStats.SystemsVisited} ({st.SessionStats.TotalDistanceLy:0.0} LY) | **Scans:** {st.SessionStats.BodiesScanned} | **Exobiology:** {st.SessionStats.OrganicsAnalysed} | **Missions:** {st.SessionStats.MissionsCompleted}";
                        var (dsOk, dsMsg) = await _discord.SendWebhookAsync(title, body);
                        actionLogs.Add(dsOk ? $"📨 {dsMsg}" : $"⚠ {dsMsg}");
                    }
                    break;

                case "DISCORD_EXPEDITION":
                    if (_discord is not null)
                    {
                        var exp = await ExpeditionAnalyticsService.AnalyzeFolderAsync(_configService.Current.JournalFolderPath, maxFiles: 100);
                        string body = $"**Journals:** {exp.JournalsScanned:N0} | **Jumps:** {exp.TotalJumps:N0} ({exp.TotalDistanceLy:N1} LY)\n**Unique Systems:** {exp.UniqueSystems:N0} | **ELW/WW/AW:** {exp.EarthLikeWorlds}/{exp.WaterWorlds}/{exp.AmmoniaWorlds}\n**Exobiology:** {exp.OrganicsAnalysed:N0} | **Missions:** {exp.MissionsCompleted:N0} | **Earnings:** {exp.TotalBountiesAndSalesCredits:N0} CR";
                        var (deOk, deMsg) = await _discord.SendWebhookAsync($"🧭 {_fileWatcher.State.CommanderName} — Expedition Report", body, 0x3498DB);
                        actionLogs.Add(deOk ? $"📨 {deMsg}" : $"⚠ {deMsg}");
                    }
                    break;

                case "DISCORD_SCREENSHOT":
                    if (_discord is not null)
                    {
                        byte[]? shot = CapturePrimaryScreenJpeg(maxWidth: 1600, jpegQuality: 84);
                        if (shot is { Length: > 0 })
                        {
                            var st = _fileWatcher.State;
                            string caption = string.IsNullOrWhiteSpace(param) || param.Equals("now", StringComparison.OrdinalIgnoreCase)
                                ? ""
                                : $"{param}\n\n";
                            string loc = !string.IsNullOrWhiteSpace(st.BodyName) ? $"{st.CurrentSystem} · {st.BodyName}" : st.CurrentSystem;
                            string desc = $"{caption}**System:** `{loc}` | **Ship:** {st.ShipType} ({st.ShipName}) | **CMDR:** {st.CommanderName}";
                            var (ssOk, ssMsg) = await _discord.SendWebhookWithImageAsync($"📸 {st.CommanderName} — Cockpit Capture", desc, shot);
                            actionLogs.Add(ssOk ? $"📸 {ssMsg}" : $"⚠ {ssMsg}");
                        }
                        else
                        {
                            actionLogs.Add("⚠ No se pudo capturar la pantalla para Discord.");
                        }
                    }
                    break;

                case "DISCORD_CARRIER":
                    if (_discord is not null)
                    {
                        var st = _fileWatcher.State;
                        if (st.Carrier is { } fc)
                        {
                            string depLine = fc.DepartureTime.HasValue
                                ? $"\n**Next Jump:** `{fc.Destination}` (<t:{fc.DepartureTime.Value.ToUnixTimeSeconds()}:R>)"
                                : "";
                            string svcs = st.CarrierServices.Count > 0 ? $"\n**Services:** {string.Join(", ", st.CarrierServices)}" : "";
                            string body = $"**System:** `{fc.System}` | **Tritium:** {fc.Fuel?.ToString() ?? "?"} t\n**Free Space:** {fc.FreeSpace?.ToString("N0") ?? "?"}/{fc.Capacity?.ToString("N0") ?? "?"} t | **Balance:** {fc.Balance?.ToString("N0") ?? "?"} CR{depLine}{svcs}";
                            var (dcOk, dcMsg) = await _discord.SendWebhookAsync($"🚢 {fc.Name} [{fc.Callsign}] — Carrier Status", body, 0xFF7100);
                            actionLogs.Add(dcOk ? $"📨 {dcMsg}" : $"⚠ {dcMsg}");
                        }
                        else
                        {
                            actionLogs.Add("⚠ Sin datos de Fleet Carrier para publicar en Discord.");
                        }
                    }
                    break;

                case "DISCORD_BUILD":
                    if (_discord is not null)
                    {
                        var st = _fileWatcher.State;
                        string links = !string.IsNullOrWhiteSpace(st.LastLoadoutJson)
                            ? $"\n🔗 **[Open in EDSY]({ShipLoadoutExportService.BuildEdsyUrl(st.LastLoadoutJson)})** · **[Open in Coriolis]({ShipLoadoutExportService.BuildCoriolisUrl(st.LastLoadoutJson)})**"
                            : "";
                        string body = $"**Ship:** {st.ShipType} ({st.ShipName} / {st.ShipIdent})\n**Hull:** {st.HullHealth:0}% | **Fuel:** {st.FuelMax:0.0} t | **Cargo:** {st.CargoCapacity} t{links}";
                        var (dbOk, dbMsg) = await _discord.SendWebhookAsync($"🛠 {st.CommanderName} — Ship Loadout", body, 0x9B59B6);
                        actionLogs.Add(dbOk ? $"📨 {dbMsg}" : $"⚠ {dbMsg}");
                    }
                    break;

                case "ALERTS_CLEAR":
                    if (_alertService is not null)
                    {
                        int cleared = _alertService.DismissAllActiveAlerts();
                        _speechService.StopSpeaking();
                        actionLogs.Add($"🔕 Alertas activas limpiadas ({cleared}).");
                    }
                    break;

                case "HUD_MODE":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        UiNavigationRequested?.Invoke(param.Trim().ToLowerInvariant());
                        actionLogs.Add($"🖥 Modo HUD: {param.Trim()}");
                    }
                    break;

                case "BGS_WATCH_ADD":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        var list = EliteBgsWatchlistService.ParseWatchedFactionNames(cfg.EliteBgsWatchedFactions);
                        if (!list.Contains(param.Trim(), StringComparer.OrdinalIgnoreCase))
                        {
                            list.Add(param.Trim());
                            cfg.EliteBgsWatchedFactions = string.Join(", ", list);
                            cfg.EnableEliteBgsWatchlist = true;
                            _configService.Save();
                        }
                        if (_eliteBgsWatchlist is not null)
                            await _eliteBgsWatchlist.RefreshAsync();
                        actionLogs.Add($"🏛 Facción añadida a EliteBGS Watchlist: {param.Trim()}");
                    }
                    break;

                case "BGS_WATCH_REMOVE":
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        var list = EliteBgsWatchlistService.ParseWatchedFactionNames(cfg.EliteBgsWatchedFactions);
                        int removed = list.RemoveAll(f => f.Contains(param.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (removed > 0)
                        {
                            cfg.EliteBgsWatchedFactions = string.Join(", ", list);
                            _configService.Save();
                            if (_eliteBgsWatchlist is not null)
                                await _eliteBgsWatchlist.RefreshAsync();
                            actionLogs.Add($"🏛 Eliminada de EliteBGS Watchlist ({removed}): {param.Trim()}");
                        }
                    }
                    break;

                case "INARA_OPEN":
                    {
                        string[] iParts = param.Split('|', 2, StringSplitOptions.TrimEntries);
                        string iKind = iParts.Length >= 1 ? iParts[0] : "system";
                        string iTarget = iParts.Length == 2 && !string.IsNullOrWhiteSpace(iParts[1])
                            ? iParts[1]
                            : _fileWatcher.State.CurrentSystem;
                        if (!string.IsNullOrWhiteSpace(iTarget))
                        {
                            string url = CommunitySyncService.BuildInaraDeepLink(iKind, iTarget);
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
                                actionLogs.Add($"🌐 Inara abierto ({iKind}): {iTarget}");
                            }
                            catch (Exception ex)
                            {
                                AppDiagnosticsLogger.Warn("AI.Inara", $"Failed to launch Inara deep link: {ex.Message}");
                                actionLogs.Add($"⚠ No se pudo abrir Inara: {ex.Message}");
                            }
                        }
                        break;
                    }

                case "CAPI_SYNC":
                    if (_frontierCapi is not null)
                    {
                        var (cOk, cMsg) = await _frontierCapi.SyncAllAsync(includeDockedEndpoints: _fileWatcher.State.Docked);
                        actionLogs.Add(cOk ? $"🔄 {cMsg}" : $"⚠ {cMsg}");
                    }
                    break;

                case "CAPI_JOURNAL":
                    if (_frontierCapi is not null)
                    {
                        var (jOk, _, jMsg) = await _frontierCapi.FetchJournalHistoryAsync();
                        actionLogs.Add(jOk ? $"📜 {jMsg}" : $"⚠ {jMsg}");
                    }
                    break;

                case "BGS_REFRESH":
                    if (_eliteBgsWatchlist is not null)
                    {
                        var (bOk, _, bMsg) = await _eliteBgsWatchlist.RefreshAsync();
                        actionLogs.Add(bOk ? $"🏛 {bMsg}" : $"⚠ {bMsg}");
                    }
                    break;

                case "DSSA_FIND":
                    if (_edastroCanonn is not null)
                    {
                        var (dsOk, carriers, dsMsg) = await _edastroCanonn.GetNearestDssaCarriersAsync();
                        if (dsOk && carriers.Count > 0)
                        {
                            var top = carriers[0];
                            CopySystemRequested?.Invoke(top.SystemName);
                            actionLogs.Add($"🛰 DSSA más cercano: {top.Callsign} {top.Name} en {top.SystemName} ({top.DistanceDisplay})");
                        }
                        else
                        {
                            actionLogs.Add($"⚠ {dsMsg}");
                        }
                    }
                    break;

                case "ARDENT_SEARCH":
                    if (_ardentMarket is not null)
                    {
                        string[] aParts = param.Split('|', 2, StringSplitOptions.TrimEntries);
                        string comm = aParts.Length > 0 ? aParts[0] : "";
                        string mode = aParts.Length > 1 && aParts[1].Equals("buy", StringComparison.OrdinalIgnoreCase) ? "buy" : "sell";
                        var (aOk, offers, aMsg) = await _ardentMarket.SearchCommodityAsync(comm, mode);
                        if (aOk && offers.Count > 0)
                        {
                            var best = offers[0];
                            actionLogs.Add($"💰 Ardent ({mode}): {best.StationName} en {best.SystemName} — {best.PriceDisplay} ({best.DistanceDisplay}, Pad {best.MaxLandingPad})");
                        }
                        else
                        {
                            actionLogs.Add($"⚠ {aMsg}");
                        }
                    }
                    break;

                case "GALAXY_REFRESH":
                    {
                        var snap = await _galaxyStatus.RefreshAsync();
                        actionLogs.Add($"🌌 Servidor: {snap.ServerMessage} | BGS Tick: {(snap.LastBgsTickUtc?.ToString("yyyy-MM-dd HH:mm") ?? "?")}Z");
                        break;
                    }

                case "TOGGLE_INTEGRATION":
                    {
                        string[] tgParts = param.Split('|', 2, StringSplitOptions.TrimEntries);
                        if (tgParts.Length == 2)
                        {
                            string api = tgParts[0].ToLowerInvariant();
                            bool enable = tgParts[1].Equals("on", StringComparison.OrdinalIgnoreCase) || tgParts[1].Equals("true", StringComparison.OrdinalIgnoreCase);
                            string toggleMsg = ApplyIntegrationToggle(cfg, api, enable);
                            actionLogs.Add(toggleMsg);
                        }
                        break;
                    }
            }
        }

        if (actionLogs.Count == 0)
            return cleanText;

        return string.IsNullOrWhiteSpace(cleanText)
            ? string.Join("\n", actionLogs)
            : $"{cleanText}\n\n{string.Join("\n", actionLogs)}";
    }

    // =========================================================================
    // Dynamic Bindings, Zero-LLM Voice Macros & 2-Step ReAct Live Queries
    // =========================================================================

    /// <summary>
    /// Builds a compact list of keyboard-bound actions and macros relevant to the active vehicle mode
    /// (Main Ship, SRV, Fighter, or On-Foot) from the commander's live .binds profile.
    /// </summary>
    private string BuildDynamicBindingsPromptList(ShipState s)
    {
        var all = _bindingsService.Bindings;
        if (all is null || all.Count == 0)
            return "LandingGearToggle, ShipSpotLightToggle, ToggleCargoScoop, DeployHardpointToggle, NightVisionToggle";

        IEnumerable<GameBinding> filtered = all.Where(static b =>
            b.HasKeyboardBind || string.Equals(b.PrimaryDevice, "Macro", StringComparison.OrdinalIgnoreCase));

        if (s.OnFoot)
        {
            filtered = filtered.Where(static b =>
                b.Category.Contains("Pie", StringComparison.OrdinalIgnoreCase) ||
                b.Category.Contains("Macro", StringComparison.OrdinalIgnoreCase) ||
                b.ActionId.StartsWith("Humanoid", StringComparison.OrdinalIgnoreCase));
        }
        else if (s.InSRV)
        {
            filtered = filtered.Where(static b =>
                b.Category.Contains("SRV", StringComparison.OrdinalIgnoreCase) ||
                b.Category.Contains("Paneles", StringComparison.OrdinalIgnoreCase) ||
                b.Category.Contains("Macro", StringComparison.OrdinalIgnoreCase));
        }
        else if (s.InFighter)
        {
            filtered = filtered.Where(static b =>
                b.Category.Contains("Combate", StringComparison.OrdinalIgnoreCase) ||
                b.Category.Contains("Energía", StringComparison.OrdinalIgnoreCase) ||
                b.Category.Contains("Caza", StringComparison.OrdinalIgnoreCase) ||
                b.Category.Contains("Macro", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            filtered = filtered.Where(static b =>
                !b.Category.Contains("Pie", StringComparison.OrdinalIgnoreCase) &&
                !b.Category.Contains("SRV", StringComparison.OrdinalIgnoreCase) &&
                !b.Category.Contains("Otros", StringComparison.OrdinalIgnoreCase));
        }

        var ids = filtered.Select(static b => b.ActionId).Distinct(StringComparer.OrdinalIgnoreCase).Take(45).ToList();
        return ids.Count > 0 ? string.Join(", ", ids) : "LandingGearToggle, ShipSpotLightToggle, ToggleCargoScoop";
    }

    /// <summary>
    /// Checks whether the commander's input matches an instant zero-LLM voice macro and executes it immediately.
    /// </summary>
    private async Task<(bool Matched, string Reply)> TryExecuteVoiceMacroAsync(AppConfig cfg, string rawPrompt)
    {
        if (cfg.AiVoiceMacros is not { Count: > 0 } || string.IsNullOrWhiteSpace(rawPrompt))
            return (false, "");

        string normalizedInput = NormalizeVoiceMatchText(rawPrompt, cfg.AiWakeWord);
        if (string.IsNullOrWhiteSpace(normalizedInput))
            return (false, "");

        VoiceMacroDefinition? match = null;
        foreach (var macro in cfg.AiVoiceMacros)
        {
            if (string.IsNullOrWhiteSpace(macro.TriggerPhrase) || string.IsNullOrWhiteSpace(macro.Steps))
                continue;

            var aliases = macro.TriggerPhrase.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string alias in aliases)
            {
                string normAlias = NormalizeVoiceMatchText(alias, null);
                if (string.IsNullOrWhiteSpace(normAlias))
                    continue;

                if (normalizedInput.Equals(normAlias, StringComparison.Ordinal) ||
                    normalizedInput.StartsWith(normAlias + " ", StringComparison.Ordinal) ||
                    normalizedInput.EndsWith(" " + normAlias, StringComparison.Ordinal))
                {
                    match = macro;
                    break;
                }

                // Fuzzy tolerance for minor STT phonetic/spelling slips (Levenshtein distance)
                int maxDist = normAlias.Length >= 10 ? 2 : (normAlias.Length >= 5 ? 1 : 0);
                if (maxDist > 0 && Math.Abs(normalizedInput.Length - normAlias.Length) <= maxDist &&
                    ComputeLevenshteinDistance(normalizedInput, normAlias) <= maxDist)
                {
                    match = macro;
                    break;
                }
            }
            if (match is not null)
                break;
        }

        if (match is null)
            return (false, "");

        var steps = match.Steps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var logs = new List<string>();
        foreach (string step in steps)
        {
            if (step.StartsWith("DELAY:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(step["DELAY:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms))
                    await Task.Delay(Math.Clamp(ms, 50, 5000));
                continue;
            }

            var (ok, msg) = await _inputSimulator.ExecuteActionAsync(step);
            logs.Add(ok ? $"⚡ {msg}" : $"⚠ {msg}");
            await Task.Delay(75);
        }

        string spoken = !string.IsNullOrWhiteSpace(match.SpokenConfirmation)
            ? match.SpokenConfirmation.Trim()
            : $"Macro ejecutada: {match.TriggerPhrase.Split(',')[0].Trim()}.";

        if (cfg.AiSpeakReplies)
            _speechService.SpeakCustomText(NormalizeTextForSpeech(spoken, cfg.InterfaceLanguage));

        string reply = logs.Count > 0 ? $"{spoken}\n\n{string.Join("\n", logs)}" : spoken;
        return (true, reply);
    }

    internal static string NormalizeVoiceMatchText(string text, string? wakeWord)
    {
        string s = text.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(wakeWord))
        {
            string w = wakeWord.Trim().ToLowerInvariant();
            if (s.StartsWith(w + " ", StringComparison.Ordinal) || s.StartsWith(w + ",", StringComparison.Ordinal))
                s = s[w.Length..].TrimStart(' ', ',', ':', ';', '-', '¡', '!');
        }

        // Remove diacritics and punctuation for robust Spanish/multilingual voice matching
        string formD = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (char c in formD)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
                sb.Append(c);
            else
                sb.Append(' ');
        }
        string cleaned = MultiWhitespaceRegex().Replace(sb.ToString(), " ").Trim();
        // Strip common leading conversational fillers so voice macros & instant queries match reliably
        string[] leadingFillers = ["por favor ", "oye ", "eh ", "please ", "hey ", "ok ", "okay ", "bitte ", "dis ", "ei "];
        bool stripped;
        do
        {
            stripped = false;
            foreach (string filler in leadingFillers)
            {
                if (cleaned.StartsWith(filler, StringComparison.Ordinal))
                {
                    cleaned = cleaned[filler.Length..].TrimStart();
                    stripped = true;
                }
            }
        } while (stripped);
        return cleaned;
    }

    internal static int ComputeLevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        int n = a.Length, m = b.Length;
        int[] prev = new int[m + 1];
        int[] curr = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }

    /// <summary>
    /// Answers ultra-frequent single-stat cockpit voice queries in 0 ms / 0 LLM tokens.
    /// </summary>
    private (bool Matched, string Reply) TryAnswerInstantTelemetryQuery(AppConfig cfg, string rawPrompt)
    {
        string norm = NormalizeVoiceMatchText(rawPrompt, cfg.AiWakeWord);
        if (string.IsNullOrWhiteSpace(norm))
            return (false, "");

        int wordCount = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 6)
            return (false, "");

        var s = _fileWatcher.State;
        bool isEs = string.Equals(cfg.InterfaceLanguage, "es-ES", StringComparison.OrdinalIgnoreCase);

        if (norm is "combustible" or "nivel de combustible" or "cuanto combustible" or "cuanto combustible tengo"
            or "fuel" or "fuel level" or "how much fuel" or "how much fuel left")
        {
            if (s.FuelMax <= 0) return (false, "");
            double pct = Math.Clamp((s.FuelMain / s.FuelMax) * 100.0, 0, 100);
            string msg = isEs
                ? $"Combustible al {pct:0} por ciento, {s.FuelMain:0.0} de {s.FuelMax:0.0} toneladas."
                : $"Fuel is at {pct:0} percent, {s.FuelMain:0.0} of {s.FuelMax:0.0} tons.";
            return (true, msg);
        }

        if (norm is "casco" or "estado del casco" or "como esta el casco" or "integridad del casco"
            or "hull" or "hull status" or "hull integrity")
        {
            string shields = s.ShieldsUp ? (isEs ? "escudos activos" : "shields online") : (isEs ? "escudos caídos" : "shields offline");
            string msg = isEs
                ? $"Casco al {s.HullHealth:0} por ciento, {shields}."
                : $"Hull integrity at {s.HullHealth:0} percent, {shields}.";
            return (true, msg);
        }

        if (norm is "plataforma" or "que plataforma" or "que plataforma tengo" or "donde aterrizo"
            or "landing pad" or "which landing pad" or "where do i land")
        {
            if (s.AssignedLandingPad is int pad && pad > 0)
            {
                var padInfo = LandingPadLocatorService.Resolve(pad, s.AssignedLandingStationType);
                return (true, padInfo.FormatGuidance(cfg.InterfaceLanguage));
            }
            return (true, isEs ? "No hay plataforma de aterrizaje asignada." : "No landing pad currently assigned.");
        }

        if (norm is "saltos" or "cuantos saltos" or "cuantos saltos quedan" or "saltos restantes"
            or "jumps" or "jumps left" or "jumps remaining" or "how many jumps left")
        {
            int curIdx = s.NavRoute.FindIndex(static w => w.IsCurrentSystem);
            int rem = Math.Max(0, s.NavRoute.Count - 1 - Math.Max(0, curIdx));
            if (rem == 0)
                return (true, isEs ? "No hay ruta de navegación activa." : "No active navigation route.");
            string fuelNote = s.RouteHasNoFuelStar
                ? (isEs ? " Atención: sin estrellas repostables en la ruta." : " Warning: no scoopable stars on route.")
                : "";
            string msg = isEs
                ? $"Quedan {rem} saltos en la ruta.{fuelNote}"
                : $"{rem} jumps remaining on route.{fuelNote}";
            return (true, msg);
        }

        return (false, "");
    }

    /// <summary>
    /// Executes up to 3 read-only [[QUERY:...]] requests emitted by the LLM and performs one follow-up turn
    /// so COVAS can reason over real live data and give a natural spoken answer.
    /// </summary>
    private async Task<string> ResolveQueriesAndFollowUpAsync(
        AppConfig cfg,
        string systemPrompt,
        List<(string Role, string Content)> baseMessages,
        string firstReply,
        SentenceStreamSpeaker? streamSpeaker = null,
        CancellationToken ct = default)
    {
        var matches = QueryCaptureRegex().Matches(firstReply)
            .Cast<Match>()
            .Take(3)
            .ToList();

        if (matches.Count == 0)
            return QueryTagRegex().Replace(firstReply, "").Trim();

        var tasks = matches.Select(m => ExecuteQueryAsync(
            m.Groups[1].Value.Trim().ToUpperInvariant(),
            m.Groups[2].Success ? m.Groups[2].Value.Trim() : "")).ToArray();

        string[] results = await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();

        var sb = new StringBuilder();
        sb.AppendLine("[RESULTADOS DE CONSULTA EN VIVO]");
        for (int i = 0; i < matches.Count; i++)
            sb.AppendLine($"- {matches[i].Groups[1].Value.ToUpperInvariant()}({(matches[i].Groups[2].Success ? matches[i].Groups[2].Value.Trim() : "")}): {results[i]}");
        sb.AppendLine("Responde ahora directamente al comandante usando estos datos exactos, sin emitir nuevas etiquetas [[QUERY:...]].");

        var followUpMessages = new List<(string Role, string Content)>(baseMessages)
        {
            ("assistant", firstReply),
            ("user", sb.ToString())
        };

        var (ok, secondReply) = await SendProviderRequestAsync(cfg, systemPrompt, followUpMessages, thinkingBudget: 0, streamSpeaker: streamSpeaker, ct: ct);
        if (!ok || string.IsNullOrWhiteSpace(secondReply))
            return string.Join("\n", results);

        return QueryTagRegex().Replace(secondReply, "").Trim();
    }

    private static readonly FrozenSet<string> CacheableRemoteQueries = new[]
    {
        "SYSTEM", "EDSM_TRAFFIC", "EDSM_DISTANCE", "EDSM_DISCOVERY", "EDSM_STATIONS", "EDSM_NEARBY",
        "SPANSH", "SPANSH_BODIES", "SPANSH_CARRIERS", "SPANSH_TRADE", "SPANSH_MINING",
        "ARDENT", "CARGO_BEST_SELL", "DSSA", "BGS", "INARA", "COMMUNITY_GOALS"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private async Task<string> ExecuteQueryAsync(string kind, string param)
    {
        bool isRemote = CacheableRemoteQueries.Contains(kind);
        string cacheKey = $"{kind}|{param.Trim()}|{_fileWatcher.State.CurrentSystem}";
        if (isRemote)
        {
            lock (_queryCache)
            {
                if (_queryCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow)
                    return cached.Result;
            }
        }

        try
        {
            string result = kind switch
            {
                "MATERIAL" => QueryMaterialInventory(param),
                "EXPLORATION" => QueryExplorationReport(param),
                "MISSIONS" => QueryMissionsReport(param),
                "REPORT" => QueryOperationalReport(param),
                "PROFILE" => QueryProfileReport(param),
                "FLEET" => QueryFleetReport(param),
                "CARGO_PROFIT" => QueryCargoProfitReport(),
                "WISHLIST" => QueryWishlistReport(),
                "SURFACE" => QuerySurfaceReport(),
                "PASSENGERS" => QueryPassengersReport(),
                "SERVER" => QueryServerAndIntegrationsStatus(),
                "ALERTS" => QueryActiveAlertsReport(),
                "LANDING_PAD" => QueryLandingPadReport(),
                "FUEL_EMERGENCY" => await QueryFuelEmergencyAsync(),
                "EXPEDITION" => await QueryExpeditionSummaryAsync(),
                "WEB_HUD" => QueryWebHudStatus(),
                "COMMUNITY_GOALS" => await QueryCommunityGoalsAsync(),
                "SPANSH_SYSTEM" => QuerySpanshSystemDump(),
                "SYSTEM" => await QueryEdsmSystemAsync(param),
                "EDSM_TRAFFIC" => await QueryEdsmTrafficAsync(param),
                "EDSM_DISTANCE" => await QueryEdsmDistanceAsync(param),
                "EDSM_DISCOVERY" => await QueryEdsmDiscoveryAsync(param),
                "EDSM_STATIONS" => await QueryEdsmStationsAsync(param),
                "EDSM_NEARBY" => await QueryEdsmNearbySystemsAsync(param),
                "SPANSH" => await QuerySpanshNearestAsync(param),
                "SPANSH_BODIES" => await QuerySpanshBodiesAsync(param),
                "SPANSH_CARRIERS" => await QuerySpanshCarriersAsync(param),
                "SPANSH_TRADE" => await QuerySpanshTradeLiveAsync(),
                "SPANSH_MINING" => await QuerySpanshMiningLiveAsync(param),
                "ARDENT" => await QueryArdentMarketAsync(param),
                "CARGO_BEST_SELL" => await QueryCargoBestSellArdentAsync(),
                "DSSA" => await QueryDssaNearestAsync(),
                "BGS" => await QueryEliteBgsWatchlistAsync(),
                "INARA" => await QueryInaraReadAsync(param),
                "LOGBOOK" => await ExpeditionAnalyticsService.QueryLogbookAsync(_configService.Current.JournalFolderPath, param),
                "CAPI_JOURNAL" => await QueryCapiJournalHistoryAsync(param),
                "STATION" => await QueryStationSnapshotAsync(param),
                "SYNTHESIS" => QuerySynthesisReadiness(),
                "ODYSSEY_GEAR" => QueryOdysseyGearReport(),
                "EXOBIO_VALUE" => QueryExobioAndCartoValueReport(),
                "RECENT_EVENTS" => QueryRecentEventsReport(param),
                _ => $"Consulta no soportada: {kind}"
            };

            if (isRemote && !string.IsNullOrWhiteSpace(result) && !result.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            {
                lock (_queryCache)
                {
                    if (_queryCache.Count > 40)
                        _queryCache.Clear();
                    _queryCache[cacheKey] = (DateTimeOffset.UtcNow.AddSeconds(60), result);
                }
                _lastExternalQuerySummary = $"{kind}({param}): {Truncate(result, 420)}";
                _lastExternalQueryUtc = DateTimeOffset.UtcNow;
            }

            return result;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("AI.Query", $"Live query '{kind}' failed: {ex.Message}");
            return $"Error en consulta {kind}: {ex.Message}";
        }
    }

    private string QuerySynthesisReadiness() =>
        BuildSynthesisReadinessReport(_fileWatcher.State.MaterialsInventory);

    internal static string BuildSynthesisReadinessReport(IReadOnlyList<MaterialItem> inventory)
    {
        if (inventory is null || inventory.Count == 0)
            return "Inventario de materiales vacío o aún no cargado.";

        int CountOf(string name) => inventory
            .Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(m.DisplayName, name, StringComparison.OrdinalIgnoreCase))
            .Sum(static m => m.Count);

        int Craftable(params (string Mat, int Req)[] reqs) =>
            reqs.Min(r => CountOf(r.Mat) / r.Req);

        int fsdBasic = Craftable(("vanadium", 2), ("germanium", 1));
        int fsdStd = Craftable(("vanadium", 1), ("germanium", 1), ("cadmium", 2), ("niobium", 1));
        int fsdPrem = Craftable(("carbon", 1), ("germanium", 1), ("arsenic", 1), ("niobium", 3), ("yttrium", 1), ("polonium", 1));
        int lifeSupport = Craftable(("iron", 2), ("nickel", 1));
        int afmuBasic = Craftable(("nickel", 2), ("zinc", 2), ("chromium", 2), ("vanadium", 3));
        int srvRefuel = Craftable(("sulphur", 1), ("phosphorus", 1));
        int srvRepair = Craftable(("iron", 2), ("nickel", 1));
        int limpets = Craftable(("iron", 10), ("nickel", 10));

        return $"Síntesis disponibles — Inyección FSD: Básica ×{fsdBasic}, Estándar ×{fsdStd}, Premium ×{fsdPrem} | Soporte vital O2: ×{lifeSupport} | Recarga AFMU básica: ×{afmuBasic} | SRV combustible: ×{srvRefuel}, reparación: ×{srvRepair} | Drones (×4): ×{limpets}.";
    }

    private async Task<string> QueryCapiJournalHistoryAsync(string dateParam)
    {
        if (_frontierCapi is null || !_configService.Current.EnableFrontierCapi || !_frontierCapi.IsConnected)
            return "Frontier cAPI no está conectado o activado en Ajustes -> Integraciones.";

        DateTime? targetDate = null;
        if (!string.IsNullOrWhiteSpace(dateParam) &&
            !dateParam.Equals("today", StringComparison.OrdinalIgnoreCase) &&
            DateTime.TryParseExact(dateParam.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            targetDate = parsed.Date;
        }

        var (_, _, msg) = await _frontierCapi.FetchJournalHistoryAsync(targetDate);
        return msg;
    }

    private string QueryOdysseyGearReport()
    {
        var s = _fileWatcher.State;
        var bp = s.BackpackInventory ?? new List<BackpackItem>();
        string suitInfo = !string.IsNullOrWhiteSpace(s.ActiveSuit)
            ? $"Traje: {s.ActiveSuit}{(string.IsNullOrWhiteSpace(s.ActiveSuitLoadout) ? "" : $" ({s.ActiveSuitLoadout})")} | "
            : "";
        string topItems = bp.Count > 0
            ? string.Join(", ", bp.OrderByDescending(static i => i.Count).Take(8).Select(static i => $"{i.DisplayName} ×{i.Count}"))
            : "Mochila vacía";
        return $"Odyssey a pie: {suitInfo}{s.OnFootStatusDisplay} | Salud: {s.HealthDisplay} | Oxígeno: {s.OxygenDisplay} | Mochila ({bp.Sum(static i => i.Count)} ítems): {topItems}.";
    }

    private string QueryExobioAndCartoValueReport()
    {
        var s = _fileWatcher.State;
        long totalRisk = s.UnsoldCartographyCredits + s.UnsoldExobiologyCredits;
        string exoActive = s.ExobiologyTracker is { } exo
            ? $" Muestra activa: {exo.Species} en {exo.BodyName} ({exo.SamplesCollected}/3, {exo.CurrentDistanceMeters:0}/{exo.RequiredDistanceMeters} m)."
            : "";
        var topBodies = s.ExplorationBodies
            .Where(static b => (b.EstimatedCredits ?? 0) >= 250_000 || b.BiologicalSignalCount > 0)
            .OrderByDescending(static b => b.EstimatedCredits ?? 0)
            .Take(4)
            .Select(static b => $"{b.BodyName} ({b.BodyType}, {(b.EstimatedCredits ?? 0):N0} CR{(b.BiologicalSignalCount > 0 ? $", {b.BiologicalSignalCount} bio" : "")})");
        string bodiesStr = topBodies.Any() ? $" Cuerpos destacados en sistema: {string.Join(" | ", topBodies)}." : "";
        return $"Datos sin vender en riesgo: {totalRisk:N0} CR total (Cartografía: {s.UnsoldCartographyCredits:N0} CR, Exobiología: {s.UnsoldExobiologyCredits:N0} CR).{exoActive}{bodiesStr}";
    }

    private string QueryRecentEventsReport(string filter)
    {
        var events = _fileWatcher.State.RecentEvents ?? new List<JournalEntryItem>();
        if (events.Count == 0)
            return "No hay eventos recientes registrados en el diario.";

        IEnumerable<JournalEntryItem> seq = events;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            string f = filter.Trim();
            seq = seq.Where(e =>
                e.EventType.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                e.Summary.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        var list = seq.Take(8).Select(static e => $"[{e.Timestamp}] {e.EventType}: {e.Summary}").ToList();
        return list.Count > 0
            ? $"Últimos eventos del diario: {string.Join(" | ", list)}"
            : $"No hay eventos recientes que coincidan con '{filter}'.";
    }

    private async Task<string> QueryEdsmDiscoveryAsync(string systemName)
    {
        if (!_configService.Current.EnableEdsmSync)
            return "EDSM integration is disabled in Settings -> Integrations.";
        string sys = string.IsNullOrWhiteSpace(systemName) ? _fileWatcher.State.CurrentSystem : systemName.Trim();
        var intel = await _communitySync.FetchEdsmDiscoveryAndFactionsAsync(sys);
        if (intel is null)
            return $"Could not retrieve EDSM discovery intel for {sys}.";
        string factions = intel.Factions.Count > 0
            ? $" Factions: {string.Join(", ", intel.Factions.Take(3).Select(static f => $"{f.Name} {f.InfluenceDisplay} ({f.FactionState})"))}."
            : "";
        return $"{intel.SystemName}: {intel.DiscoveryBadge}.{factions}";
    }

    private async Task<string> QuerySpanshBodiesAsync(string materialQuery)
    {
        if (!_configService.Current.EnableSpanshSystemData)
            return "Spansh integration is disabled in Settings -> Integrations.";
        var st = _fileWatcher.State;
        var (ok, results, msg) = await _spanshService.SearchBodiesAsync(materialQuery, st.CurrentSystem, st.StarPos);
        if (!ok || results.Count == 0)
            return msg;
        return string.Join(" | ", results.Take(5).Select(static b => $"{b.BodyName} ({b.SystemName}, {b.DistanceDisplay}, {b.GravityDisplay}: {b.MaterialsSummary})"));
    }

    private async Task<string> QuerySpanshCarriersAsync(string service)
    {
        if (!_configService.Current.EnableSpanshSystemData)
            return "Spansh integration is disabled in Settings -> Integrations.";
        var st = _fileWatcher.State;
        var (ok, results, msg) = await _spanshService.SearchFleetCarriersAsync(service, st.CurrentSystem, st.StarPos);
        if (!ok || results.Count == 0)
            return msg;
        return string.Join(" | ", results.Take(5).Select(static c => $"{c.Callsign} in {c.SystemName} ({c.DistanceDisplay})"));
    }

    private async Task<string> QueryArdentMarketAsync(string param)
    {
        if (_ardentMarket is null || !_configService.Current.EnableArdentMarketApi)
            return "Ardent Insight Market API is disabled in Settings -> Integrations.";
        string[] parts = param.Split('|', 3, StringSplitOptions.TrimEntries);
        string commodity = parts.Length > 0 ? parts[0] : "";
        string mode = parts.Length > 1 && parts[1].Equals("buy", StringComparison.OrdinalIgnoreCase) ? "buy" : "sell";
        bool includeCarriers = parts.Length > 2 && (parts[2].Contains("carrier", StringComparison.OrdinalIgnoreCase) || parts[2].Equals("true", StringComparison.OrdinalIgnoreCase));
        var (ok, offers, msg) = await _ardentMarket.SearchCommodityAsync(commodity, mode, includeCarriers: includeCarriers);
        if (!ok || offers.Count == 0)
            return msg;
        return string.Join(" | ", offers.Take(5).Select(static o => $"{o.StationName} ({o.SystemName}, {o.DistanceDisplay}, Pad {o.MaxLandingPad}): {o.PriceDisplay} x{o.QuantityDisplay}"));
    }

    private async Task<string> QueryDssaNearestAsync()
    {
        if (_edastroCanonn is null || !_configService.Current.EnableEdastroDssa)
            return "EDAstro DSSA integration is disabled in Settings -> Integrations.";
        var (ok, carriers, msg) = await _edastroCanonn.GetNearestDssaCarriersAsync();
        if (!ok || carriers.Count == 0)
            return msg;
        return string.Join(" | ", carriers.Take(5).Select(static c => $"{c.Callsign} {c.Name} in {c.SystemName} ({c.DistanceDisplay}, {c.Region})"));
    }

    private async Task<string> QueryEliteBgsWatchlistAsync()
    {
        if (_eliteBgsWatchlist is null || !_configService.Current.EnableEliteBgsWatchlist)
            return "EliteBGS Watchlist is disabled in Settings -> Integrations.";
        var (ok, items, msg) = await _eliteBgsWatchlist.RefreshAsync();
        if (!ok || items.Count == 0)
            return msg;
        return string.Join(" | ", items.Take(8).Select(static f => $"{f.FactionName} @ {f.SystemName}: {f.InfluenceDisplay} ({f.State}{(f.HasConflict ? " CONFLICT!" : "")})"));
    }

    private async Task<string> QueryInaraReadAsync(string param)
    {
        if (!_configService.Current.EnableInaraSync)
            return "Inara integration is disabled in Settings -> Integrations.";
        if (param.Equals("cgs", StringComparison.OrdinalIgnoreCase))
        {
            var (ok, goals, msg) = await _communitySync.FetchInaraCommunityGoalsAsync();
            if (!ok || goals.Count == 0)
                return msg;
            return string.Join(" | ", goals.Take(4).Select(static g => $"{g.Title} @ {g.StarSystem} ({g.StationName})"));
        }
        var (pOk, profile, pMsg) = await _communitySync.FetchInaraCommanderProfileAsync(string.IsNullOrWhiteSpace(param) ? null : param);
        if (!pOk || profile is null)
            return pMsg;
        return $"CMDR {profile.CommanderName}, Squadron: {profile.SquadronName} ({profile.SquadronRank}), Allegiance: {profile.PreferredAllegiance}.";
    }

    private string ApplyIntegrationToggle(AppConfig cfg, string api, bool enable)
    {
        switch (api)
        {
            case "frontier":
            case "capi": cfg.EnableFrontierCapi = enable; break;
            case "eddn": cfg.EnableEddnUpload = enable; break;
            case "edsm": cfg.EnableEdsmSync = enable; break;
            case "inara": cfg.EnableInaraSync = enable; break;
            case "spansh": cfg.EnableSpanshSystemData = enable; break;
            case "bgs":
            case "elitebgs": cfg.EnableEliteBgsWatchlist = enable; break;
            case "ardent": cfg.EnableArdentMarketApi = enable; break;
            case "dssa":
            case "edastro": cfg.EnableEdastroDssa = enable; break;
            case "canonn": cfg.EnableCanonnUpload = enable; break;
            case "discord":
                cfg.EnableDiscord = enable;
                if (enable) _ = _discord?.RefreshPresenceAsync();
                else _discord?.DisconnectIpc();
                break;
            default:
                return $"⚠ Integración desconocida: {api}";
        }
        _configService.Save();
        return $"⚙ Integración '{api}' {(enable ? "activada" : "desactivada")}.";
    }

    internal string QueryCargoProfitReport()
    {
        var s = _fileWatcher.State;
        if (s.CargoInventory.Count == 0)
            return "Tu bodega de carga está vacía.";

        var notes = new List<string>();
        var marketItems = s.Market?.Items ?? new List<MarketCommodityItem>();
        if (marketItems.Count > 0)
        {
            long totalSell = 0, totalProfit = 0;
            foreach (var c in s.CargoInventory)
            {
                var m = marketItems.FirstOrDefault(x =>
                    EliteFileWatcherService.CommodityMatches(x.Name, x.DisplayName, c.Name, c.DisplayName));
                if (m is not null && m.SellPrice > 0)
                {
                    totalSell += m.SellPrice * c.Count;
                    if (m.MeanPrice > 0 && m.SellPrice > m.MeanPrice)
                        totalProfit += (m.SellPrice - m.MeanPrice) * c.Count;
                }
            }
            if (totalSell > 0)
                notes.Add($"Valor de venta en {s.StationName}: {totalSell:N0} CR (beneficio est. sobre media: +{totalProfit:N0} CR)");
        }
        else
        {
            notes.Add("Sin mercado atracado para valorar venta local.");
        }

        if (s.Colonisation.Resources.Count > 0)
        {
            var matches = s.CargoInventory
                .Where(c => s.Colonisation.Resources.Any(r => r.RequiredAmount > r.ProvidedAmount &&
                    EliteFileWatcherService.CommodityMatches(r.Name, r.DisplayName, c.Name, c.DisplayName)))
                .Select(static c => $"{(string.IsNullOrWhiteSpace(c.DisplayName) ? c.Name : c.DisplayName)} ({c.Count} t)")
                .ToList();
            if (matches.Count > 0)
                notes.Add($"Útil para colonización activa: {string.Join(", ", matches)}");
        }

        return $"Bodega ({s.CargoUsed}/{s.CargoCapacity} t): {string.Join(" | ", notes)}";
    }

    internal string QueryWishlistReport()
    {
        var cfg = _configService.Current;
        if (cfg.EngineeringWishlistBlueprints is not { Count: > 0 })
            return "No tienes planos añadidos a tu lista de deseos (Engineering Wishlist).";

        var rows = EngineeringWishlistService.Evaluate(cfg.EngineeringWishlistBlueprints, _fileWatcher.State);
        var missing = rows.Where(static r => r.Missing > 0).OrderByDescending(static r => r.Missing).Take(10)
            .Select(static r => $"{r.MaterialName} ({r.Category}): faltan {r.Missing} (tienes {r.Owned}/{r.Required})").ToList();
        return missing.Count == 0
            ? $"Wishlist ({cfg.EngineeringWishlistBlueprints.Count} planos): ¡Tienes todos los materiales necesarios!"
            : $"Wishlist ({cfg.EngineeringWishlistBlueprints.Count} planos) — Materiales faltantes: {string.Join(" | ", missing)}";
    }

    internal string QuerySurfaceReport()
    {
        var s = _fileWatcher.State;
        var parts = new List<string>();
        if (s.HasLatLong)
            parts.Add($"Posición en {s.BodyName}: LAT {s.Latitude:0.000}, LON {s.Longitude:0.000}, ALT {s.Altitude:0} m, Rumbo {s.Heading:0}°, Gravedad {s.GravityDisplay}{(s.TemperatureKelvin is > 0 ? $", Temp {s.TemperatureKelvin:0} K" : "")}");
        else
            parts.Add("Sin telemetría activa de superficie.");

        if (s.ExobiologyTracker is { } exo)
            parts.Add($"Exobiología activa: {exo.Species} ({exo.SamplesCollected}/3 muestras, distancia {exo.CurrentDistanceMeters:0}/{exo.RequiredDistanceMeters} m)");

        var bms = _configService.Current.SurfaceBookmarks ?? new List<SurfaceBookmarkItem>();
        if (bms.Count > 0)
            parts.Add($"Marcadores ({bms.Count}): {string.Join(", ", bms.Take(5).Select(static b => $"{b.Label}@{b.BodyName}"))}");

        return string.Join(" | ", parts);
    }

    internal string QueryPassengersReport()
    {
        var s = _fileWatcher.State;
        var paxMissions = s.ActiveMissions.Where(static m => m.PassengerMission == true).ToList();
        if (s.PassengerManifest.Count == 0 && paxMissions.Count == 0)
            return "No llevas pasajeros a bordo actualmente.";

        var parts = new List<string>();
        if (s.PassengerManifest.Count > 0)
            parts.Add($"Pasajeros ({s.PassengerManifest.Count} grupos): {string.Join(" | ", s.PassengerManifest.Take(8).Select(static p => $"{p.Type} x{p.Count}{(p.Vip ? " VIP" : "")}{(p.Wanted ? " BUSCADO" : "")}{(string.IsNullOrWhiteSpace(p.DestinationSystem) ? "" : $" → {p.DestinationSystem}{(string.IsNullOrWhiteSpace(p.DestinationStation) ? "" : $" ({p.DestinationStation})")}")}"))}");

        if (paxMissions.Count > 0)
            parts.Add($"Destinos de pasajeros: {string.Join(" | ", paxMissions.Take(6).Select(static m => $"{m.Name} → {m.DestinationSystem}{(string.IsNullOrWhiteSpace(m.DestinationStation) ? "" : $" ({m.DestinationStation})")}, {m.Reward ?? 0:N0} CR"))}");

        return string.Join(" | ", parts);
    }

    internal string QueryServerAndIntegrationsStatus()
    {
        var cfg = _configService.Current;
        var snap = _galaxyStatus.Current;
        var activeApis = new List<string>();
        if (cfg.EnableFrontierCapi) activeApis.Add($"Frontier cAPI{(_frontierCapi?.IsConnected == true ? "(OK)" : "(NoAuth)")}");
        if (cfg.EnableEddnUpload)
            activeApis.Add(_communitySync.EddnUploadedCount > 0 ? $"EDDN({_communitySync.EddnUploadedCount} sent)" : "EDDN");
        if (cfg.EnableEdsmSync) activeApis.Add("EDSM");
        if (cfg.EnableInaraSync)
            activeApis.Add(_communitySync.InaraUploadedCount > 0 ? $"Inara({_communitySync.InaraUploadedCount} sent)" : "Inara");
        if (cfg.EnableSpanshSystemData) activeApis.Add("Spansh");
        if (cfg.EnableEliteBgsWatchlist)
        {
            int watchedCount = _fileWatcher.State.WatchedBgsFactions.Count;
            activeApis.Add(watchedCount > 0 ? $"EliteBGS({watchedCount} tracked)" : "EliteBGS");
        }
        if (cfg.EnableArdentMarketApi) activeApis.Add("Ardent");
        if (cfg.EnableEdastroDssa) activeApis.Add("EDAstro-DSSA");
        if (cfg.EnableCanonnUpload)
            activeApis.Add(_edastroCanonn is { CanonnUploadedCount: > 0 } ? $"Canonn({_edastroCanonn.CanonnUploadedCount} sent)" : "Canonn");
        if (cfg.EnableDiscord) activeApis.Add("Discord");

        string server = string.IsNullOrWhiteSpace(snap.ServerMessage) ? "Sin datos" : snap.ServerMessage;
        string tick = snap.LastBgsTickUtc.HasValue ? snap.LastBgsTickUtc.Value.ToString("yyyy-MM-dd HH:mm") + "Z" : "desconocido";
        return $"Servidor Elite: {server} | Último Tick BGS: {tick} | Integraciones activas ({activeApis.Count}): {(activeApis.Count > 0 ? string.Join(", ", activeApis) : "ninguna")}";
    }

    private async Task<string> QueryCommunityGoalsAsync()
    {
        var s = _fileWatcher.State;
        var goals = s.CommunityGoals;
        if (goals.Count == 0 && _configService.Current.EnableInaraSync)
        {
            var (_, fetched, _) = await _communitySync.FetchInaraCommunityGoalsAsync();
            goals = fetched;
        }
        if (goals.Count == 0)
            return "No hay Community Goals activos cargados (activa Frontier cAPI o Inara Read API).";
        return $"Community Goals ({goals.Count}): {string.Join(" | ", goals.Take(5).Select(static g => $"{g.Title} en {g.StarSystem} ({g.StationName}) — {g.ProgressDisplay}, caduca {g.ExpiryDisplay}"))}";
    }

    internal string QuerySpanshSystemDump()
    {
        if (!_configService.Current.EnableSpanshSystemData)
            return "Spansh System Data está desactivado en Ajustes -> Integraciones.";
        var snap = _spanshService.State.Snapshot;
        if (snap is null)
            return "No hay volcado de Spansh cargado para el sistema actual.";
        int landables = snap.Bodies.Count(static b => b.IsLandable == true);
        return $"Spansh ({snap.Name}): {snap.BodyCount} cuerpos ({landables} aterrizables), {snap.StationCount} estaciones.";
    }

    private async Task<string> QueryEdsmTrafficAsync(string systemName)
    {
        if (!_configService.Current.EnableEdsmSync)
            return "EDSM integration is disabled in Settings -> Integrations.";
        string sys = string.IsNullOrWhiteSpace(systemName) ? _fileWatcher.State.CurrentSystem : systemName.Trim();
        var intel = await _communitySync.FetchEdsmSystemIntelAsync(sys);
        if (intel is null)
            return $"No EDSM traffic data available for {sys}.";
        string noteStr = !string.IsNullOrWhiteSpace(intel.RemoteComment) ? $" | EDSM note: {intel.RemoteComment}" : "";
        return $"EDSM {intel.SystemName}: Traffic {intel.TrafficDay}/day ({intel.TrafficWeek}/week), Destructions {intel.DeathsDay}/day ({intel.DeathsWeek}/week){noteStr}.";
    }

    private async Task<string> QueryEdsmStationsAsync(string systemName)
    {
        if (!_configService.Current.EnableEdsmSync)
            return "EDSM integration is disabled in Settings -> Integrations.";
        string sys = string.IsNullOrWhiteSpace(systemName) ? _fileWatcher.State.CurrentSystem : systemName.Trim();
        string url = $"https://www.edsm.net/api-system-v1/stations?systemName={Uri.EscapeDataString(sys)}";
        using var resp = await _httpClient.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
            return $"EDSM stations HTTP {(int)resp.StatusCode} for {sys}.";

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("stations", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return $"No stations recorded in EDSM for {sys}.";

        var list = new List<string>();
        foreach (var st in arr.EnumerateArray().Take(12))
        {
            string name = GetStr(st, "name") ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;
            string type = GetStr(st, "type") ?? "";
            double dist = JsonElementExtensions.GetDouble(st, "distanceToArrival") ?? 0;
            bool hasShipyard = st.TryGetProperty("haveShipyard", out var hs) && hs.ValueKind == JsonValueKind.True;
            bool hasOutfitting = st.TryGetProperty("haveOutfitting", out var ho) && ho.ValueKind == JsonValueKind.True;
            bool hasMarket = st.TryGetProperty("haveMarket", out var hm) && hm.ValueKind == JsonValueKind.True;
            var tags = new List<string>();
            if (hasMarket) tags.Add("Market");
            if (hasShipyard) tags.Add("Shipyard");
            if (hasOutfitting) tags.Add("Outfitting");
            list.Add($"{name} ({type}, {dist:0} ls{(tags.Count > 0 ? ", " + string.Join("/", tags) : "")})");
        }
        return $"EDSM stations in {sys} ({arr.GetArrayLength()} total): {string.Join(" | ", list)}";
    }

    private async Task<string> QueryEdsmNearbySystemsAsync(string param)
    {
        if (!_configService.Current.EnableEdsmSync)
            return "EDSM integration is disabled in Settings -> Integrations.";

        string[] parts = (param ?? "").Split(new[] { '|', ':' }, 2, StringSplitOptions.TrimEntries);
        string sys = parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0] : _fileWatcher.State.CurrentSystem;
        double radius = parts.Length == 2 && double.TryParse(parts[1].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double r)
            ? Math.Clamp(r, 5, 100)
            : 20;

        string url = $"https://www.edsm.net/api-v1/sphere-systems?systemName={Uri.EscapeDataString(sys)}&radius={radius.ToString("0", CultureInfo.InvariantCulture)}&showInformation=1";
        using var resp = await _httpClient.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
            return $"EDSM sphere-systems HTTP {(int)resp.StatusCode} for {sys}.";

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return $"No nearby systems found in EDSM within {radius:0} LY of {sys}.";

        var nearby = new List<(string Name, double Dist, string Eco)>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            string name = GetStr(item, "name") ?? "";
            double dist = JsonElementExtensions.GetDouble(item, "distance") ?? 0;
            if (string.IsNullOrWhiteSpace(name) || string.Equals(name, sys, StringComparison.OrdinalIgnoreCase))
                continue;
            string eco = item.TryGetProperty("information", out var info) && info.ValueKind == JsonValueKind.Object
                ? (GetStr(info, "economy") ?? "")
                : "";
            nearby.Add((name, dist, eco));
        }

        var top = nearby.OrderBy(static x => x.Dist).Take(12)
            .Select(static x => $"{x.Name} ({x.Dist:0.0} LY{(string.IsNullOrWhiteSpace(x.Eco) ? "" : ", " + x.Eco)})");
        return $"EDSM systems within {radius:0} LY of {sys} ({nearby.Count} found): {string.Join(" | ", top)}";
    }

    private async Task<string> QueryEdsmDistanceAsync(string param)
    {
        if (!_configService.Current.EnableEdsmSync)
            return "EDSM integration is disabled in Settings -> Integrations.";
        string[] parts = (param ?? "").Split('|', 2, StringSplitOptions.TrimEntries);
        string fromSys = parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0] : _fileWatcher.State.CurrentSystem;
        string toSys = parts.Length == 2 ? parts[1] : (parts.Length == 1 ? parts[0] : "");
        if (string.IsNullOrWhiteSpace(fromSys) || string.IsNullOrWhiteSpace(toSys))
            return "Provide origin and destination systems.";

        var c1 = await FetchEdsmCoordsAsync(fromSys);
        var c2 = await FetchEdsmCoordsAsync(toSys);
        if (c1 is null || c2 is null)
            return $"Could not resolve EDSM 3D coordinates for '{fromSys}' or '{toSys}'.";

        double dx = c2.Value.X - c1.Value.X;
        double dy = c2.Value.Y - c1.Value.Y;
        double dz = c2.Value.Z - c1.Value.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return $"3D distance from {fromSys} to {toSys}: {dist:0.00} LY.";
    }

    private async Task<(double X, double Y, double Z)?> FetchEdsmCoordsAsync(string systemName)
    {
        if (string.Equals(systemName, _fileWatcher.State.CurrentSystem, StringComparison.OrdinalIgnoreCase) &&
            _fileWatcher.State.StarPos is { Length: 3 } sp)
        {
            return (sp[0], sp[1], sp[2]);
        }

        try
        {
            string url = $"https://www.edsm.net/api-v1/system?systemName={Uri.EscapeDataString(systemName)}&showCoordinates=1";
            using var resp = await _httpClient.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("coords", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                return (c.GetProperty("x").GetDouble(), c.GetProperty("y").GetDouble(), c.GetProperty("z").GetDouble());
            }
        }
        catch
        {
        }
        return null;
    }

    private async Task<string> QuerySpanshTradeLiveAsync()
    {
        if (!_configService.Current.EnableSpanshSystemData)
            return "Spansh integration is disabled in Settings -> Integrations.";
        var st = _fileWatcher.State;
        var (ok, hops, msg) = await _spanshService.PlotTradeRouteAsync(st.CurrentSystem, st.StationName, st.CargoCapacity, st.Credits, 30, st.CargoCapacity >= 200);
        if (!ok || hops.Count == 0)
            return msg;
        return string.Join(" | ", hops.Take(3).Select(static h => $"Hop {h.Hop}: {h.RouteDisplay} — {h.Commodity} ({h.ProfitDisplay}, {h.DistanceDisplay})"));
    }

    private async Task<string> QuerySpanshMiningLiveAsync(string mineral)
    {
        if (!_configService.Current.EnableSpanshSystemData)
            return "Spansh integration is disabled in Settings -> Integrations.";
        var st = _fileWatcher.State;
        string target = string.IsNullOrWhiteSpace(mineral) ? "Platinum" : mineral.Trim();
        var (ok, rings, msg) = await _spanshService.SearchMiningRingsAsync(target, st.CurrentSystem, st.StarPos);
        if (!ok || rings.Count == 0)
            return msg;
        return string.Join(" | ", rings.Take(5).Select(static r => $"{r.RingName} ({r.SystemName}, {r.DistanceDisplay}, {r.RingType}): {r.Signals}"));
    }

    private async Task<string> QueryCargoBestSellArdentAsync()
    {
        if (_ardentMarket is null || !_configService.Current.EnableArdentMarketApi)
            return "Ardent Insight Market API is disabled in Settings -> Integrations.";
        var st = _fileWatcher.State;
        if (st.CargoInventory.Count == 0)
            return "Your ship cargo hold is empty.";
        var topCargo = st.CargoInventory.OrderByDescending(static c => c.Count).First();
        string comm = !string.IsNullOrWhiteSpace(topCargo.DisplayName) ? topCargo.DisplayName : topCargo.Name;
        var (ok, offers, msg) = await _ardentMarket.SearchCommodityAsync(comm, "sell", st.CurrentSystem);
        if (!ok || offers.Count == 0)
            return $"No Ardent sell offers found for {comm}: {msg}";
        var topOffers = offers.Take(3).Select(o => $"{o.StationName} ({o.SystemName}, {o.DistanceDisplay}, Pad {o.MaxLandingPad}): {o.PriceDisplay}/t (~{o.Price * topCargo.Count:N0} CR total)");
        return $"Best Ardent sell stations for {topCargo.Count}t {comm}: {string.Join(" | ", topOffers)}";
    }

    internal string QueryActiveAlertsReport()
    {
        if (_alertService is null)
            return "Alert service is unavailable.";
        var active = _alertService.ActiveAlerts;
        if (active.Count == 0)
            return "No active tactical alerts right now.";
        return $"Active alerts ({active.Count}): {string.Join(" | ", active.Take(6).Select(static a => $"[{a.Severity}] {a.Title}: {a.Message}"))}";
    }

    internal string QueryLandingPadReport()
    {
        var s = _fileWatcher.State;
        if (s.AssignedLandingPad is not int pad || pad <= 0)
            return "No landing pad is currently assigned.";
        var info = LandingPadLocatorService.Resolve(pad, s.AssignedLandingStationType);
        return $"Pad {pad} at {(string.IsNullOrWhiteSpace(s.AssignedLandingStation) ? s.StationName : s.AssignedLandingStation)}: {info.FormatGuidance(_configService.Current.InterfaceLanguage)}";
    }

    internal string QueryWebHudStatus() =>
        WebHudStatusProvider?.Invoke() ?? "Web HUD status is unavailable.";

    private async Task<string> QueryFuelEmergencyAsync()
    {
        var s = _fileWatcher.State;
        double pct = s.FuelMax > 0 ? (s.FuelMain / s.FuelMax) * 100.0 : 0;
        var parts = new List<string>
        {
            $"Fuel: {s.FuelMain:0.0}/{s.FuelMax:0.0} t ({pct:0}%). Next scoopable on route: {(s.RouteHasNoFuelStar ? "NONE" : (s.JumpsToNextFuelStar?.ToString() ?? "N/A"))}."
        };

        if (_configService.Current.EnableSpanshSystemData)
        {
            var (ok, carriers, _) = await _spanshService.SearchFleetCarriersAsync("Refuel", s.CurrentSystem, s.StarPos, maxDistanceLy: 150);
            if (ok && carriers.Count > 0)
            {
                var nearest = carriers[0];
                CopySystemRequested?.Invoke(nearest.SystemName);
                parts.Add($"Nearest Refuel Carrier: {nearest.Callsign} in {nearest.SystemName} ({nearest.DistanceDisplay}, system copied to clipboard).");
            }
        }
        return string.Join(" ", parts);
    }

    private async Task<string> QueryExpeditionSummaryAsync()
    {
        var exp = await ExpeditionAnalyticsService.AnalyzeFolderAsync(_configService.Current.JournalFolderPath, maxFiles: 100);
        return $"Expedition ({exp.JournalsScanned} journals): {exp.TotalJumps:N0} jumps, {exp.TotalDistanceLy:N1} LY, {exp.UniqueSystems:N0} unique systems, {exp.EarthLikeWorlds} ELW / {exp.WaterWorlds} WW / {exp.AmmoniaWorlds} AW, {exp.OrganicsAnalysed} exobiology, {exp.MissionsCompleted} missions, {exp.TotalBountiesAndSalesCredits:N0} CR earned.";
    }

    internal string QueryMaterialInventory(string query)
    {
        var s = _fileWatcher.State;
        string q = (query ?? "").Trim();
        string lower = q.ToLowerInvariant().Replace('|', ':');

        // Parse analytical tokens in any order (e.g. "full:g5", "g5:full", "g5", "lowest:raw", "raw", "100%:g5")
        var tokens = lower.Split(new[] { ':', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool isBareCategoryOrGrade = tokens.Length > 0 && tokens.All(static t =>
            t is "raw" or "bruto" or "manufactured" or "manufacturados" or "encoded" or "codificados" or "datos"
                or "odyssey" or "locker" or "taquilla" or "all" or "todos"
                or "g1" or "g2" or "g3" or "g4" or "g5"
                or "lowest" or "min" or "escasos" or "highest" or "max" or "full" or "llenos" or "100%" or "100"
                or "summary" or "resumen");

        bool wantSummary = tokens.Any(static t => t is "summary" or "resumen") || string.IsNullOrWhiteSpace(lower);
        bool wantLowest = tokens.Any(static t => t is "lowest" or "min" or "escasos" || t.StartsWith("escas", StringComparison.Ordinal));
        bool wantHighest = tokens.Any(static t => t is "highest" or "max" or "full" or "llenos" or "100%" or "100" || t.StartsWith("lleno", StringComparison.Ordinal));

        if (wantSummary || wantLowest || wantHighest || isBareCategoryOrGrade)
        {
            string filter = tokens.FirstOrDefault(static t =>
                t is "raw" or "bruto" or "manufactured" or "manufacturados" or "encoded" or "codificados" or "datos"
                    or "odyssey" or "locker" or "taquilla" or "g1" or "g2" or "g3" or "g4" or "g5") ?? "";

            if (wantSummary && string.IsNullOrEmpty(filter))
            {
                if (s.MaterialsInventory.Count == 0 && s.ShipLockerInventory.Count == 0)
                    return "Aún no hay telemetría de materiales cargada en esta sesión.";

                var byCat = s.MaterialsInventory
                    .GroupBy(static m => string.IsNullOrWhiteSpace(m.Category) ? "Otros" : m.Category)
                    .Select(g =>
                    {
                        int full = g.Count(static m => m.MaxCapacity > 0 && m.Count >= m.MaxCapacity);
                        int low = g.Count(static m => m.Count <= 5);
                        double avgPct = g.Where(static m => m.MaxCapacity > 0)
                            .Select(static m => (double)m.Count / m.MaxCapacity * 100.0)
                            .DefaultIfEmpty(0)
                            .Average();
                        return $"{g.Key}: {g.Count()} tipos ({full} llenos, {low} muy bajos ≤5, llenado medio {avgPct:0}%)";
                    });
                return $"Resumen de materiales de nave: {string.Join(" | ", byCat)}. Taquilla Odyssey: {s.ShipLockerInventory.Count} tipos ({s.ShipLockerInventory.Sum(static l => l.Count)} ítems). Mochila: {s.BackpackInventory.Count} tipos.";
            }

            if (filter is "odyssey" or "locker" or "taquilla")
            {
                if (s.ShipLockerInventory.Count == 0)
                    return "La taquilla Odyssey está vacía o sin datos cargados.";
                var ordered = wantLowest
                    ? s.ShipLockerInventory.OrderBy(static l => l.Count).ThenBy(static l => l.DisplayName)
                    : s.ShipLockerInventory.OrderByDescending(static l => l.Count).ThenBy(static l => l.DisplayName);
                var items = ordered.Take(10).Select(static l => $"{l.DisplayName} ({l.Type}): {l.Count}");
                return $"{(wantLowest ? "Ítems con MENOS cantidad" : "Ítems con MÁS cantidad")} en taquilla Odyssey: {string.Join(" | ", items)}";
            }

            if (s.MaterialsInventory.Count == 0)
                return "Aún no hay telemetría de materiales de nave cargada en esta sesión.";

            IEnumerable<MaterialItem> pool = s.MaterialsInventory;
            if (filter is "raw" or "bruto")
                pool = pool.Where(static m => m.Category.Contains("bruto", StringComparison.OrdinalIgnoreCase) || m.Id.StartsWith("raw:", StringComparison.OrdinalIgnoreCase));
            else if (filter is "manufactured" or "manufacturados")
                pool = pool.Where(static m => m.Category.Contains("Manufact", StringComparison.OrdinalIgnoreCase) || m.Id.StartsWith("manufactured:", StringComparison.OrdinalIgnoreCase));
            else if (filter is "encoded" or "codificados" or "datos")
                pool = pool.Where(static m => m.Category.Contains("codific", StringComparison.OrdinalIgnoreCase) || m.Id.StartsWith("encoded:", StringComparison.OrdinalIgnoreCase));
            else if (filter.Length == 2 && filter[0] == 'g' && int.TryParse(filter[1..], out int gNum))
                pool = pool.Where(m => m.Grade == gNum);

            var list = pool.ToList();
            if (list.Count == 0)
                return $"No hay materiales registrados para el filtro '{filter}'.";

            if (wantLowest)
            {
                var bottom = list
                    .OrderBy(static m => m.Count)
                    .ThenBy(static m => m.MaxCapacity > 0 ? (double)m.Count / m.MaxCapacity : 0)
                    .Take(10)
                    .Select(static m => $"{m.DisplayName} ({m.Category}, G{m.Grade?.ToString() ?? "?"}): {m.Count}/{m.MaxCapacity}");
                return $"Materiales con MENOS cantidad{(string.IsNullOrEmpty(filter) ? "" : $" ({filter})")}: {string.Join(" | ", bottom)}";
            }
            else if (wantHighest)
            {
                var at100 = list.Where(static m => m.MaxCapacity > 0 && m.Count >= m.MaxCapacity).ToList();
                if (at100.Count > 0)
                {
                    return $"Materiales AL 100%{(string.IsNullOrEmpty(filter) ? "" : $" ({filter})")} — Total {at100.Count}: {string.Join(", ", at100.Select(static m => $"{m.DisplayName} ({m.Count}/{m.MaxCapacity})"))}";
                }
                var top = list
                    .OrderByDescending(static m => m.MaxCapacity > 0 ? (double)m.Count / m.MaxCapacity : 0)
                    .ThenByDescending(static m => m.Count)
                    .Take(8)
                    .Select(static m => $"{m.DisplayName} ({m.Count}/{m.MaxCapacity})");
                return $"Ningún material está al 100%{(string.IsNullOrEmpty(filter) ? "" : $" en {filter}")}. Más cercanos al máximo: {string.Join(" | ", top)}";
            }
            else
            {
                var at100 = list.Where(static m => m.MaxCapacity > 0 && m.Count >= m.MaxCapacity).ToList();
                var bottom = list.OrderBy(static m => m.Count).Take(6).Select(static m => $"{m.DisplayName} ({m.Count}/{m.MaxCapacity})");
                double avgPct = list.Where(static m => m.MaxCapacity > 0)
                    .Select(static m => (double)m.Count / m.MaxCapacity * 100.0)
                    .DefaultIfEmpty(0)
                    .Average();
                string fullNote = at100.Count > 0
                    ? $"Al 100% ({at100.Count}): {string.Join(", ", at100.Select(static m => $"{m.DisplayName} ({m.Count}/{m.MaxCapacity})"))}"
                    : $"Al 100%: ninguno";
                return $"Filtro {filter} ({list.Count} tipos, llenado medio {avgPct:0}%) — {fullNote} | Más escasos: {string.Join(", ", bottom)}";
            }
        }

        var matches = new List<string>();
        foreach (var m in s.MaterialsInventory)
        {
            if (m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add($"{m.DisplayName} ({m.Category}, G{m.Grade?.ToString() ?? "?"}): {m.Count}/{m.MaxCapacity}");
            }
        }
        foreach (var l in s.ShipLockerInventory)
        {
            if (l.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                l.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add($"{l.DisplayName} (Taquilla Odyssey/{l.Type}): {l.Count}");
            }
        }
        foreach (var b in s.BackpackInventory)
        {
            if (b.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                b.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add($"{b.DisplayName} (Mochila/{b.Type}): {b.Count}");
            }
        }

        if (matches.Count == 0)
            return $"No se encontró '{q}' en materiales de nave, taquilla Odyssey ni mochila (cantidad: 0).";

        return string.Join(" | ", matches.Take(10));
    }

    private string QueryExplorationReport(string param)
    {
        var s = _fileWatcher.State;
        string p = (param ?? "bodies").Trim();
        string lower = p.ToLowerInvariant();

        if (lower.StartsWith("materials:", StringComparison.Ordinal) || lower.StartsWith("material:", StringComparison.Ordinal) ||
            lower.StartsWith("materials|", StringComparison.Ordinal) || lower.StartsWith("material|", StringComparison.Ordinal))
        {
            string elem = p.Split(new[] { ':', '|' }, 2)[1].Trim();
            if (string.IsNullOrWhiteSpace(elem))
                return "Especifica el elemento químico a buscar en los planetas del sistema.";

            var hits = s.ExplorationBodies
                .Where(static b => b.Landable == true && b.Materials.Count > 0)
                .Select(b => new
                {
                    b.BodyName,
                    b.DistanceFromArrivalLs,
                    GravityG = b.SurfaceGravity.HasValue ? b.SurfaceGravity.Value / 9.80665 : (double?)null,
                    Mat = b.Materials.FirstOrDefault(m => m.Name.Contains(elem, StringComparison.OrdinalIgnoreCase))
                })
                .Where(static x => x.Mat is not null)
                .OrderByDescending(static x => x.Mat!.Percent)
                .Take(6)
                .Select(static x => $"{x.BodyName}: {x.Mat!.Name} {x.Mat.Percent:0.0}% ({(x.DistanceFromArrivalLs.HasValue ? $"{x.DistanceFromArrivalLs.Value:0} LS" : "? LS")}{(x.GravityG.HasValue ? $", {x.GravityG.Value:0.00} G" : "")})")
                .ToList();

            return hits.Count == 0
                ? $"No se encontró '{elem}' en los cuerpos aterrizables escaneados de {s.CurrentSystem}."
                : $"Mejores planetas con {elem} en {s.CurrentSystem}: {string.Join(" | ", hits)}";
        }

        if (lower == "bio")
        {
            var bioBodies = s.ExplorationBodies
                .Where(static b => b.BiologicalSignalCount > 0)
                .OrderByDescending(static b => b.EstimatedExobiologyCredits ?? 0)
                .ThenByDescending(static b => b.BiologicalSignalCount)
                .Take(8)
                .Select(static b =>
                {
                    string pred = string.IsNullOrWhiteSpace(b.PredictedExobiology) ? "" : $", predicción: {b.PredictedExobiology}";
                    string val = b.EstimatedExobiologyCredits > 0 ? $", ~{b.EstimatedExobiologyCredits:N0} CR" : "";
                    return $"{b.BodyName} ({b.BiologicalSignalCount} bio, {(b.DistanceFromArrivalLs.HasValue ? $"{b.DistanceFromArrivalLs.Value:0} LS" : "? LS")}{val}{pred})";
                })
                .ToList();

            return bioBodies.Count == 0
                ? $"No hay cuerpos con señales biológicas registrados en {s.CurrentSystem}."
                : $"Cuerpos con exobiología en {s.CurrentSystem}: {string.Join(" | ", bioBodies)}";
        }

        if (lower == "gravity")
        {
            var landables = s.ExplorationBodies
                .Where(static b => b.Landable == true && b.SurfaceGravity is > 0)
                .OrderByDescending(static b => b.SurfaceGravity!.Value)
                .ToList();

            if (landables.Count == 0)
                return $"No hay datos de gravedad de cuerpos aterrizables en {s.CurrentSystem}.";

            var highest = landables.Take(3).Select(static b => $"{b.BodyName} ({b.SurfaceGravity!.Value / 9.80665:0.00} G)");
            var lowest = landables.TakeLast(3).Reverse().Select(static b => $"{b.BodyName} ({b.SurfaceGravity!.Value / 9.80665:0.00} G)");
            return $"Gravedad en {s.CurrentSystem} ({landables.Count} aterrizables) — Mayor: {string.Join(", ", highest)} | Menor: {string.Join(", ", lowest)}";
        }

        if (lower == "route")
        {
            if (s.NavRoute.Count == 0)
                return "No hay ninguna ruta de navegación trazada actualmente.";
            int fuelStars = s.NavRoute.Count(static w => w.IsFuelStar);
            int nonFuel = s.NavRoute.Count - fuelStars;
            var dest = s.NavRoute[^1];
            double totalLy = s.NavRoute.Sum(static w => w.StepDistanceLy);
            return $"Ruta activa hacia {dest.StarSystem}: {s.NavRoute.Count} sistemas ({totalLy:0.0} AL totales), {fuelStars} estrellas repostables y {nonFuel} no repostables. Próxima repostable en: {(s.RouteHasNoFuelStar ? "NINGUNA" : (s.JumpsToNextFuelStar?.ToString() ?? "?"))} saltos.";
        }

        if (lower == "factions")
        {
            if (s.SystemFactions.Count == 0)
                return $"No hay facciones BGS registradas en {s.CurrentSystem}.";
            var list = s.SystemFactions
                .OrderByDescending(static f => f.InfluencePercent)
                .Take(8)
                .Select(static f => $"{f.Name}: {f.InfluenceDisplay} ({f.FactionState}, {f.Government}, {f.Allegiance})");
            return $"Facciones BGS en {s.CurrentSystem}: {string.Join(" | ", list)}";
        }

        // Default: bodies ranking
        if (s.ExplorationBodies.Count == 0)
            return $"No hay cuerpos escaneados en {s.CurrentSystem}.";

        var bodies = s.ExplorationBodies
            .OrderByDescending(static b => b.EstimatedCredits ?? 0)
            .ThenBy(static b => b.IsSpanshOnly ? 1 : 0)
            .Take(10)
            .Select(static b => $"{b.BodyName} ({b.BodyType}, {(b.DistanceFromArrivalLs.HasValue ? $"{b.DistanceFromArrivalLs.Value:0} LS" : "? LS")}, {(b.IsSpanshOnly ? "Spansh sin escanear" : $"~{b.EstimatedCredits ?? 0:N0} CR{(b.WasMapped == true ? ", mapeado" : ", sin mapear")}")}{(b.BiologicalSignalCount > 0 ? $", {b.BiologicalSignalCount} bio" : "")})");
        return $"Cuerpos en {s.CurrentSystem} ({s.SystemScannedBodies}/{s.SystemTotalBodies?.ToString() ?? "?"} escaneados, {s.ExplorationBodies.Count} listados): {string.Join(" | ", bodies)}";
    }

    internal string QueryMissionsReport(string param)
    {
        var s = _fileWatcher.State;
        string lower = (param ?? "summary").Trim().ToLowerInvariant();
        if (s.ActiveMissions.Count == 0)
            return "No tienes ninguna misión activa actualmente.";

        if (lower == "urgent")
        {
            var urgent = s.ActiveMissions
                .Where(static m => m.Expiry.HasValue)
                .OrderBy(static m => m.Expiry!.Value)
                .Take(6)
                .Select(static m => $"{m.Name} → {(string.IsNullOrWhiteSpace(m.DestinationSystem) ? "?" : m.DestinationSystem)} (caduca en {m.TimeRemaining}, recompensa {m.Reward ?? 0:N0} CR)")
                .ToList();
            return urgent.Count == 0
                ? "Tus misiones activas no tienen fecha de caducidad registrada."
                : $"Misiones más urgentes: {string.Join(" | ", urgent)}";
        }

        if (lower == "rewards")
        {
            long total = s.ActiveMissions.Sum(static m => m.Reward ?? 0);
            var top = s.ActiveMissions
                .OrderByDescending(static m => m.Reward ?? 0)
                .Take(6)
                .Select(static m => $"{m.Name} ({m.Reward ?? 0:N0} CR → {m.DestinationSystem})");
            return $"Total recompensas de {s.ActiveMissions.Count} misiones activas: {total:N0} CR. Mejores: {string.Join(" | ", top)}";
        }

        if (lower == "massacre")
        {
            if (s.MassacreStacks.Count == 0)
                return "No tienes misiones de masacre apiladas actualmente.";
            long totalRew = s.MassacreStacks.Sum(static m => m.TotalReward);
            var stacks = s.MassacreStacks.Select(static m => $"{m.IssuingFaction} contra {m.TargetFaction}: {m.MissionCount} misiones, {m.RequiredKills} bajas, {m.TotalReward:N0} CR");
            return $"Apilamiento de masacres ({totalRew:N0} CR totales): {string.Join(" | ", stacks)}";
        }

        if (lower == "cargo")
        {
            var deliveryGroups = s.ActiveMissions
                .Where(static m => !string.IsNullOrWhiteSpace(m.Commodity) && m.Count is > 0 && m.PassengerMission != true)
                .GroupBy(static m => m.Commodity.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    int needed = g.Sum(static m => m.Count!.Value);
                    int onBoard = s.CargoInventory
                        .Where(c => EliteFileWatcherService.CommodityMatches(c.Name, c.DisplayName, g.Key))
                        .Sum(static c => c.Count);
                    int missing = Math.Max(0, needed - onBoard);
                    return $"{g.Key}: necesitas {needed} t, llevas {onBoard} t{(missing > 0 ? $" (FALTAN {missing} t)" : " (OK)")}";
                })
                .ToList();

            return deliveryGroups.Count == 0
                ? "No tienes misiones activas que requieran transporte de mercancía."
                : $"Carga de misiones: {string.Join(" | ", deliveryGroups)}";
        }

        // Default summary
        long totalCredits = s.ActiveMissions.Sum(static m => m.Reward ?? 0);
        var byDest = s.ActiveMissions
            .GroupBy(static m => string.IsNullOrWhiteSpace(m.DestinationSystem) ? "Sin destino" : m.DestinationSystem, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static g => g.Count())
            .Take(6)
            .Select(g => $"{g.Key} ({g.Count()} misiones, {g.Sum(static m => m.Reward ?? 0):N0} CR)");
        var nextExp = s.ActiveMissions.Where(static m => m.Expiry.HasValue).OrderBy(static m => m.Expiry!.Value).FirstOrDefault();
        return $"Resumen misiones: {s.ActiveMissions.Count} activas, {totalCredits:N0} CR totales. Por destino: {string.Join(" | ", byDest)}.{(nextExp is not null ? $" Próxima en caducar: {nextExp.Name} ({nextExp.TimeRemaining})." : "")}";
    }

    private string QueryOperationalReport(string param)
    {
        var s = _fileWatcher.State;
        string lower = (param ?? "session").Trim().ToLowerInvariant();

        if (lower is "colonisation" or "colonizacion")
        {
            if (s.Colonisation.Resources.Count == 0)
                return "No hay datos de obra de colonización activos en esta sesión.";

            long totalReq = s.Colonisation.Resources.Sum(static r => r.RequiredAmount);
            long totalProv = s.Colonisation.Resources.Sum(static r => r.ProvidedAmount);
            long remaining = Math.Max(0, totalReq - totalProv);
            int trips = s.CargoCapacity > 0 ? (int)Math.Ceiling((double)remaining / s.CargoCapacity) : 0;
            var pending = s.Colonisation.Resources
                .Where(static r => r.RequiredAmount > r.ProvidedAmount)
                .OrderByDescending(static r => r.RequiredAmount - r.ProvidedAmount)
                .Take(8)
                .Select(static r => $"{r.DisplayName}: faltan {r.RequiredAmount - r.ProvidedAmount:N0} t");

            return $"Colonización ({s.Colonisation.ProgressDisplay}): entregadas {totalProv:N0}/{totalReq:N0} t, faltan {remaining:N0} t{(trips > 0 ? $" (~{trips} viajes con tu bodega de {s.CargoCapacity} t)" : "")}. Pendiente: {string.Join(" | ", pending)}";
        }

        if (lower == "combat")
        {
            var cs = s.CombatSession;
            var byFac = cs.ByFaction.OrderByDescending(static f => f.PendingCredits).Take(5)
                .Select(static f => $"{f.Faction}: {f.Kills} bajas ({f.PendingCredits:N0} CR)");
            return $"Informe combate sesión: {cs.TotalKills} bajas, {cs.PendingCredits:N0} CR pendientes de cobro ({cs.CreditsPerHour:N0} CR/h).{(byFac.Any() ? " Por facción: " + string.Join(" | ", byFac) : "")}";
        }

        if (lower == "mining")
        {
            var ms = s.MiningSession;
            var byMin = ms.RefinedByMineral.OrderByDescending(static m => m.Tons).Take(6)
                .Select(static m => $"{m.Mineral}: {m.Tons} t");
            string lastAst = s.LastProspectedAsteroid is not null ? $" | Último asteroide: {s.LastProspectedAsteroid.SummaryDisplay}" : "";
            return $"Informe minería sesión: {ms.TotalRefinedTons} t refinadas ({ms.TonsPerHour:0.0} t/h), {ms.AsteroidsProspected} asteroides prospectados.{(byMin.Any() ? " Desglose: " + string.Join(", ", byMin) : "")}{lastAst}";
        }

        if (lower == "ship")
        {
            if (s.ShipModules.Count == 0)
                return $"Nave {s.ShipType} ({s.ShipName}), sin datos detallados de módulos todavía.";
            double totalMw = s.ShipModules.Where(static m => m.On).Sum(static m => m.PowerDrawMw);
            var mods = s.ShipModules
                .Take(16)
                .Select(static m => $"{m.DisplayName} (P{m.Priority}, {m.HealthPercent:0}%{(string.IsNullOrWhiteSpace(m.Engineering) ? "" : $", {m.Engineering}")})");
            return $"Nave {s.ShipType} ({s.ShipName}): {s.ShipModules.Count} módulos, consumo activo {totalMw:0.00} MW, bodega {s.CargoUsed}/{s.CargoCapacity} t, combustible {s.FuelMain:0.0}/{s.FuelMax:0.0} t. Módulos: {string.Join(" | ", mods)}";
        }

        // Default: session report
        var st = s.SessionStats;
        return $"Informe de sesión: balance neto {st.NetCredits:+#,0;-#,0;0} CR ({st.CreditsPerHour:N0} CR/h), {st.SystemsVisited} sistemas visitados ({st.TotalDistanceLy:0.0} AL), {st.BodiesScanned} cuerpos escaneados, {st.OrganicsAnalysed} especies exobiológicas, {st.MissionsCompleted} misiones completadas. Sin vender: Cartografía ~{s.UnsoldCartographyCredits:N0} CR, Exobiología ~{s.UnsoldExobiologyCredits:N0} CR.";
    }

    private string QueryProfileReport(string param)
    {
        var s = _fileWatcher.State;
        string lower = (param ?? "all").Trim().ToLowerInvariant();
        var sections = new List<string>();

        if (lower is "ranks" or "all")
        {
            if (s.CommanderProfile.Count > 0)
            {
                var ranks = s.CommanderProfile.Take(18).Select(static p => $"{p.Name}: {p.Value}");
                sections.Add($"Perfil/Rangos: {string.Join(" | ", ranks)}");
            }
            else
            {
                sections.Add("Sin datos de rangos cargados en esta sesión.");
            }
        }

        if (lower is "engineers" or "all")
        {
            if (s.Engineers.Count > 0)
            {
                var g5List = s.Engineers
                    .Where(static e => e.Rank >= 5)
                    .Take(15)
                    .Select(static e => e.Name)
                    .ToList();
                var pending = s.Engineers
                    .Where(static e => (e.Rank ?? 0) < 5)
                    .Take(12)
                    .Select(static e => $"{e.Name} ({e.Progress}, G{e.Rank?.ToString() ?? "0"})")
                    .ToList();
                sections.Add($"Ingenieros: {s.Engineers.Count} registrados ({g5List.Count} en Grado 5: {(g5List.Count > 0 ? string.Join(", ", g5List) : "ninguno")}). Pendientes de G5: {(pending.Count > 0 ? string.Join(" | ", pending) : "ninguno")}");
            }
            else
            {
                sections.Add("Sin datos de ingenieros cargados en esta sesión.");
            }
        }

        return string.Join("\n", sections);
    }

    private string QueryFleetReport(string param)
    {
        var s = _fileWatcher.State;
        string p = (param ?? "ships").Trim();
        string lower = p.ToLowerInvariant();

        if (lower.StartsWith("ships", StringComparison.Ordinal))
        {
            string filter = (p.Contains(':') || p.Contains('|')) ? p.Split(new[] { ':', '|' }, 2)[1].Trim() : "";
            string normFilter = string.IsNullOrWhiteSpace(filter) ? "" : NormalizeVoiceMatchText(filter, null);

            var results = new List<string>();
            if (!string.IsNullOrWhiteSpace(normFilter) &&
                (NormalizeVoiceMatchText(s.ShipName, null).Contains(normFilter, StringComparison.OrdinalIgnoreCase) ||
                 NormalizeVoiceMatchText(s.ShipType, null).Contains(normFilter, StringComparison.OrdinalIgnoreCase)))
            {
                string activeLoc = string.IsNullOrWhiteSpace(s.StationName) ? s.CurrentSystem : $"{s.CurrentSystem} ({s.StationName})";
                results.Add($"[NAVE ACTIVA] {s.ShipType}{(string.IsNullOrWhiteSpace(s.ShipName) ? "" : $" «{s.ShipName}»")} en {activeLoc}");
            }

            if (s.StoredShips.Count == 0 && results.Count == 0)
                return "No hay flota almacenada cargada (conecta Frontier cAPI en Ajustes -> Integraciones para sincronizar tus naves).";

            var stored = s.StoredShips
                .Where(sh => string.IsNullOrWhiteSpace(normFilter) ||
                             NormalizeVoiceMatchText(sh.ShipType, null).Contains(normFilter, StringComparison.OrdinalIgnoreCase) ||
                             NormalizeVoiceMatchText(sh.ShipName, null).Contains(normFilter, StringComparison.OrdinalIgnoreCase) ||
                             NormalizeVoiceMatchText(sh.StarSystem, null).Contains(normFilter, StringComparison.OrdinalIgnoreCase))
                .Take(40)
                .Select(static sh => $"{sh.ShipType}{(string.IsNullOrWhiteSpace(sh.ShipName) ? "" : $" «{sh.ShipName}»")} en {sh.StarSystem} ({sh.StationName}, {sh.Value:N0} CR)");
            results.AddRange(stored);

            return results.Count == 0
                ? $"No se encontró ninguna nave almacenada que coincida con '{filter}'."
                : string.IsNullOrWhiteSpace(normFilter)
                    ? $"Flota almacenada ({s.StoredShips.Count} naves en total): {string.Join(" | ", results)}"
                    : $"Coincidencias de flota para '{filter}': {string.Join(" | ", results)}";
        }

        if (lower.StartsWith("modules", StringComparison.Ordinal))
        {
            if (s.StoredModules.Count == 0)
                return "No hay módulos almacenados cargados (conecta Frontier cAPI en Ajustes -> Integraciones).";
            string filter = (p.Contains(':') || p.Contains('|')) ? p.Split(new[] { ':', '|' }, 2)[1].Trim() : "";
            var mods = s.StoredModules
                .Where(m => string.IsNullOrWhiteSpace(filter) ||
                            m.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            m.StarSystem.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(15)
                .Select(static m => $"{m.DisplayName}{(string.IsNullOrWhiteSpace(m.Engineering) ? "" : $" [{m.Engineering}]")} en {m.StarSystem} ({m.StationName})")
                .ToList();
            return mods.Count == 0
                ? $"No se encontró ningún módulo almacenado que coincida con '{filter}'."
                : $"Módulos almacenados ({s.StoredModules.Count} totales): {string.Join(" | ", mods)}";
        }

        if (lower == "orders")
        {
            if (s.CarrierMarketOrders.Count == 0)
                return "No hay órdenes de mercado activas en el portanaves (cAPI).";
            var orders = s.CarrierMarketOrders.Take(15)
                .Select(static o => $"{o.OrderType} {o.Commodity} x{o.Quantity} a {o.PriceDisplay}");
            return $"Órdenes de mercado del portanaves ({s.CarrierMarketOrders.Count}): {string.Join(" | ", orders)}";
        }

        if (lower == "services")
        {
            return s.CarrierServices.Count == 0
                ? "No hay servicios activos del portanaves registrados."
                : $"Servicios activos en el portanaves: {string.Join(", ", s.CarrierServices)}";
        }

        if (lower == "bartender")
        {
            if (s.CarrierMaterials.Count == 0)
                return "No hay datos del Bartender del portanaves registrados.";
            var items = s.CarrierMaterials.Take(15)
                .Select(static m => $"{m.DisplayName}: stock {m.Stock}, demanda {m.Demand}, precio {m.Price:N0} CR");
            return $"Bartender ({s.CarrierMaterialsStation}): {string.Join(" | ", items)}";
        }

        if (lower == "cargo")
        {
            if (s.CarrierCargo.Count == 0)
                return "No hay datos de bodega del portanaves (cAPI) cargados.";
            int totalTons = s.CarrierCargo.Sum(static c => c.Quantity);
            var items = s.CarrierCargo.OrderByDescending(static c => c.Quantity).Take(15)
                .Select(static c => $"{c.Commodity}: {c.Quantity} t");
            return $"Bodega del portanaves ({totalTons:N0} t en {s.CarrierCargo.Count} mercancías): {string.Join(" | ", items)}";
        }

        // Default: carrier financial & operational report
        if (s.Carrier is not { } fc)
            return "No hay datos de portanaves registrados en esta sesión.";

        long balance = fc.Balance ?? 0;
        long upkeep = fc.WeeklyUpkeep is > 0 ? fc.WeeklyUpkeep.Value : 10_000_000;
        double estWeeks = balance > 0 ? balance / (double)upkeep : 0;
        string upkeepLabel = fc.WeeklyUpkeep is > 0 ? $"{upkeep:N0} CR/sem real" : "~10M/sem est.";
        return $"Portanaves {fc.Name} [{fc.Callsign}] en {fc.System}: Tritio {fc.Fuel?.ToString() ?? "?"} t (salto máx. {fc.JumpRange?.ToString("0") ?? "500"} AL), espacio libre {fc.FreeSpace?.ToString("N0") ?? "?"}/{fc.Capacity?.ToString("N0") ?? "?"} t, saldo {balance:N0} CR (disponible {fc.Available?.ToString("N0") ?? "?"} CR, reserva {fc.Reserve?.ToString("N0") ?? "?"} CR, autonomía ~{estWeeks:0} semanas a {upkeepLabel}).";
    }

    private async Task<string> QueryEdsmSystemAsync(string systemName)
    {
        if (!_configService.Current.EnableEdsmSync)
            return "EDSM integration is disabled in Settings -> Integrations.";
        if (string.IsNullOrWhiteSpace(systemName))
            return "Nombre de sistema vacío.";

        string encoded = Uri.EscapeDataString(systemName.Trim());
        string sysUrl = $"https://www.edsm.net/api-v1/system?systemName={encoded}&showInformation=1&showCoordinates=1&showPrimaryStar=1";
        using var resp = await _httpClient.GetAsync(sysUrl);
        if (!resp.IsSuccessStatusCode)
            return $"EDSM devolvió HTTP {(int)resp.StatusCode} para {systemName}.";

        string body = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body) || body.Trim() is "{}" or "[]")
            return $"Sistema '{systemName}' no encontrado en EDSM.";

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string name = root.TryGetProperty("name", out var np) ? np.GetString() ?? systemName : systemName;

        string distText = "";
        if (root.TryGetProperty("coords", out var c) && c.ValueKind == JsonValueKind.Object &&
            _fileWatcher.State.StarPos is { Length: 3 } curPos &&
            c.TryGetProperty("x", out var xp) && c.TryGetProperty("y", out var yp) && c.TryGetProperty("z", out var zp))
        {
            double dx = xp.GetDouble() - curPos[0];
            double dy = yp.GetDouble() - curPos[1];
            double dz = zp.GetDouble() - curPos[2];
            double ly = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            distText = $"Distancia desde {_fileWatcher.State.CurrentSystem}: {ly:0.0} AL";
        }

        var parts = new List<string> { $"Sistema: {name}" };
        if (!string.IsNullOrWhiteSpace(distText)) parts.Add(distText);

        if (root.TryGetProperty("primaryStar", out var star) && star.ValueKind == JsonValueKind.Object)
        {
            string sType = star.TryGetProperty("type", out var st) ? st.GetString() ?? "" : "";
            bool scoop = star.TryGetProperty("isScoopable", out var sc) && sc.ValueKind == JsonValueKind.True;
            if (!string.IsNullOrWhiteSpace(sType))
                parts.Add($"Estrella: {sType} ({(scoop ? "repostable" : "no repostable")})");
        }

        if (root.TryGetProperty("information", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            if (info.TryGetProperty("allegiance", out var al) && al.ValueKind == JsonValueKind.String) parts.Add($"Lealtad: {al.GetString()}");
            if (info.TryGetProperty("government", out var gv) && gv.ValueKind == JsonValueKind.String) parts.Add($"Gobierno: {gv.GetString()}");
            if (info.TryGetProperty("economy", out var ec) && ec.ValueKind == JsonValueKind.String) parts.Add($"Economía: {ec.GetString()}");
            if (info.TryGetProperty("security", out var se) && se.ValueKind == JsonValueKind.String) parts.Add($"Seguridad: {se.GetString()}");
            if (info.TryGetProperty("population", out var po) && po.TryGetInt64(out long pop) && pop > 0) parts.Add($"Población: {pop:N0}");
            if (info.TryGetProperty("faction", out var fa) && fa.ValueKind == JsonValueKind.String) parts.Add($"Facción controladora: {fa.GetString()}");
        }

        return string.Join(" | ", parts);
    }

    private async Task<string> QuerySpanshNearestAsync(string serviceKey)
    {
        if (!_configService.Current.EnableSpanshSystemData)
            return "Spansh integration is disabled in Settings -> Integrations.";
        var state = _fileWatcher.State;
        var (ok, results, msg) = await _spanshService.SearchNearestServicesAsync(serviceKey, state.CurrentSystem, state.StarPos);
        if (!ok || results.Count == 0)
            return string.IsNullOrWhiteSpace(msg) ? $"Sin resultados en Spansh para {serviceKey}." : msg;

        var top = results.Take(3).Select(static r =>
            $"{r.SystemName} — {r.StationName} ({r.DistanceDisplay}, Pad {r.LandingPad})");
        return string.Join(" | ", top);
    }

    private async Task<string> QueryStationSnapshotAsync(string param)
    {
        var s = _fileWatcher.State;
        string[] parts = param.Split(new[] { '|', ':' }, 2, StringSplitOptions.TrimEntries);
        string kind = parts[0].ToLowerInvariant();
        string rawFilter = parts.Length > 1 ? parts[1].Trim() : "";
        string filter = rawFilter.ToLowerInvariant() is "" or "*" or "all" or "todo" or "todos" or "todas" or "list" or "lista"
            ? ""
            : rawFilter;

        if (kind == "shipyard")
        {
            if (s.Shipyard?.Available == true && s.Shipyard.Items.Count > 1 &&
                (string.IsNullOrWhiteSpace(s.StationName) || string.Equals(s.Shipyard.StationName, s.StationName, StringComparison.OrdinalIgnoreCase)))
            {
                return QueryStationSnapshot(param);
            }

            // Live EDSM fallback if local Shipyard.json is missing or hit the Odyssey 1-ship bug (only when EDSM is enabled)
            if (_configService.Current.EnableEdsmSync && s.Docked && !string.IsNullOrWhiteSpace(s.CurrentSystem) && !string.IsNullOrWhiteSpace(s.StationName))
            {
                var edsm = await EliteFileWatcherService.FetchEdsmShipyardAsync(s.CurrentSystem, s.StationName);
                if (edsm is { Items.Count: > 0 })
                {
                    var matches = edsm.Items
                        .Where(sh => string.IsNullOrWhiteSpace(filter) ||
                                     sh.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        .Take(25)
                        .Select(static sh => sh.ShipPrice > 0 ? $"{sh.DisplayName} ({sh.ShipPrice:N0} CR)" : sh.DisplayName)
                        .ToList();
                    return matches.Count == 0
                        ? $"No se vende '{filter}' en el astillero de {s.StationName} según EDSM."
                        : $"Astillero {s.StationName} (EDSM, {edsm.Items.Count} naves en total): {string.Join(" | ", matches)}";
                }
            }
        }

        return QueryStationSnapshot(param);
    }

    internal string QueryStationSnapshot(string param)
    {
        var s = _fileWatcher.State;
        string[] parts = param.Split(new[] { '|', ':' }, 2, StringSplitOptions.TrimEntries);
        string kind = parts[0].ToLowerInvariant();
        string rawFilter = parts.Length > 1 ? parts[1].Trim() : "";
        string filter = rawFilter.ToLowerInvariant() is "" or "*" or "all" or "todo" or "todos" or "todas" or "list" or "lista"
            ? ""
            : rawFilter;

        if (kind == "shipyard")
        {
            if (s.Shipyard?.Available != true || s.Shipyard.Items.Count == 0)
                return "No hay datos de astillero cargados todavía (en Elite Dangerous hay que abrir la pantalla de Astillero dentro de la estación para que el juego genere Shipyard.json).";
            var list = s.Shipyard.Items
                .Where(sh => string.IsNullOrWhiteSpace(filter) ||
                             sh.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                             sh.ShipType.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(25)
                .Select(static sh => $"{sh.DisplayName} ({sh.ShipPrice:N0} CR)")
                .ToList();
            return list.Count == 0
                ? $"No se vende '{filter}' en el astillero de {s.Shipyard.StationName}."
                : $"Astillero {s.Shipyard.StationName} ({s.Shipyard.Items.Count} naves en total): {string.Join(" | ", list)}";
        }

        if (kind == "outfitting")
        {
            if (s.Outfitting?.Available != true || s.Outfitting.Items.Count == 0)
                return "No hay datos de equipamiento local cargados.";
            var list = s.Outfitting.Items
                .Where(m => string.IsNullOrWhiteSpace(filter) ||
                            m.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(10)
                .Select(static m => $"{m.DisplayName} ({m.BuyPrice:N0} CR)")
                .ToList();
            return list.Count == 0
                ? $"No se encontró módulo '{filter}' en el equipamiento de {s.Outfitting.StationName}."
                : $"Equipamiento {s.Outfitting.StationName}: {string.Join(" | ", list)}";
        }

        if (kind == "market")
        {
            if (s.Market?.Available != true || s.Market.Items.Count == 0)
                return "No hay datos de mercado local cargados.";
            var list = s.Market.Items
                .Where(m => string.IsNullOrWhiteSpace(filter) ||
                            m.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .Select(static m => $"{m.DisplayName}: compra {m.BuyPrice:N0} CR (stock {m.Stock:N0}), venta {m.SellPrice:N0} CR (demanda {m.Demand:N0})")
                .ToList();
            return list.Count == 0
                ? $"No se encontró '{filter}' en el mercado de {s.Market.StationName}."
                : $"Mercado {s.Market.StationName}: {string.Join(" | ", list)}";
        }

        return $"Tipo de consulta de estación desconocido: {kind}";
    }

}