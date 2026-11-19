// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Models/CarrierState.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Fleet Carrier state snapshot and multi-carrier Journal tracker.
// -----------------------------------------------------------------------------

using System.Globalization;
using System.IO;
using System.Text.Json;
using HUDnopy.Localization;

namespace HUDnopy.Models;

/// <summary>
/// Fleet Carrier state snapshot and multi-carrier Journal tracker.
/// </summary>
public sealed class CarrierState
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Callsign { get; set; } = "";
    public string System { get; set; } = "";
    public string Body { get; set; } = "";
    public string Destination { get; set; } = "";
    public string DestinationBody { get; set; } = "";
    public DateTimeOffset? DepartureTime { get; set; }
    public string DockingAccess { get; set; } = "";
    public bool? AllowNotorious { get; set; }
    public bool? PendingDecommission { get; set; }
    public long? Fuel { get; set; }
    public double? JumpRange { get; set; }
    public double? JumpRangeMax { get; set; }
    public long? Capacity { get; set; }
    public long? FreeSpace { get; set; }
    public long? Cargo { get; set; }
    public long? Balance { get; set; }
    public long? Reserve { get; set; }
    public long? Available { get; set; }
    public double? ReservePercent { get; set; }
    public double? TaxRate { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    internal CarrierState Snapshot() => (CarrierState)MemberwiseClone();
}

internal static class CarrierJournal
{
    private static readonly Dictionary<long, CarrierState> CarriersById = new();

