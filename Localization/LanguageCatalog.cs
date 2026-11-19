// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Localization/LanguageCatalog.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Embedded JSON language resource loader and supported-locale registry.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HUDnopy.Localization;

/// <summary>
/// Embedded JSON language resource loader and supported-locale registry.
/// </summary>
internal static class LanguageCatalog
{
    public static IReadOnlyList<string> Languages { get; } =
        Array.AsReadOnly(new[] { "es-ES", "en-GB", "fr-FR", "de-DE", "ru-RU", "pt-BR" });

    private static readonly IReadOnlyDictionary<string, JsonElement> Documents = Load();

    public static bool TryNormalize(string? language, out string normalized)
    {
        string? match = Languages.FirstOrDefault(item => string.Equals(item, language, StringComparison.OrdinalIgnoreCase));
        normalized = match ?? "";
        return match is not null;
    }

    public static JsonElement GetSection(string language, string section)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (!Documents.TryGetValue(language, out var document))
            throw new ArgumentException($"Idioma no compatible: {language}", nameof(language));
        if (!document.TryGetProperty(section, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Falta la sección {section} en el catálogo {language}.");
        return value;
    }

    private static IReadOnlyDictionary<string, JsonElement> Load()
    {
        var documents = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in Languages)
        {
            string resourceName = $"HUDnopy.Resources.Languages.{language}.json";
            using Stream stream = typeof(LanguageCatalog).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException($"No se encontró el catálogo de traducciones para {language}.");
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"El catálogo {language} no es un objeto JSON.");
            documents.Add(language, document.RootElement.Clone());
        }
        return documents;
    }
}
