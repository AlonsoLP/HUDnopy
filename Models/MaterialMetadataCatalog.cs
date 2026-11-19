// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Models/MaterialMetadataCatalog.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Static catalog of Elite Dangerous material categories, grades, and caps.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace HUDnopy.Models;

/// <summary>
/// Static catalog of Elite Dangerous material categories, grades, and caps.
/// </summary>
public static class MaterialMetadataCatalog
{
    // Grade values follow EDCD/EDDI's Journal material definitions (MIT):
    // https://github.com/EDCD/EDDI/blob/develop/DataDefinitions/Material.cs
    private static readonly Dictionary<string, int> Grades = new(StringComparer.OrdinalIgnoreCase)
    {
        ["raw:carbon"] = 1, ["raw:iron"] = 1, ["raw:lead"] = 1, ["raw:nickel"] = 1,
        ["raw:phosphorus"] = 1, ["raw:rhenium"] = 1, ["raw:sulphur"] = 1,
        ["raw:arsenic"] = 2, ["raw:chromium"] = 2, ["raw:germanium"] = 2, ["raw:manganese"] = 2,
        ["raw:vanadium"] = 2, ["raw:zinc"] = 2, ["raw:zirconium"] = 2,
        ["raw:boron"] = 3, ["raw:cadmium"] = 3, ["raw:mercury"] = 3, ["raw:molybdenum"] = 3,
        ["raw:niobium"] = 3, ["raw:tin"] = 3, ["raw:tungsten"] = 3,
        ["raw:ruthenium"] = 4, ["raw:selenium"] = 4, ["raw:technetium"] = 4,
        ["raw:tellurium"] = 4, ["raw:yttrium"] = 4, ["raw:antimony"] = 4, ["raw:polonium"] = 4,

        ["encoded:bulkscandata"] = 1, ["encoded:disruptedwakeechoes"] = 1,
        ["encoded:shieldcyclerecordings"] = 1, ["encoded:scrambledemissiondata"] = 1,
        ["encoded:legacyfirmware"] = 1, ["encoded:encryptedfiles"] = 1,
        ["encoded:fsdtelemetry"] = 2, ["encoded:shieldsoakanalysis"] = 2,
        ["encoded:archivedemissiondata"] = 2, ["encoded:consumerfirmware"] = 2,
        ["encoded:encryptioncodes"] = 2, ["encoded:scanarchives"] = 2,
        ["encoded:tg_structuraldata"] = 2,
        ["encoded:scandatabanks"] = 3, ["encoded:industrialfirmware"] = 3,
        ["encoded:symmetrickeys"] = 3, ["encoded:wakesolutions"] = 3,
        ["encoded:emissiondata"] = 3, ["encoded:shielddensityreports"] = 3,
        ["encoded:tg_compositiondata"] = 3, ["encoded:unknownshipsignature"] = 3,
        ["encoded:shieldpatternanalysis"] = 4, ["encoded:encryptionarchives"] = 4,
        ["encoded:decodedemissiondata"] = 4, ["encoded:encodedscandata"] = 4,
        ["encoded:hyperspacetrajectories"] = 4, ["encoded:securityfirmware"] = 4,
        ["encoded:ancienthistoricaldata"] = 4, ["encoded:ancientculturaldata"] = 4,
        ["encoded:ancientbiologicaldata"] = 4, ["encoded:ancientlanguagedata"] = 4,
        ["encoded:ancienttechnologicaldata"] = 4, ["encoded:tg_shipflightdata"] = 4,
        ["encoded:tg_shipsystemsdata"] = 4, ["encoded:tg_residuedata"] = 4,
        ["encoded:unknownwakedata"] = 4, ["encoded:guardian_weaponblueprint"] = 4,
        ["encoded:guardian_moduleblueprint"] = 4,
        ["encoded:compactemissionsdata"] = 5, ["encoded:adaptiveencryptors"] = 5,
        ["encoded:classifiedscandata"] = 5, ["encoded:dataminedwake"] = 5,
        ["encoded:embeddedfirmware"] = 5, ["encoded:shieldfrequencydata"] = 5,
        ["encoded:guardian_vesselblueprint"] = 5,

        ["manufactured:basicconductors"] = 1, ["manufactured:chemicalstorageunits"] = 1,
        ["manufactured:compactcomposites"] = 1, ["manufactured:crystalshards"] = 1,
        ["manufactured:gridresistors"] = 1, ["manufactured:heatconductionwiring"] = 1,
        ["manufactured:mechanicalscrap"] = 1, ["manufactured:salvagedalloys"] = 1,
        ["manufactured:temperedalloys"] = 1, ["manufactured:wornshieldemitters"] = 1,
        ["manufactured:guardian_powercell"] = 1, ["manufactured:guardian_sentinel_wreckagecomponents"] = 1,
        ["manufactured:chemicalprocessors"] = 2, ["manufactured:conductivecomponents"] = 2,
        ["manufactured:filamentcomposites"] = 2, ["manufactured:uncutfocuscrystals"] = 2,
        ["manufactured:galvanisingalloys"] = 2, ["manufactured:heatdispersionplate"] = 2,
        ["manufactured:heatresistantceramics"] = 2, ["manufactured:hybridcapacitors"] = 2,
        ["manufactured:mechanicalequipment"] = 2, ["manufactured:shieldemitters"] = 2,
        ["manufactured:tg_causticshard"] = 2, ["manufactured:guardian_powerconduit"] = 2,
        ["manufactured:unknowncorechip"] = 2, ["manufactured:unknowncarapace"] = 2,
        ["manufactured:chemicaldistillery"] = 3, ["manufactured:conductiveceramics"] = 3,
        ["manufactured:electrochemicalarrays"] = 3, ["manufactured:focuscrystals"] = 3,
        ["manufactured:heatexchangers"] = 3, ["manufactured:highdensitycomposites"] = 3,
        ["manufactured:mechanicalcomponents"] = 3, ["manufactured:phasealloys"] = 3,
        ["manufactured:precipitatedalloys"] = 3, ["manufactured:shieldingsensors"] = 3,
        ["manufactured:tg_causticgeneratorparts"] = 3,
        ["manufactured:guardian_sentinel_weaponparts"] = 3, ["manufactured:guardian_techcomponent"] = 3,
        ["manufactured:tg_shutdowndata"] = 3, ["manufactured:tg_biomechanicalconduits"] = 3,
        ["manufactured:unknownenergycell"] = 3, ["manufactured:tg_wreckagecomponents"] = 3,
        ["manufactured:chemicalmanipulators"] = 4, ["manufactured:compoundshielding"] = 4,
        ["manufactured:conductivepolymers"] = 4, ["manufactured:configurablecomponents"] = 4,
        ["manufactured:heatvanes"] = 4, ["manufactured:polymercapacitors"] = 4,
        ["manufactured:fedproprietarycomposites"] = 4, ["manufactured:protolightalloys"] = 4,
        ["manufactured:refinedfocuscrystals"] = 4, ["manufactured:thermicalloys"] = 4,
        ["manufactured:tg_causticcrystal"] = 4, ["manufactured:unknowntechnologycomponents"] = 4,
        ["manufactured:tg_weaponparts"] = 4,
        ["manufactured:biotechconductors"] = 5, ["manufactured:fedcorecomposites"] = 5,
        ["manufactured:exquisitefocuscrystals"] = 5, ["manufactured:imperialshielding"] = 5,
        ["manufactured:improvisedcomponents"] = 5, ["manufactured:militarygradealloys"] = 5,
        ["manufactured:militarysupercapacitors"] = 5, ["manufactured:pharmaceuticalisolators"] = 5,
        ["manufactured:protoheatradiators"] = 5, ["manufactured:protoradiolicalloys"] = 5,
        ["manufactured:tg_propulsionelement"] = 5, ["manufactured:unknownenergysource"] = 5,
        ["manufactured:unknownorganiccircuitry"] = 5
    };

    public static bool TryGetGrade(string category, string name, out int grade) =>
        Grades.TryGetValue($"{category}:{name}", out grade);

    public static int GetMaxCapacity(int? grade) => grade switch
    {
        1 => 300,
        2 => 250,
        3 => 200,
        4 => 150,
        5 => 100,
        _ => 100
    };
}
