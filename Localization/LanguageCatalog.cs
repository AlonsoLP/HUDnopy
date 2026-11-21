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
        Array.AsReadOnly(new[] { "en-GB", "es-ES", "fr-FR", "de-DE", "ru-RU", "pt-BR" });

    private static readonly IReadOnlyDictionary<string, JsonElement> Documents = Load();

    public static bool TryNormalize(string? language, out string normalized)
    {
        string clean = (language ?? "").Trim().Replace('_', '-');
        string? match = Languages.FirstOrDefault(item => string.Equals(item, clean, StringComparison.OrdinalIgnoreCase));
        if (match is null && clean.Length >= 2)
        {
            // Also accept base ISO-639 codes ("en", "es", "fr", "de", "ru", "pt") and any regional variant ("en-US", "es-MX", "pt-PT")
            string prefix = clean[..2];
            match = Languages.FirstOrDefault(item => item.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase));
        }
        normalized = match ?? "";
        return match is not null;
    }

    public static JsonElement GetSection(string language, string section)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (!Documents.TryGetValue(language, out var document))
            throw new ArgumentException($"Unsupported language: {language}", nameof(language));
        if (!document.TryGetProperty(section, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Missing section '{section}' in language catalog '{language}'.");
        return value;
    }

    private static IReadOnlyDictionary<string, JsonElement> Load()
    {
        var documents = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in Languages)
        {
            string resourceName = $"HUDnopy.Resources.Languages.{language}.json";
            using Stream stream = typeof(LanguageCatalog).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException($"Translation catalog not found for '{language}'.");
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Language catalog '{language}' is not a JSON object.");
            documents.Add(language, document.RootElement.Clone());
        }
        return documents;
    }
}
