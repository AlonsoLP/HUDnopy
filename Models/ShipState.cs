// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Models/ShipState.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Live commander and ship telemetry snapshot plus Status.json flag enums.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace HUDnopy.Models;

[Flags]
public enum EliteStatusFlags : uint
{
    None = 0,
    Docked = 1u << 0,
    Landed = 1u << 1,
    LandingGearDown = 1u << 2,
    ShieldsUp = 1u << 3,
    Supercruise = 1u << 4,
    FlightAssistOff = 1u << 5,
    HardpointsDeployed = 1u << 6,
    InWing = 1u << 7,
    LightsOn = 1u << 8,
    CargoScoopDeployed = 1u << 9,
    SilentRunning = 1u << 10,
    ScoopingFuel = 1u << 11,
    SrvHandbrake = 1u << 12,
    SrvUsingTurretView = 1u << 13,
    SrvTurretRetracted = 1u << 14,
    SrvDriveAssist = 1u << 15,
    FsdMassLocked = 1u << 16,
    FsdCharging = 1u << 17,
    FsdCooldown = 1u << 18,
    LowFuel = 1u << 19,
    OverHeating = 1u << 20,
    HasLatLong = 1u << 21,
    IsInDanger = 1u << 22,
    BeingInterdicted = 1u << 23,
    InMainShip = 1u << 24,
    InFighter = 1u << 25,
    InSRV = 1u << 26,
    HudInAnalysisMode = 1u << 27,
    NightVision = 1u << 28,
    AltitudeFromAverageRadius = 1u << 29,
    FsdJump = 1u << 30,
    SrvHighBeam = 1u << 31
}

[Flags]
public enum EliteStatusFlags2 : uint
{
    None = 0,
    OnFoot = 1u << 0,
    InTaxi = 1u << 1,
    InMulticrew = 1u << 2,
    OnFootInStation = 1u << 3,
    OnFootOnPlanet = 1u << 4,
    AimDownSight = 1u << 5,
    LowOxygen = 1u << 6,
    LowHealth = 1u << 7,
    Cold = 1u << 8,
    Hot = 1u << 9,
    VeryCold = 1u << 10,
    VeryHot = 1u << 11,
    GlideMode = 1u << 12,
    OnFootInHangar = 1u << 13,
    OnFootSocialSpace = 1u << 14,
    OnFootExterior = 1u << 15,
    BreathableAtmosphere = 1u << 16,
    TelepresenceMulticrew = 1u << 17,
    PhysicalMulticrew = 1u << 18,
    FsdHyperdriveCharging = 1u << 19,
    SupercruiseOverdriveActive = 1u << 20,
    SupercruiseAssistActive = 1u << 21
}

public class ShipState
{
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public bool IsGameDetected { get; set; }
    public bool IsSimulation { get; set; }

    // Comandante y Nave (Journal)
    public string CommanderName { get; set; } = "CMDR DESCONOCIDO";
    public string ShipType { get; set; } = "---";
    public string ShipName { get; set; } = "SIN SEÑAL";
    public string ShipIdent { get; set; } = "---";
    public double HullHealth { get; set; }
    public bool HasHullTelemetry { get; set; }
    public long Credits { get; set; }
    public string LegalState { get; set; } = "---";
    public int CargoCapacity { get; set; }
    public string CargoVessel { get; set; } = "";
    public DateTimeOffset? CargoTimestamp { get; set; }

    // Ubicación y Navegación
    public string CurrentSystem { get; set; } = "---";
    public string SecurityLevel { get; set; } = "---";
    public string Economy { get; set; } = "---";
    public string Allegiance { get; set; } = "---";
    public bool AllegianceIsFallback { get; set; }
    public string StationName { get; set; } = "";
    public string BodyName { get; set; } = "";
    public string DestinationName { get; set; } = "";
    public string DestinationSystem { get; set; } = "";
    public string DestinationBody { get; set; } = "";
    public string SystemAddress { get; set; } = "";
    public double StarPosX { get; set; }
    public double StarPosY { get; set; }
    public double StarPosZ { get; set; }

