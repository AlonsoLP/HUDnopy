// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/AiCopilot/AiCopilotService.Queries.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Embedded actions, instant voice macros, ReAct 2-step loop, and analytical/remote queries.
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
    internal static string StripInternalTags(string rawReply)
    {
        if (string.IsNullOrEmpty(rawReply))
            return "";
        string noQuery = Regex.Replace(rawReply, @"\[\[QUERY[:|][^\]]*\]\]", "", RegexOptions.IgnoreCase);
        return Regex.Replace(noQuery, @"\[\[ACTION[:|][^\]]*\]\]", "", RegexOptions.IgnoreCase).Trim();
    }

    private async Task<string> ProcessEmbeddedActionsAsync(AppConfig cfg, string rawReply)
    {
        // Always strip any residual [[QUERY:...]] tags so internal syntax never leaks to chat or TTS
        string sanitized = Regex.Replace(rawReply, @"\[\[QUERY[:|][^\]]*\]\]", "", RegexOptions.IgnoreCase).Trim();

        if (!cfg.AiAllowActions || !sanitized.Contains("[[ACTION", StringComparison.OrdinalIgnoreCase))
            return StripInternalTags(sanitized);

        var matches = Regex.Matches(sanitized, @"\[\[ACTION[:|]\s*([A-Z_]+)\s*[:|]\s*([^\]]+)\]\]", RegexOptions.IgnoreCase);
        string cleanText = Regex.Replace(sanitized, @"\[\[ACTION[:|][^\]]*\]\]", "", RegexOptions.IgnoreCase).Trim();
        var actionLogs = new List<string>();

        foreach (Match m in matches)
        {
            string kind = m.Groups[1].Value.Trim().ToUpperInvariant();
            string param = m.Groups[2].Value.Trim();

            switch (kind)
            {
                case "BIND":
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
                if (!string.IsNullOrWhiteSpace(normAlias) &&
                    (normalizedInput.Equals(normAlias, StringComparison.Ordinal) ||
                     normalizedInput.StartsWith(normAlias + " ", StringComparison.Ordinal) ||
                     normalizedInput.EndsWith(" " + normAlias, StringComparison.Ordinal)))
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
        return Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim();
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
        SentenceStreamSpeaker? streamSpeaker = null)
    {
        var matches = Regex.Matches(firstReply, @"\[\[QUERY[:|]\s*([A-Z_]+)(?:\s*[:|]\s*([^\]]*))?\]\]", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Take(3)
            .ToList();

        if (matches.Count == 0)
            return Regex.Replace(firstReply, @"\[\[QUERY[:|][^\]]*\]\]", "", RegexOptions.IgnoreCase).Trim();

        var tasks = matches.Select(m => ExecuteQueryAsync(
            m.Groups[1].Value.Trim().ToUpperInvariant(),
            m.Groups[2].Success ? m.Groups[2].Value.Trim() : "")).ToArray();

        string[] results = await Task.WhenAll(tasks);

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

        var (ok, secondReply) = await SendProviderRequestAsync(cfg, systemPrompt, followUpMessages, thinkingBudget: 0, streamSpeaker: streamSpeaker);
        if (!ok || string.IsNullOrWhiteSpace(secondReply))
            return string.Join("\n", results);

        return Regex.Replace(secondReply, @"\[\[QUERY[:|][^\]]*\]\]", "", RegexOptions.IgnoreCase).Trim();
    }

    private async Task<string> ExecuteQueryAsync(string kind, string param)
    {
        try
        {
            return kind switch
            {
                "MATERIAL" => QueryMaterialInventory(param),
                "EXPLORATION" => QueryExplorationReport(param),
                "MISSIONS" => QueryMissionsReport(param),
                "REPORT" => QueryOperationalReport(param),
                "PROFILE" => QueryProfileReport(param),
                "FLEET" => QueryFleetReport(param),
                "SYSTEM" => await QueryEdsmSystemAsync(param),
                "SPANSH" => await QuerySpanshNearestAsync(param),
                "LOGBOOK" => await ExpeditionAnalyticsService.QueryLogbookAsync(_configService.Current.JournalFolderPath, param),
                "STATION" => await QueryStationSnapshotAsync(param),
                _ => $"Consulta no soportada: {kind}"
            };
        }
        catch (Exception ex)
        {
            return $"Error en consulta {kind}: {ex.Message}";
        }
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
                var bottom = list.OrderBy(static m => m.Count).Take(5).Select(static m => $"{m.DisplayName} ({m.Count}/{m.MaxCapacity})");
                string fullNote = at100.Count > 0
                    ? $"Al 100% ({at100.Count}): {string.Join(", ", at100.Select(static m => $"{m.DisplayName} ({m.Count}/{m.MaxCapacity})"))}"
                    : $"Al 100%: ninguno";
                return $"Filtro {filter} ({list.Count} tipos) — {fullNote} | Más escasos: {string.Join(", ", bottom)}";
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
            return $"Ruta activa hacia {dest.StarSystem}: {s.NavRoute.Count} sistemas ({totalLy:0.1} AL totales), {fuelStars} estrellas repostables y {nonFuel} no repostables. Próxima repostable en: {(s.RouteHasNoFuelStar ? "NINGUNA" : (s.JumpsToNextFuelStar?.ToString() ?? "?"))} saltos.";
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
            .Take(10)
            .Select(static b => $"{b.BodyName} ({b.BodyType}, {(b.DistanceFromArrivalLs.HasValue ? $"{b.DistanceFromArrivalLs.Value:0} LS" : "? LS")}, ~{b.EstimatedCredits ?? 0:N0} CR{(b.WasMapped == true ? ", mapeado" : ", sin mapear")}{(b.BiologicalSignalCount > 0 ? $", {b.BiologicalSignalCount} bio" : "")})");
        return $"Cuerpos escaneados en {s.CurrentSystem} ({s.SystemScannedBodies}/{s.SystemTotalBodies?.ToString() ?? "?"}): {string.Join(" | ", bodies)}";
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
                        .Where(c => string.Equals(c.Name?.Trim(), g.Key, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(c.DisplayName?.Trim(), g.Key, StringComparison.OrdinalIgnoreCase))
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
            return $"Nave {s.ShipType} ({s.ShipName}): {s.ShipModules.Count} módulos, consumo activo {totalMw:0.00} MW, bodega {s.CargoUsed}/{s.CargoCapacity} t, combustible {s.FuelMain:0.1}/{s.FuelMax:0.1} t. Módulos: {string.Join(" | ", mods)}";
        }

        // Default: session report
        var st = s.SessionStats;
        return $"Informe de sesión: balance neto {st.NetCredits:+#,0;-#,0;0} CR ({st.CreditsPerHour:N0} CR/h), {st.SystemsVisited} sistemas visitados ({st.TotalDistanceLy:0.1} AL), {st.BodiesScanned} cuerpos escaneados, {st.OrganicsAnalysed} especies exobiológicas, {st.MissionsCompleted} misiones completadas. Sin vender: Cartografía ~{s.UnsoldCartographyCredits:N0} CR, Exobiología ~{s.UnsoldExobiologyCredits:N0} CR.";
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
                int maxG5 = s.Engineers.Count(static e => e.Rank >= 5);
                var pending = s.Engineers
                    .Where(static e => (e.Rank ?? 0) < 5)
                    .Take(10)
                    .Select(static e => $"{e.Name} ({e.Progress}, G{e.Rank?.ToString() ?? "0"})");
                sections.Add($"Ingenieros: {s.Engineers.Count} registrados ({maxG5} en Grado 5). Pendientes de G5: {(pending.Any() ? string.Join(" | ", pending) : "ninguno")}");
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
            if (s.StoredShips.Count == 0)
                return "No hay flota almacenada cargada (conecta Frontier cAPI en Ajustes -> Integraciones para sincronizar tus naves).";

            string filter = (p.Contains(':') || p.Contains('|')) ? p.Split(new[] { ':', '|' }, 2)[1].Trim() : "";
            var ships = s.StoredShips
                .Where(sh => string.IsNullOrWhiteSpace(filter) ||
                             sh.ShipType.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                             sh.ShipName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                             sh.StarSystem.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(15)
                .Select(static sh => $"{sh.ShipType}{(string.IsNullOrWhiteSpace(sh.ShipName) ? "" : $" «{sh.ShipName}»")} en {sh.StarSystem} ({sh.StationName}, {sh.Value:N0} CR)")
                .ToList();

            return ships.Count == 0
                ? $"No se encontró ninguna nave almacenada que coincida con '{filter}'."
                : $"Flota almacenada ({s.StoredShips.Count} naves en total): {string.Join(" | ", ships)}";
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

        // Estimate weekly base upkeep (~10M CR/week typical average if exact upkeep not exposed)
        long balance = fc.Balance ?? 0;
        double estWeeks = balance > 0 ? balance / 10_000_000.0 : 0;
        return $"Portanaves {fc.Name} [{fc.Callsign}] en {fc.System}: Tritio {fc.Fuel?.ToString() ?? "?"} t (salto máx. {fc.JumpRange?.ToString("0") ?? "500"} AL), espacio libre {fc.FreeSpace?.ToString("N0") ?? "?"}/{fc.Capacity?.ToString("N0") ?? "?"} t, saldo {balance:N0} CR (disponible {fc.Available?.ToString("N0") ?? "?"} CR, reserva {fc.Reserve?.ToString("N0") ?? "?"} CR, autonomía estimada ~{estWeeks:0} semanas a 10M/sem).";
    }

    private async Task<string> QueryEdsmSystemAsync(string systemName)
    {
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
            distText = $"Distancia desde {_fileWatcher.State.CurrentSystem}: {ly:0.1} AL";
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

            // Live EDSM fallback if local Shipyard.json is missing or hit the Odyssey 1-ship bug
            if (s.Docked && !string.IsNullOrWhiteSpace(s.CurrentSystem) && !string.IsNullOrWhiteSpace(s.StationName))
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