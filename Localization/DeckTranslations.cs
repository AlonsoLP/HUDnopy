// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/DeckTranslations.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Localized labels and status messages for the control deck and keybindings.
// -----------------------------------------------------------------------------

using HUDnopy.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace HUDnopy.Localization;

/// <summary>
/// Localized labels and status messages for the control deck and keybindings.
/// </summary>
internal sealed record DeckBindingView(
    string ActionId, string CategoryId, string Category, string DisplayName, string IconGlyph,
    string FormattedBind, bool IsAssigned, string SourceDisplayName, string SourceCategory, string SourceFormattedBind);

internal static class DeckTranslations
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog = Load();

    public static IReadOnlyDictionary<string, string> ForLanguage(string language) =>
        Catalog.TryGetValue(language, out var text)
            ? text
            : throw new ArgumentException($"Idioma de controles no compatible: {language}", nameof(language));

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var catalog = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? expected = null;
        foreach (string language in LanguageCatalog.Languages)
        {
            var text = LanguageCatalog.GetSection(language, "deck").Deserialize<Dictionary<string, string>>();
            if (text is null || text.Count == 0 || text.Any(pair => string.IsNullOrWhiteSpace(pair.Value)))
                throw new InvalidDataException($"El catálogo de controles está incompleto para {language}.");
            expected ??= new HashSet<string>(text.Keys, StringComparer.Ordinal);
            if (!expected.SetEquals(text.Keys))
                throw new InvalidDataException($"Las claves de controles no coinciden para {language}.");
            catalog.Add(language, new ReadOnlyDictionary<string, string>(text));
        }
        return catalog;
    }

    public static string Category(string category, string language)
    {
        string? key = category switch
        {
            "Vuelo y Navegación" => "categoryFlight",
            "Sistemas de Nave" => "categorySystems",
            "Combate y Defensas" => "categoryCombat",
            "Energía (Pips)" => "categoryPower",
            "Paneles e Interfaz" => "categoryPanels",
            "Vehículo SRV" => "categorySrv",
            "A Pie (Odyssey)" => "categoryOnFoot",
            "DSS y Caza (SLF)" => "categoryDssSlf",
            "Macros Tácticas" => "categoryMacros",
            "Otros Controles del Juego" => "categoryOther",
            _ => null
        };
        return key is null ? category : ForLanguage(language)[key];
    }

    public static DeckBindingView Present(GameBinding binding, string language)
    {
        var text = ForLanguage(language);
        string formatted = binding.FormattedBind;
        bool assigned = formatted != "Sin asignar";
        string display = text.TryGetValue("action." + binding.ActionId, out string? translated) ? translated : binding.DisplayName;
        return new DeckBindingView(binding.ActionId, binding.Category, Category(binding.Category, language),
            display, binding.IconGlyph, assigned ? formatted : text["unassigned"], assigned,
            binding.DisplayName, binding.Category, formatted);
    }
}