    // Estado en tiempo real (Status.json)
    public uint RawFlags { get; set; }
    public uint RawFlags2 { get; set; }
    public bool HasStatusTelemetry { get; set; }
    public bool HasOnFootTelemetry { get; set; }
    public double? Oxygen { get; set; }
    public double? Health { get; set; }
    public double? TemperatureKelvin { get; set; }
    public double? Gravity { get; set; }
    public string SelectedWeapon { get; set; } = "";
    [JsonIgnore]
    public string OxygenDisplay => FormatPercent(Oxygen);
    [JsonIgnore]
    public string HealthDisplay => FormatPercent(Health);
    [JsonIgnore]
    public string TemperatureDisplay => TemperatureKelvin.HasValue ? $"{FormatTelemetry(TemperatureKelvin)} K" : "---";
    [JsonIgnore]
    public string GravityDisplay => Gravity.HasValue ? $"{FormatTelemetry(Gravity)} G" : "---";
    [JsonIgnore]
    public string OnFootStatusDisplay
    {
        get
        {
            var statuses = new List<string>();
            if (OnFoot) statuses.Add("A pie");
            if (InTaxi) statuses.Add("En taxi");
            if (InMulticrew) statuses.Add("En multicrew");
            if (OnFootInStation) statuses.Add("En estación");
            if (OnFootOnPlanet) statuses.Add("En planeta");
            if (AimDownSight) statuses.Add("Apuntando");
            if (LowOxygen) statuses.Add("Poco oxígeno");
            if (LowHealth) statuses.Add("Salud baja");
            if (VeryCold) statuses.Add("Frío extremo");
            else if (Cold) statuses.Add("Frío");
            if (VeryHot) statuses.Add("Calor extremo");
            else if (Hot) statuses.Add("Calor");
            if (GlideMode) statuses.Add("Planeando");
            if (OnFootInHangar) statuses.Add("En hangar");
            if (OnFootSocialSpace) statuses.Add("Espacio social");
            if (OnFootExterior) statuses.Add("Exterior");
            if (BreathableAtmosphere) statuses.Add("Atmósfera respirable");
            if (TelepresenceMulticrew) statuses.Add("Multicrew telepresencial");
            if (PhysicalMulticrew) statuses.Add("Multicrew físico");
            if (FsdHyperdriveCharging) statuses.Add("Cargando hiperespacio");
            if (SupercruiseOverdriveActive) statuses.Add("SCO activo");
            if (SupercruiseAssistActive) statuses.Add("Asistente de supercrucero activo");
            return statuses.Count == 0 ? "Sin estado a pie" : string.Join(" · ", statuses);
        }
    }
    public bool HasShieldTelemetry { get; set; }
    public bool HasLoadoutTelemetry { get; set; }
    public bool HasShieldGenerator { get; set; }
    public bool CockpitBreached { get; set; }

    public bool Docked => HasFlag(EliteStatusFlags.Docked);
    public bool Landed => HasFlag(EliteStatusFlags.Landed);
    public bool LandingGearDown => HasFlag(EliteStatusFlags.LandingGearDown);
    public bool ShieldsUp => HasFlag(EliteStatusFlags.ShieldsUp);
    public bool Supercruise => HasFlag(EliteStatusFlags.Supercruise);
    public bool FlightAssistOff => HasFlag(EliteStatusFlags.FlightAssistOff);
    public bool HardpointsDeployed => HasFlag(EliteStatusFlags.HardpointsDeployed);
    public bool InWing => HasFlag(EliteStatusFlags.InWing);
    public bool LightsOn => HasFlag(EliteStatusFlags.LightsOn);
    public bool CargoScoopDeployed => HasFlag(EliteStatusFlags.CargoScoopDeployed);
    public bool SilentRunning => HasFlag(EliteStatusFlags.SilentRunning);
    public bool ScoopingFuel => HasFlag(EliteStatusFlags.ScoopingFuel);
    public bool FsdMassLocked => HasFlag(EliteStatusFlags.FsdMassLocked);
    public bool FsdCharging => HasFlag(EliteStatusFlags.FsdCharging);
    public bool FsdCooldown => HasFlag(EliteStatusFlags.FsdCooldown);
    public bool LowFuel => HasFlag(EliteStatusFlags.LowFuel);
    public bool OverHeating => HasFlag(EliteStatusFlags.OverHeating);
    public bool IsInDanger => HasFlag(EliteStatusFlags.IsInDanger);
    public bool BeingInterdicted => HasFlag(EliteStatusFlags.BeingInterdicted);
    public bool InMainShip => HasFlag(EliteStatusFlags.InMainShip);
    public bool InFighter => HasFlag(EliteStatusFlags.InFighter);
    public bool InSRV => HasFlag(EliteStatusFlags.InSRV);
    public bool SrvHandbrake => HasFlag(EliteStatusFlags.SrvHandbrake);
    public bool SrvUsingTurretView => HasFlag(EliteStatusFlags.SrvUsingTurretView);
    public bool SrvTurretRetracted => HasFlag(EliteStatusFlags.SrvTurretRetracted);
    public bool SrvDriveAssist => HasFlag(EliteStatusFlags.SrvDriveAssist);
    public bool SrvHighBeam => HasFlag(EliteStatusFlags.SrvHighBeam);
    public bool AnalysisMode => HasFlag(EliteStatusFlags.HudInAnalysisMode);
    public bool NightVision => HasFlag(EliteStatusFlags.NightVision);
    public bool FsdJump => HasFlag(EliteStatusFlags.FsdJump);