    private static string? Text(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long? Long(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long number) ? number : null;

    private static double? Number(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double number) ? number : null;

    private static bool? Boolean(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;

    public static void Reset() => CarriersById.Clear();

    public static void Apply(ShipState state, string type, JsonElement data)
    {
        if (type is not ("CarrierBuy" or "CarrierStats" or "CarrierLocation" or "CarrierJumpRequest" or
            "CarrierJumpCancelled" or "CarrierJump" or "CarrierFinance" or "CarrierBankTransfer" or
            "CarrierDepositFuel" or "CarrierDockingPermission" or "CarrierNameChanged" or
            "CarrierDecommission" or "CarrierCancelDecommission"))
            return;

        long? id = Long(data, type == "CarrierJump" ? "MarketID" : "CarrierID");
        if (!id.HasValue) return;

        if (!CarriersById.TryGetValue(id.Value, out var existing))
        {
            if (state.Carrier?.Id == id.Value)
            {
                existing = state.Carrier.Snapshot();
            }
            else
            {
                if (type is not ("CarrierBuy" or "CarrierStats" or "CarrierLocation"))
                    return;
                existing = new CarrierState { Id = id.Value };
            }
        }
        else
        {
            existing = existing.Snapshot();
        }

        // Si el jugador está atracado en este mismo portanaves y aún no tenemos su sistema, usar CurrentSystem
        if (string.IsNullOrWhiteSpace(existing.System) &&
            !string.IsNullOrWhiteSpace(state.CurrentSystem) &&
            !string.IsNullOrWhiteSpace(state.StationName) &&
            !string.IsNullOrWhiteSpace(existing.Callsign) &&
            string.Equals(state.StationName, existing.Callsign, StringComparison.OrdinalIgnoreCase))
        {
            existing.System = state.CurrentSystem;
        }

        state.Carrier = existing;
        var carrier = state.Carrier;
        carrier.UpdatedAt = DateTimeOffset.TryParse(Text(data, "timestamp"), out var timestamp)
            ? timestamp : carrier.UpdatedAt;
        carrier.Name = Text(data, "Name") ?? carrier.Name;
        carrier.Callsign = Text(data, "Callsign") ?? carrier.Callsign;

        if (type is "CarrierBuy" or "CarrierLocation" or "CarrierJump")
        {
            carrier.System = Text(data, "StarSystem") ?? Text(data, "Location") ?? carrier.System;
            carrier.Body = Text(data, "Body") ?? "";
        }
        if (type == "CarrierJumpRequest")
        {
            carrier.Destination = Text(data, "SystemName") ?? "";
            carrier.DestinationBody = Text(data, "Body") ?? "";
            carrier.DepartureTime = DateTimeOffset.TryParse(
                Text(data, "DepartureTime") ?? Text(data, "JumpTime"), out var departure) ? departure : null;
        }
        if (type is "CarrierJumpCancelled" or "CarrierJump")
        {
            carrier.Destination = "";
            carrier.DestinationBody = "";
            carrier.DepartureTime = null;
        }
        if (type is "CarrierStats" or "CarrierDockingPermission")
        {
            carrier.DockingAccess = Text(data, "DockingAccess") ?? carrier.DockingAccess;
            carrier.AllowNotorious = Boolean(data, "AllowNotorious") ?? carrier.AllowNotorious;
        }
        if (type == "CarrierStats")
        {
            carrier.Fuel = Long(data, "FuelLevel") ?? carrier.Fuel;
            carrier.JumpRange = Number(data, "JumpRangeCurr") ?? carrier.JumpRange;
            carrier.JumpRangeMax = Number(data, "JumpRangeMax") ?? carrier.JumpRangeMax;
            carrier.PendingDecommission = Boolean(data, "PendingDecommission") ?? carrier.PendingDecommission;
            if (data.TryGetProperty("SpaceUsage", out var space) && space.ValueKind == JsonValueKind.Object)
            {
                carrier.Capacity = Long(space, "TotalCapacity");
                carrier.FreeSpace = Long(space, "FreeSpace");
                carrier.Cargo = Long(space, "Cargo");
            }
        }
        if (type == "CarrierDepositFuel")
            carrier.Fuel = Long(data, "Total") ?? carrier.Fuel;
        if (type == "CarrierDecommission") carrier.PendingDecommission = true;
        if (type == "CarrierCancelDecommission") carrier.PendingDecommission = false;
        JsonElement finance = data;
        if (type == "CarrierStats" &&
            (!data.TryGetProperty("Finance", out finance) || finance.ValueKind != JsonValueKind.Object))
        {
            CarriersById[carrier.Id] = carrier.Snapshot();
            return;
        }
        if (type is "CarrierStats" or "CarrierFinance" or "CarrierBankTransfer")
        {
            carrier.Balance = Long(finance, "CarrierBalance") ?? carrier.Balance;
            carrier.Reserve = Long(finance, "ReserveBalance") ?? carrier.Reserve;
            carrier.Available = Long(finance, "AvailableBalance") ?? carrier.Available;
            carrier.ReservePercent = Number(finance, "ReservePercent") ?? carrier.ReservePercent;
            carrier.TaxRate = Number(finance, "TaxRate") ?? carrier.TaxRate;
        }

        if (string.IsNullOrWhiteSpace(carrier.System) &&
            !string.IsNullOrWhiteSpace(state.CurrentSystem) &&
            !string.IsNullOrWhiteSpace(state.StationName) &&
            !string.IsNullOrWhiteSpace(carrier.Callsign) &&
            string.Equals(state.StationName, carrier.Callsign, StringComparison.OrdinalIgnoreCase))
        {
            carrier.System = state.CurrentSystem;
        }

        CarriersById[carrier.Id] = carrier.Snapshot();
    }
}

internal sealed record CarrierRow(string Label, string Value);
internal sealed record CarrierMetric(string Title, CarrierRow Left, CarrierRow Right, double? Percent);
internal sealed record CarrierDisplay(
    string Title, string Status, string FieldHeader, string ValueHeader,
    CarrierMetric Space, CarrierMetric Finance, CarrierMetric Fuel, string JumpTitle,
    IReadOnlyList<CarrierRow> Jump, bool HasCarrier, bool HasJump, IReadOnlyList<CarrierRow> Rows);

internal static class CarrierTranslations
{
    public static CarrierDisplay Present(CarrierState? carrier, string language)
    {
        var text = LanguageCatalog.GetSection(language, "carrier");
        string T(string key) => text.GetProperty(key).GetString() ?? throw new InvalidDataException(key);
        var culture = CultureInfo.GetCultureInfo(language);
        string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
        string Num(double? value, string suffix = "") => value.HasValue ? value.Value.ToString("N2", culture).TrimEnd('0').TrimEnd(culture.NumberFormat.NumberDecimalSeparator.ToCharArray()) + suffix : "—";
        string Int(long? value, string suffix = "") => value.HasValue ? value.Value.ToString("N0", culture) + suffix : "—";
        string Bool(bool? value) => value.HasValue ? T(value.Value ? "yes" : "no") : "—";
        string Date(DateTimeOffset? value) => value.HasValue ? value.Value.ToLocalTime().ToString("g", culture) : "—";
        CarrierRow Row(string key, string value) => new(T(key), value);
        const long fuelCapacity = 1000;
        if (carrier is null)
            return new(T("title"), T("waiting"), T("field"), T("value"),
                new(T("cargo"), Row("used", "—"), Row("free", "—"), null),
                new(T("financeTitle"), Row("reserve", "—"), Row("balance", "—"), null),
                new(T("fuel"), Row("fuel", "—"), Row("capacity", Int(fuelCapacity, " t")), null),
                T("jumpTitle"), Array.Empty<CarrierRow>(), false, false, Array.Empty<CarrierRow>());
        long? used = carrier.Capacity.HasValue && carrier.FreeSpace.HasValue &&
            carrier.Capacity.Value >= 0 && carrier.FreeSpace.Value >= 0 && carrier.FreeSpace.Value <= carrier.Capacity.Value
            ? carrier.Capacity.Value - carrier.FreeSpace.Value : null;
        double? spacePercent = used.HasValue && carrier.Capacity > 0
            ? 100d * used.Value / carrier.Capacity.Value : null;
        double? financePercent = carrier.Balance > 0 && carrier.Reserve >= 0 && carrier.Reserve <= carrier.Balance
            ? 100d * carrier.Reserve.Value / carrier.Balance.Value : null;
        double? fuelPercent = carrier.Fuel >= 0 && carrier.Fuel <= fuelCapacity
            ? 100d * carrier.Fuel.Value / fuelCapacity : null;
        var space = new CarrierMetric(T("cargo"), Row("used", Int(used, " t")), Row("free", Int(carrier.FreeSpace, " t")), spacePercent);
        var finance = new CarrierMetric(T("financeTitle"), Row("reserve", Int(carrier.Reserve, " CR")), Row("balance", Int(carrier.Balance, " CR")), financePercent);
        var fuel = new CarrierMetric(T("fuel"), Row("fuel", Int(carrier.Fuel, " t")), Row("capacity", Int(fuelCapacity, " t")), fuelPercent);
        var jump = new[] { Row("destination", Value(carrier.Destination)), Row("destinationBody", Value(carrier.DestinationBody)), Row("departure", Date(carrier.DepartureTime)) };
        bool hasJump = !string.IsNullOrWhiteSpace(carrier.Destination) || !string.IsNullOrWhiteSpace(carrier.DestinationBody) || carrier.DepartureTime.HasValue;
        var rows = new List<CarrierRow>();
        void Add(string key, string value) => rows.Add(new(T(key), value));
        Add("id", carrier.Id.ToString(culture)); Add("system", Value(carrier.System)); Add("body", Value(carrier.Body));
        Add("range", Num(carrier.JumpRange, " ly"));
        Add("rangeMax", Num(carrier.JumpRangeMax, " ly"));
        Add("capacity", Int(carrier.Capacity, " t"));
        Add("cargo", Int(carrier.Cargo, " t"));
        Add("available", Int(carrier.Available, " CR"));
        Add("reservePercent", Num(carrier.ReservePercent, " %"));
        Add("tax", Num(carrier.TaxRate, " %"));
        string accessKey = "access." + carrier.DockingAccess.ToLowerInvariant();
        Add("access", text.TryGetProperty(accessKey, out var access) ? access.GetString() ?? carrier.DockingAccess : Value(carrier.DockingAccess));
        Add("notorious", Bool(carrier.AllowNotorious)); Add("decommission", Bool(carrier.PendingDecommission));
        Add("updated", Date(carrier.UpdatedAt));
        return new($"{Value(carrier.Name)} ({Value(carrier.Callsign)})", T("note"), T("field"), T("value"),
            space, finance, fuel, T("jumpTitle"), jump, true, hasJump, rows);
    }
}
