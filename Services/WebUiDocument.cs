// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/WebUiDocument.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Loader for the embedded Web HUD single-page HTML document.
// -----------------------------------------------------------------------------

using System;
using System.IO;

namespace HUDnopy.Services;

/// <summary>
/// Loader for the embedded Web HUD single-page HTML document.
/// </summary>
internal static class WebUiDocument
{
    public static string Load() => LoadAsset("index.html");

    public static string LoadAsset(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Web", fileName);
        return File.ReadAllText(path);
    }
}