    public bool OnFoot => HasFlag2(EliteStatusFlags2.OnFoot);
    public bool InTaxi => HasFlag2(EliteStatusFlags2.InTaxi);
    public bool InMulticrew => HasFlag2(EliteStatusFlags2.InMulticrew);
    public bool OnFootInStation => HasFlag2(EliteStatusFlags2.OnFootInStation);
    public bool OnFootOnPlanet => HasFlag2(EliteStatusFlags2.OnFootOnPlanet);
    public bool AimDownSight => HasFlag2(EliteStatusFlags2.AimDownSight);
    public bool LowOxygen => HasFlag2(EliteStatusFlags2.LowOxygen);
    public bool LowHealth => HasFlag2(EliteStatusFlags2.LowHealth);
    public bool Cold => HasFlag2(EliteStatusFlags2.Cold);
    public bool Hot => HasFlag2(EliteStatusFlags2.Hot);
    public bool VeryCold => HasFlag2(EliteStatusFlags2.VeryCold);
    public bool VeryHot => HasFlag2(EliteStatusFlags2.VeryHot);
    public bool GlideMode => HasFlag2(EliteStatusFlags2.GlideMode);
    public bool OnFootInHangar => HasFlag2(EliteStatusFlags2.OnFootInHangar);
    public bool OnFootSocialSpace => HasFlag2(EliteStatusFlags2.OnFootSocialSpace);
    public bool OnFootExterior => HasFlag2(EliteStatusFlags2.OnFootExterior);
    public bool BreathableAtmosphere => HasFlag2(EliteStatusFlags2.BreathableAtmosphere);
    public bool TelepresenceMulticrew => HasFlag2(EliteStatusFlags2.TelepresenceMulticrew);
    public bool PhysicalMulticrew => HasFlag2(EliteStatusFlags2.PhysicalMulticrew);
    public bool FsdHyperdriveCharging => HasFlag2(EliteStatusFlags2.FsdHyperdriveCharging);
    public bool SupercruiseOverdriveActive => HasFlag2(EliteStatusFlags2.SupercruiseOverdriveActive);
    public bool SupercruiseAssistActive => HasFlag2(EliteStatusFlags2.SupercruiseAssistActive);

    // Pips de energía (en medios puntos de Status.json, aquí convertidos a 0.0 - 4.0)
    public double PipsSys { get; set; }
    public double PipsEng { get; set; }
    public double PipsWep { get; set; }

    public int FireGroup { get; set; } = 0;
    public int GuiFocus { get; set; } = 0;

    // Combustible y Carga
    public double FuelMain { get; set; }
    public double FuelMax { get; set; }
    public bool HasFuelCapacity { get; set; }
    public bool HasFuelTelemetry { get; set; }
    public double FuelReservoir { get; set; }
    public int CargoUsed { get; set; } = 0;

