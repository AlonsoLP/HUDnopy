// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/GalnetService.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Multi-language GalNet News feed client backed by Frontier CMS.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HUDnopy.Services;

/// <summary>
/// Multi-language GalNet News feed client backed by Frontier CMS.
/// </summary>
public sealed record GalnetArticle(string Id, string DateDisplay, string Title, string Body);

public sealed partial class GalnetService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Lock _lock = new();
    private List<GalnetArticle> _articles = new();

    public IReadOnlyList<GalnetArticle> Articles
    {
        get
        {
            lock (_lock)
                return _articles.ToArray();
        }
    }

    public event Action? ArticlesUpdated;

    public GalnetService()
    {
        _httpClient = HttpClientHeaderDefaults.CreateHttpClient(TimeSpan.FromSeconds(15));
    }

    public async Task<(bool Success, string Message)> RefreshAsync(string interfaceLanguage)
    {
        string cmsLang = interfaceLanguage switch
        {
            "es-ES" => "es-ES",
            "fr-FR" => "fr-FR",
            "de-DE" => "de-DE",
            "ru-RU" => "ru-RU",
            _ => "en-GB"
        };

        var result = await FetchFeedAsync(cmsLang);
        if (!result.Success && cmsLang != "en-GB")
            result = await FetchFeedAsync("en-GB");

        if (result.Success)
        {
            lock (_lock)
                _articles = result.Articles;
            ArticlesUpdated?.Invoke();
            return (true, $"GalNet: {result.Articles.Count}");
        }

        return (false, result.Error);
    }

    private async Task<(bool Success, List<GalnetArticle> Articles, string Error)> FetchFeedAsync(string cmsLang)
    {
        string url = $"https://cms.zaonce.net/{cmsLang}/jsonapi/node/galnet_article?sort=-published_at&page[limit]=15";
        try
        {
            using var resp = await _httpClient.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
                return (false, new List<GalnetArticle>(), $"GalNet HTTP {(int)resp.StatusCode}");

            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array)
                return (false, new List<GalnetArticle>(), "GalNet: formato inesperado");

            var list = new List<GalnetArticle>();
            foreach (var node in dataArr.EnumerateArray())
            {
                string id = node.TryGetProperty("id", out var idP) ? (idP.GetString() ?? "") : "";
                if (!node.TryGetProperty("attributes", out var attr) || attr.ValueKind != JsonValueKind.Object)
                    continue;

                string title = attr.TryGetProperty("title", out var tP) ? (tP.GetString() ?? "").Trim() : "";
                string date = attr.TryGetProperty("field_galnet_date", out var dP) ? (dP.GetString() ?? "").Trim() : "";
                string bodyRaw = "";
                if (attr.TryGetProperty("body", out var bObj) && bObj.ValueKind == JsonValueKind.Object &&
                    bObj.TryGetProperty("value", out var bVal))
                {
                    bodyRaw = bVal.GetString() ?? "";
                }

                if (string.IsNullOrWhiteSpace(title))
                    continue;

                string cleanBody = CleanHtml(bodyRaw);
                list.Add(new GalnetArticle(id, date, title, cleanBody));
            }

            return (true, list, "");
        }
        catch (Exception ex)
        {
            return (false, new List<GalnetArticle>(), $"GalNet: {ex.Message}");
        }
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBrRegex();

    [GeneratedRegex(@"</p>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlParagraphEndRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessNewlinesRegex();

    private static string CleanHtml(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        string text = HtmlBrRegex().Replace(raw, "\n");
        text = HtmlParagraphEndRegex().Replace(text, "\n\n");
        text = HtmlTagRegex().Replace(text, "");
        text = System.Net.WebUtility.HtmlDecode(text);
        return ExcessNewlinesRegex().Replace(text, "\n\n").Trim();
    }

    public void Dispose() => _httpClient.Dispose();
}
