// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/WebAccessControl.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Access-mode policy, session code validation, and loopback/VPN interface filtering.
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HUDnopy.Services;

/// <summary>
/// Access-mode policy, session code validation, and loopback/VPN interface filtering.
/// </summary>
internal sealed class WebAccessControl
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private int _failures;
    private DateTimeOffset _blockedUntil;

    public string Code { get; } = RandomNumberGenerator.GetInt32(100_000_000).ToString("D8", CultureInfo.InvariantCulture);

    public bool TryLogin(string code, out string? token, out bool blocked)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            token = null;
            blocked = now < _blockedUntil;
            if (blocked)
                return false;

            byte[] expected = Encoding.UTF8.GetBytes(Code);
            byte[] supplied = Encoding.UTF8.GetBytes(code);
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
            {
                if (++_failures >= 5)
                {
                    _failures = 0;
                    _blockedUntil = now.AddMinutes(1);
                    blocked = true;
                }
                return false;
            }
            _failures = 0;
            foreach (string expired in _sessions.Where(session => session.Value <= now).Select(session => session.Key).ToArray())
                _sessions.Remove(expired);
            if (_sessions.Count >= 64)
                _sessions.Remove(_sessions.MinBy(session => session.Value).Key);
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _sessions.Add(token, now.AddHours(12));
            return true;
        }
    }

    public DateTimeOffset? GetExpiry(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(token, out var expiry))
                return null;
            if (expiry <= DateTimeOffset.UtcNow)
            {
                _sessions.Remove(token);
                return null;
            }
            return expiry;
        }
    }
}