    // Telemetría planetaria
    public bool HasLatLong => HasFlag(EliteStatusFlags.HasLatLong);
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }
    public double Heading { get; set; }
    public double PlanetRadius { get; set; }

    // Ruta de navegación (NavRoute.json)
    public List<RouteWaypoint> NavRoute { get; set; } = new();

    // Inventario de carga (Cargo.json)
    public List<CargoItem> CargoInventory { get; set; } = new();
    public bool HasMaterialsTelemetry { get; set; }
    public List<MaterialItem> MaterialsInventory { get; set; } = new();
    public List<MissionItem> ActiveMissions { get; set; } = new();
    public List<ExplorationBodyItem> ExplorationBodies { get; set; } = new();
    public List<ProfileValueItem> CommanderProfile { get; set; } = new();
    public List<PassengerItem> PassengerManifest { get; set; } = new();
    public List<BackpackItem> BackpackInventory { get; set; } = new();
    public string ActiveSuit { get; set; } = "";
    public string ActiveSuitLoadout { get; set; } = "";
    public ColonisationSnapshot Colonisation { get; set; } = new();
    public MarketSnapshot Market { get; set; } = new();
    public OutfittingSnapshot Outfitting { get; set; } = new();
    public ShipyardSnapshot Shipyard { get; set; } = new();
    public CarrierState? Carrier { get; set; }

    // Exploración avanzada y exobiología (Categoría A)
    public int? SystemTotalBodies { get; set; }
    public int SystemScannedBodies { get; set; }
    public bool SystemAllBodiesFound { get; set; }
    public long UnsoldCartographyCredits { get; set; }
    public long UnsoldExobiologyCredits { get; set; }
    public ExobiologyTrackerState? ExobiologyTracker { get; set; }

    // Copiloto táctico de vuelo y seguridad (Categoría B)
    public bool HasLimpetController { get; set; }
    public int LimpetCount { get; set; }
    public double? FsdHealthPercent { get; set; }
    public bool IsFsdSupercharged { get; set; }
    public string JumpTargetSystem { get; set; } = "";
    public string JumpTargetStarClass { get; set; } = "";
    public bool JumpTargetIsScoopable { get; set; }
    public bool JumpTargetIsHazardous { get; set; }
    public string ApproachBodyName { get; set; } = "";
    public double? ApproachBodyGravityG { get; set; }
    public int? JumpsToNextFuelStar { get; set; }
    public bool RouteHasNoFuelStar { get; set; }

    // Módulos instalados, taquilla Odyssey e ingenieros (Categorías C y D)
    public List<ShipModuleItem> ShipModules { get; set; } = new();
    public List<BackpackItem> ShipLockerInventory { get; set; } = new();
    public List<EngineerProgressItem> Engineers { get; set; } = new();

    // Minería y navegación de superficie (Categorías E y F)
    public ProspectedAsteroidState? LastProspectedAsteroid { get; set; }
    public MiningSessionState MiningSession { get; set; } = new();
    public List<SurfaceEventItem> RecentSurfaceEvents { get; set; } = new();

    // Combate avanzado y masacres (Categoría G)
    public TargetShipState? CurrentTarget { get; set; }
    public List<MassacreStackItem> MassacreStacks { get; set; } = new();
    public CombatSessionState CombatSession { get; set; } = new();

    // Portanaves Bartender, BGS/Powerplay, Sesión y Notas (Categorías I y J)
    public List<CarrierMaterialItem> CarrierMaterials { get; set; } = new();
    public string CarrierMaterialsStation { get; set; } = "";
    public List<SystemFactionItem> SystemFactions { get; set; } = new();
    public string PowerplaySummary { get; set; } = "";
    public SessionStatsState SessionStats { get; set; } = new();
    public string CurrentSystemNote { get; set; } = "";
    public List<StoredShipItem> StoredShips { get; set; } = new();
    public List<CommunityGoalItem> CommunityGoals { get; set; } = new();
    public List<CarrierCargoItem> CarrierCargo { get; set; } = new();

    // Metadatos de sesión y versión del juego (para EDDN / EDSM / Inara)
    public string GameVersion { get; set; } = "";
    public string GameBuild { get; set; } = "";
    public bool IsHorizons { get; set; } = true;
    public bool IsOdyssey { get; set; }
    public bool IsLegacyGalaxy { get; set; }
    public double[]? StarPos { get; set; }
    public long? ShipId { get; set; }
    public string ShipTypeInternal { get; set; } = "";

    // Eventos recientes del Diario
    public List<JournalEntryItem> RecentEvents { get; set; } = new();

    // Tema actual para sincronizar con la Web
    public string ThemeAccentHex { get; set; } = "#FF7100";
    public string ThemeSecondaryHex { get; set; } = "#FFB300";

    internal ShipState Snapshot() => (ShipState)MemberwiseClone();

    private bool HasFlag(EliteStatusFlags flag) => ((EliteStatusFlags)RawFlags).HasFlag(flag);
    private bool HasFlag2(EliteStatusFlags2 flag) => ((EliteStatusFlags2)RawFlags2).HasFlag(flag);
    private static string FormatTelemetry(double? value) =>
        value?.ToString("0.##", CultureInfo.GetCultureInfo("es-ES")) ?? "---";
    private static string FormatPercent(double? value) =>
        value.HasValue ? $"{(value.Value * 100).ToString("0.#", CultureInfo.GetCultureInfo("es-ES"))}%" : "---";
}

public class RouteWaypoint
{
    internal RouteWaypoint Snapshot() => (RouteWaypoint)MemberwiseClone();
    public int Index { get; set; }
    public string StarSystem { get; set; } = "";
    public string StarClass { get; set; } = "";
    public double DistanceFromStartLy { get; set; }
    public double StepDistanceLy { get; set; }
    public bool IsFuelStar => StarClass is "K" or "G" or "B" or "F" or "O" or "A" or "M";
    public string FuelStarIndicator => IsFuelStar ? "\u2713" : "\u2715";
    public bool IsCurrentSystem { get; set; }
}
