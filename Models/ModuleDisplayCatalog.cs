// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Models/ModuleDisplayCatalog.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Multilingual in-game decoder for internal Elite Dangerous module and hardpoint IDs.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace HUDnopy.Models;

/// <summary>
/// Converts internal Frontier module IDs (e.g. <c>hpt_plasmaaccelerator_fixed_huge</c> or
/// <c>int_hyperdrive_overcharge_size5_class5</c>) into official in-game localized names across
/// all 6 supported HUDnopy languages (es-ES, en-GB, fr-FR, de-DE, ru-RU, pt-BR).
/// </summary>
public static class ModuleDisplayCatalog
{
    private static readonly Regex SizeRegex = new(@"_size(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ClassRegex = new(@"_class(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ArmourRegex = new(@"^([a-z0-9_]+?)_armour_(grade1|grade2|grade3|mirrored|reactive)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (string Key, string En, string Es, string Fr, string De, string Ru, string Pt)[] Catalog =
    [
        // Core & FSD
        ("hyperdrive_overcharge", "Frame Shift Drive (SCO)", "Motor de distorsión (SCO)", "Réacteur FSD (SCO)", "Frameshift-Antrieb (SCO)", "Рамочно-сместительный двигатель (SCO)", "Motor de Dobra (SCO)"),
        ("hyperdrive", "Frame Shift Drive", "Motor de distorsión", "Réacteur FSD", "Frameshift-Antrieb", "Рамочно-сместительный двигатель", "Motor de Dobra"),
        ("powerplant", "Power Plant", "Núcleo de energía", "Générateur", "Kraftwerk", "Силовая установка", "Usina de Energia"),
        ("powerdistributor", "Power Distributor", "Distribuidor de energía", "Répartiteur de puissance", "Energieverteiler", "Распределитель питания", "Distribuidor de Energia"),
        ("engine", "Thrusters", "Impulsores", "Propulseurs", "Schubdüsen", "Маневровые двигатели", "Propulsores"),
        ("lifesupport", "Life Support", "Soporte vital", "Système de survie", "Lebenserhaltung", "Система жизнеобеспечения", "Suporte de Vida"),
        ("sensors", "Sensors", "Sensores", "Capteurs", "Sensoren", "Сенсоры", "Sensores"),
        ("fueltank", "Fuel Tank", "Depósito de combustible", "Réservoir de carburant", "Treibstofftank", "Топливный бак", "Tanque de Combustível"),

        // Optional Internals
        ("corrosionproofcargorack", "Corrosion Resistant Cargo Rack", "Bodega resistente a la corrosión", "Soute résistante à la corrosion", "Korrosionsbeständiges Frachtgestell", "Антикоррозийный грузовой стеллаж", "Compartimento de Carga Anticorrosivo"),
        ("cargorack", "Cargo Rack", "Bodega de carga", "Soute", "Frachtgestell", "Грузовой стеллаж", "Compartimento de Carga"),
        ("fuelscoop", "Fuel Scoop", "Colector de combustible", "Récupérateur de carburant", "Treibstoffsammler", "Топливозаборник", "Coletor de Combustível"),
        ("shieldgenerator_strong", "Prismatic Shield Generator", "Generador de escudos prismático", "Générateur de bouclier prismatique", "Prismatischer Schildgenerator", "Призматический генератор щита", "Gerador de Escudo Prismático"),
        ("shieldgenerator_fast", "Bi-Weave Shield Generator", "Generador de escudos bi-entrelazado", "Générateur de bouclier entrelacé", "Bi-Wave-Schildgenerator", "Двухпоточный генератор щита", "Gerador de Escudo Bi-Trançado"),
        ("shieldgenerator", "Shield Generator", "Generador de escudos", "Générateur de bouclier", "Schildgenerator", "Генератор щита", "Gerador de Escudo"),
        ("shieldcellbank", "Shield Cell Bank", "Célula de energía de escudo", "Cellule d'énergie de bouclier", "Schildzellenbank", "Щитонакопитель", "Banco de Células de Escudo"),
        ("guardianfsdbooster", "Guardian FSD Booster", "Acelerador de MDD Guardián", "Survolteur FSD Guardian", "Guardian-FSA-Booster", "Ускоритель РСД Стражей", "Amplificador de Motor de Dobra Guardião"),
        ("guardianshieldreinforcement", "Guardian Shield Reinforcement", "Refuerzo de escudo Guardián", "Renfort de bouclier Guardian", "Guardian-Schildverstärkung", "Усилитель щита Стражей", "Reforço de Escudo Guardião"),
        ("guardianhullreinforcement", "Guardian Hull Reinforcement", "Refuerzo de casco Guardián", "Renfort de coque Guardian", "Guardian-Rumpfverstärkung", "Усилитель корпуса Стражей", "Reforço de Casco Guardião"),
        ("guardianmodulereinforcement", "Guardian Module Reinforcement", "Refuerzo de módulo Guardián", "Renfort de module Guardian", "Guardian-Modulverstärkung", "Усилитель модулей Стражей", "Reforço de Módulo Guardião"),
        ("metaalloyhullreinforcement", "Meta-Alloy Hull Reinforcement", "Refuerzo de casco de meta-aleación", "Renfort de coque en méta-alliage", "Meta-Legierungs-Rumpfverstärkung", "Усилитель корпуса из метасплавов", "Reforço de Casco de Meta-Liga"),
        ("hullreinforcement", "Hull Reinforcement Package", "Paquete de refuerzo de casco", "Pack de renfort de coque", "Rumpfverstärkungspaket", "Набор для усиления корпуса", "Pacote de Reforço de Casco"),
        ("modulereinforcement", "Module Reinforcement Package", "Paquete de refuerzo de módulo", "Pack de renfort de module", "Modulverstärkungspaket", "Набор для усиления модулей", "Pacote de Reforço de Módulo"),
        ("refinery", "Refinery", "Refinería", "Raffinerie", "Raffinerie", "Очиститель", "Refinaria"),
        ("repairer", "Auto Field-Maintenance Unit (AFMU)", "Unidad de automantenimiento (UAM)", "Unité de maintenance de terrain (UMT)", "Automatische Feldwartungseinheit (AFWE)", "Блок авторемонта (АПР)", "Unidade de Automanutenção (UAM)"),
        ("buggybay", "Planetary Vehicle Hangar (SRV)", "Hangar para vehículos planetarios (VRS)", "Hangar à véhicules planétaires (VRS)", "Planetenfahrzeughangar (SRV)", "Ангар для планетарного транспорта (ТРП)", "Hangar de Veículo Planetário (VRS)"),
        ("fighterbay", "Fighter Hangar", "Hangar de cazas", "Hangar à chasseurs", "Jägerhangar", "Ангар для истребителей", "Hangar de Caças"),
        ("passengercabin", "Passenger Cabin", "Cabina de pasajeros", "Cabine de passagers", "Passagierkabine", "Пассажирская каюта", "Cabine de Passageiros"),
        ("detailedsurfacescanner", "Detailed Surface Scanner (DSS)", "Escáner de superficie detallado (ESD)", "Détecteur de surface détaillé", "Detailoberflächenscanner", "Подробный сканер поверхности", "Scanner de Superfície Detalhado"),
        ("dockingcomputer_advanced", "Advanced Docking Computer", "Computadora de aterrizaje avanzada", "Ordinateur d'appontage avancé", "Verbesserter Landecomputer", "Улучшенный стыковочный компьютер", "Computador de Atracagem Avançado"),
        ("dockingcomputer_standard", "Standard Docking Computer", "Computadora de aterrizaje estándar", "Ordinateur d'appontage standard", "Standard-Landecomputer", "Стандартный стыковочный компьютер", "Computador de Atracagem Padrão"),
        ("supercruiseassist", "Supercruise Assist", "Asistente de supercrucero", "Aide au super-navigation", "Supercruise-Assistent", "Помощник в суперкруизе", "Assistente de Supercruzeiro"),
        ("planetapproachsuite", "Planetary Approach Suite", "Módulo de aproximación planetaria", "Module d'approche planétaire", "Planetenanflug-Suite", "Модуль планетарного спуска", "Módulo de Aproximação Planetária"),
        ("fsdinterdictor", "Frame Shift Drive Interdictor", "Interdictor de MDD", "Intercepteur FSD", "Frameshift-Antriebs-Unterbrecher", "Перехватчик РСД", "Interdictor de Motor de Dobra"),
        ("multidronecontrol_mining", "Mining Multi-Limpet Controller", "Controlador multidrones de minería", "Contrôleur multi-drones de minage", "Bergbau-Multidrohnencontroller", "Универсальный контроллер дронов (добыча)", "Controlador Multi-Drones de Mineração"),
        ("multidronecontrol_operations", "Operations Multi-Limpet Controller", "Controlador multidrones de operaciones", "Contrôleur multi-drones d'opérations", "Einsatz-Multidrohnencontroller", "Универсальный контроллер дронов (операции)", "Controlador Multi-Drones de Operações"),
        ("multidronecontrol_rescue", "Rescue Multi-Limpet Controller", "Controlador multidrones de rescate", "Contrôleur multi-drones de sauvetage", "Rettungs-Multidrohnencontroller", "Универсальный контроллер дронов (спасение)", "Controlador Multi-Drones de Resgate"),
        ("multidronecontrol_universal", "Universal Multi-Limpet Controller", "Controlador multidrones universal", "Contrôleur multi-drones universel", "Universal-Multidrohnencontroller", "Универсальный контроллер дронов", "Controlador Multi-Drones Universal"),
        ("multidronecontrol_xeno", "Xeno Multi-Limpet Controller", "Controlador multidrones xeno", "Contrôleur multi-drones xéno", "Xeno-Multidrohnencontroller", "Ксено-контроллер дронов", "Controlador Multi-Drones Xeno"),
        ("dronecontrol_collection", "Collector Limpet Controller", "Controlador de drones recolectores", "Contrôleur de drones collecteurs", "Sammeldrohnencontroller", "Контроллер дронов-сборщиков", "Controlador de Drones Coletores"),
        ("dronecontrol_prospector", "Prospector Limpet Controller", "Controlador de drones prospectores", "Contrôleur de drones prospecteurs", "Erzsucherdrohnencontroller", "Контроллер дронов-геологоразведчиков", "Controlador de Drones Prospectores"),
        ("dronecontrol_repair", "Repair Limpet Controller", "Controlador de drones reparadores", "Contrôleur de drones réparateurs", "Reparaturdrohnencontroller", "Контроллер дронов-ремонтников", "Controlador de Drones de Reparo"),
        ("dronecontrol_fueltransfer", "Fuel Transfer Limpet Controller", "Controlador de drones de combustible", "Contrôleur de drones de ravitaillement", "Treibstofftransfer-Drohnencontroller", "Контроллер дронов-заправщиков", "Controlador de Drones de Combustível"),
        ("dronecontrol_hatchbreaker", "Hatch Breaker Limpet Controller", "Controlador de drones extractores", "Contrôleur de drones perce-soute", "Lukenöffnerdrohnencontroller", "Контроллер дронов-взломщиков трюма", "Controlador de Drones Arrombadores"),
        ("dronecontrol_decontamination", "Decontamination Limpet Controller", "Controlador de drones descontaminadores", "Contrôleur de drones décontaminants", "Dekontaminierungsdrohnencontroller", "Контроллер дронов-очистителей", "Controlador de Drones de Descontaminação"),
        ("dronecontrol_recon", "Recon Limpet Controller", "Controlador de drones de reconocimiento", "Contrôleur de drones de reconnaissance", "Aufklärungsdrohnencontroller", "Контроллер дронов-разведчиков", "Controlador de Drones de Reconhecimento"),
        ("dronecontrol_resourcesiphon", "Research Limpet Controller", "Controlador de drones de investigación", "Contrôleur de drones de recherche", "Forschungsdrohnencontroller", "Контроллер дронов-исследователей", "Controlador de Drones de Pesquisa"),

        // Weapons (Hardpoints)
        ("plasmaaccelerator", "Plasma Accelerator", "Acelerador de plasma", "Accélérateur à plasma", "Plasmabeschleuniger", "Ускоритель плазмы", "Acelerador de Plasma"),
        ("pulselaserburst", "Burst Laser", "Láser de ráfaga", "Laser à rafales", "Salvenlaser", "Пучковый лазер", "Laser de Rajada"),
        ("pulselaser", "Pulse Laser", "Láser de pulso", "Laser à impulsions", "Impulslaser", "Импульсный лазер", "Laser de Pulso"),
        ("beamlaser", "Beam Laser", "Láser de rayo", "Laser à rayon", "Strahlenlaser", "Лучевой лазер", "Laser de Feixe"),
        ("atmulticannon", "AX Multi-Cannon", "Multicañón AX", "Canon multiple AX", "AX-Mehrfachgeschütz", "АК-многоствольное орудие", "Multicanhão AX"),
        ("multicannon", "Multi-Cannon", "Multicañón", "Canon multiple", "Mehrfachgeschütz", "Многоствольное орудие", "Multicanhão"),
        ("cannon", "Cannon", "Cañón", "Canon", "Kanone", "Пушка", "Canhão"),
        ("slugshot", "Fragment Cannon", "Cañón de fragmentación", "Canon à fragmentation", "Splitterkanone", "Залповое орудие", "Canhão de Fragmentação"),
        ("railgun", "Railgun", "Cañón de riel", "Canon électrique", "Railgun", "Рельсотрон", "Canhão Elétrico"),
        ("atdumbfiremissile", "AX Missile Rack", "Lanzamisiles AX", "Lance-missiles AX", "AX-Raketenbatterie", "АК-ракетная установка", "Lançador de Mísseis AX"),
        ("dumbfiremissilerack", "Dumbfire Missile Rack", "Lanzamisiles no guiados", "Lance-missiles non guidés", "Ungelenkte Raketenbatterie", "Неуправляемая ракетная установка", "Lançador de Mísseis Não Guiados"),
        ("basicmissilerack", "Seeker Missile Rack", "Lanzamisiles guiados", "Lance-missiles guidés", "Zielsuchende Raketenbatterie", "Управляемая ракетная установка", "Lançador de Mísseis Guiados"),
        ("advancedtorppylon", "Torpedo Pylon", "Pilón de torpedos", "Pylône à torpilles", "Torpedopy­lon", "Торпедная установка", "Pilone de Torpedos"),
        ("minelauncher", "Mine Launcher", "Lanzaminas", "Lance-mines", "Minenwerfer", "Минная установка", "Lançador de Minas"),
        ("flakmortar", "Remote Release Flak Launcher", "Lanzador antiaéreo remoto", "Lanceur antiaérien à détonation", "Fernzünder-Flakwerfer", "Зенитная установка с дистанционным подрывом", "Lançador Antiaéreo Remoto"),
        ("flechettelauncher", "Remote Release Flechette Launcher", "Lanzador de flechettes remoto", "Lanceur de fléchettes", "Fernzünder-Flechettewerfer", "Дротиковая установка", "Lançador de Flechettes Remoto"),
        ("mining_abrblstr", "Abrasion Blaster", "Bláster de abrasión", "Blaster à abrasion", "Abrasionsblaster", "Абразивный бластер", "Blaster de Abrasão"),
        ("mining_subsurfdispl", "Sub-Surface Displacement Missile", "Misil de desplazamiento subsuperficial", "Missile de déplacement sub-surface", "Untergrund-Verdrängungsrakete", "Подповерхностная ракета", "Míssil de Deslocamento Subsuperficial"),
        ("mining_seismchrgwarhd", "Seismic Charge Launcher", "Lanzador de cargas sísmicas", "Lanceur de charges sismiques", "Seismischer Ladungswerfer", "Сейсмическая установка", "Lançador de Cargas Sísmicas"),
        ("mininglaser", "Mining Laser", "Láser de minería", "Laser minier", "Abbaulaser", "Проходческий лазер", "Laser de Mineração"),
        ("guardian_gausscannon", "Guardian Gauss Cannon", "Cañón Gauss Guardián", "Canon Gauss Guardian", "Guardian-Gaußkanone", "Пушка Гаусса Стражей", "Canhão Gauss Guardião"),
        ("guardian_plasmalauncher", "Guardian Plasma Charger", "Cargador de plasma Guardián", "Chargeur à plasma Guardian", "Guardian-Plasmakanone", "Плазменная пушка Стражей", "Carregador de Plasma Guardião"),
        ("guardian_shardcannon", "Guardian Shard Cannon", "Cañón de esquirlas Guardián", "Canon à éclats Guardian", "Guardian-Splitterkanone", "Осколочное орудие Стражей", "Canhão de Estilhaços Guardião"),
        ("causticsinklauncher", "Caustic Sink Launcher", "Eyector cáustico", "Éjecteur caustique", "Ätzmittel-Kühlkörperwerfer", "Пусковая установка поглотителей разъедания", "Lançador de Dissipador Cáustico"),

        // Utility Mounts
        ("heatsinklauncher", "Heat Sink Launcher", "Eyector térmico", "Dissipateur thermique", "Kühlkörperwerfer", "Теплоотводная катапульта", "Lançador de Dissipador de Calor"),
        ("chafflauncher", "Chaff Launcher", "Lanzador de señuelos", "Lanceur de paillettes", "Düppelwerfer", "Разбрасыватель дипольных отражателей", "Lançador de Contramedidas"),
        ("plasmapointdefence", "Point Defence", "Defensa de punto", "Défense ponctuelle", "Punktverteidigung", "Точечная оборона", "Defesa de Ponto"),
        ("electroniccountermeasure", "Electronic Countermeasure (ECM)", "Contramedida electrónica (ECM)", "Contre-mesure électronique (ECM)", "Elektronische Gegenmaßnahme (ECM)", "РЭБ (ECM)", "Contramedida Eletrônica (ECM)"),
        ("shieldbooster", "Shield Booster", "Potenciador de escudo", "Survolteur de bouclier", "Schild-Booster", "Усилитель щита", "Amplificador de Escudo"),
        ("crimescanner", "Kill Warrant Scanner", "Escáner de recompensas", "Détecteur de primes", "Tötungsbefehl-Scanner", "Сканер преступников", "Scanner de Mandados"),
        ("cargoscanner", "Manifest Scanner", "Escáner de manifiesto", "Détecteur de manifeste", "Manifestscanner", "Сканер груза", "Scanner de Manifesto"),
        ("cloudscanner", "Frame Shift Wake Scanner", "Escáner de estela de MDD", "Détecteur de sillage FSD", "Frameshift-Sogwolkenscanner", "Сканер следа РСД", "Scanner de Rastro de Dobra"),
        ("xenoscanner", "Xeno Scanner", "Escáner xeno", "Détecteur xéno", "Xeno-Scanner", "Ксеносканер", "Scanner Xeno"),
        ("shutdownfieldneutraliser", "Shutdown Field Neutraliser", "Neutralizador de campo de apagado", "Neutraliseur de champ d'arrêt", "Abschaltfeld-Neutralisator", "Нейтрализатор поля отключения", "Neutralizador de Campo de Desligamento"),
        ("mrascanner", "Pulse Wave Analyser", "Analizador de onda de pulso", "Analyseur d'onde à impulsions", "Pulswellen-Analysator", "Анализатор импульсных волн", "Analisador de Onda de Pulso")
    ];

    /// <summary>
    /// Formats a raw internal Frontier module name into a clean localized in-game display label.
    /// </summary>
    public static string Format(string? rawItem, string language = "en-GB")
    {
        if (string.IsNullOrWhiteSpace(rawItem))
            return "";

        string lang = NormalizeLang(language);
        string s = rawItem.Trim()
            .Replace("$", "", StringComparison.Ordinal)
            .Replace("_name;", "", StringComparison.OrdinalIgnoreCase);

        string lower = s.ToLowerInvariant();

        // Ship bulkheads / armour (e.g. anaconda_armour_grade3)
        var armourMatch = ArmourRegex.Match(lower);
        if (armourMatch.Success)
        {
            string grade = LocalizeArmourGrade(armourMatch.Groups[2].Value, lang);
            string ship = FormatFallbackWords(armourMatch.Groups[1].Value);
            return $"{grade} ({ship})";
        }

        // Extract size & class rating (e.g. _size5_class5 -> "5A")
        string sizeCode = "";
        var sizeMatch = SizeRegex.Match(lower);
        if (sizeMatch.Success)
            sizeCode = sizeMatch.Groups[1].Value;

        string rating = "";
        var classMatch = ClassRegex.Match(lower);
        if (classMatch.Success && int.TryParse(classMatch.Groups[1].Value, out int cls))
        {
            rating = cls switch
            {
                1 => "E",
                2 => "D",
                3 => "C",
                4 => "B",
                5 => "A",
                _ => ""
            };
        }

        // Hardpoint sizes & mounts
        string hpSize = lower.Contains("_huge", StringComparison.Ordinal) ? "4"
            : lower.Contains("_large", StringComparison.Ordinal) ? "3"
            : lower.Contains("_medium", StringComparison.Ordinal) ? "2"
            : lower.Contains("_small", StringComparison.Ordinal) ? "1"
            : lower.Contains("_tiny", StringComparison.Ordinal) ? "0"
            : "";

        string mount = lower.Contains("_fixed", StringComparison.Ordinal) ? LocalizeMount("fixed", lang)
            : lower.Contains("_gimbal", StringComparison.Ordinal) ? LocalizeMount("gimbal", lang)
            : lower.Contains("_turret", StringComparison.Ordinal) ? LocalizeMount("turret", lang)
            : "";

        string classWord = lang switch
        {
            "es" => "Clase",
            "fr" => "Classe",
            "de" => "Klasse",
            "ru" => "Класс",
            "pt" => "Classe",
            _ => "Class"
        };

        foreach (var entry in Catalog)
        {
            if (lower.Contains(entry.Key, StringComparison.Ordinal))
            {
                string friendly = lang switch
                {
                    "es" => entry.Es,
                    "fr" => entry.Fr,
                    "de" => entry.De,
                    "ru" => entry.Ru,
                    "pt" => entry.Pt,
                    _ => entry.En
                };
                string prefix = !string.IsNullOrEmpty(sizeCode)
                    ? $"{sizeCode}{rating} "
                    : !string.IsNullOrEmpty(hpSize)
                        ? $"{classWord} {hpSize}{rating} "
                        : "";
                string suffix = !string.IsNullOrEmpty(mount) ? $" ({mount})" : "";
                return $"{prefix}{friendly}{suffix}".Trim();
            }
        }

        // Clean fallback for any unrecognized module ID
        string fallback = lower
            .Replace("int_", "", StringComparison.Ordinal)
            .Replace("hpt_", "", StringComparison.Ordinal);
        fallback = SizeRegex.Replace(fallback, "");
        fallback = ClassRegex.Replace(fallback, "");
        string cleanWords = FormatFallbackWords(fallback);
        string fbPrefix = !string.IsNullOrEmpty(sizeCode) ? $"{sizeCode}{rating} " : "";
        return $"{fbPrefix}{cleanWords}".Trim();
    }

    private static string NormalizeLang(string? language)
    {
        string l = (language ?? "en-GB").Trim().ToLowerInvariant();
        if (l.StartsWith("es", StringComparison.Ordinal)) return "es";
        if (l.StartsWith("fr", StringComparison.Ordinal)) return "fr";
        if (l.StartsWith("de", StringComparison.Ordinal)) return "de";
        if (l.StartsWith("ru", StringComparison.Ordinal)) return "ru";
        if (l.StartsWith("pt", StringComparison.Ordinal)) return "pt";
        return "en";
    }

    private static string LocalizeMount(string mount, string lang) => (mount, lang) switch
    {
        ("fixed", "es") => "Fijo",
        ("fixed", "fr") => "Fixe",
        ("fixed", "de") => "Fest",
        ("fixed", "ru") => "Фикс.",
        ("fixed", "pt") => "Fixo",
        ("fixed", _) => "Fixed",

        ("gimbal", "es") => "Cardán",
        ("gimbal", "fr") => "Sur pivot",
        ("gimbal", "de") => "Kardanisch",
        ("gimbal", "ru") => "Кардан.",
        ("gimbal", "pt") => "Articulado",
        ("gimbal", _) => "Gimballed",

        ("turret", "es") => "Torreta",
        ("turret", "fr") => "Tourelle",
        ("turret", "de") => "Geschützturm",
        ("turret", "ru") => "Турель",
        ("turret", "pt") => "Torre",
        _ => "Turreted"
    };

    private static string LocalizeArmourGrade(string grade, string lang) => (grade, lang) switch
    {
        ("grade1", "es") => "Aleación ligera",
        ("grade1", "fr") => "Alliage léger",
        ("grade1", "de") => "Leichtbau-Legierung",
        ("grade1", "ru") => "Лёгкие сплавы",
        ("grade1", "pt") => "Liga Leve",
        ("grade1", _) => "Lightweight Alloy",

        ("grade2", "es") => "Aleación reforzada",
        ("grade2", "fr") => "Alliage renforcé",
        ("grade2", "de") => "Verstärkte Legierung",
        ("grade2", "ru") => "Усиленные сплавы",
        ("grade2", "pt") => "Liga Reforçada",
        ("grade2", _) => "Reinforced Alloy",

        ("grade3", "es") => "Compuesto de grado militar",
        ("grade3", "fr") => "Composite de qualité militaire",
        ("grade3", "de") => "Militärkomposit",
        ("grade3", "ru") => "Военный композит",
        ("grade3", "pt") => "Compósito Militar",
        ("grade3", _) => "Military Grade Composite",

        ("mirrored", "es") => "Compuesto de superficie espejada",
        ("mirrored", "fr") => "Composite à surface réfléchissante",
        ("mirrored", "de") => "Gespiegelter Oberflächenkomposit",
        ("mirrored", "ru") => "Зеркальный композит",
        ("mirrored", "pt") => "Compósito Espelhado",
        ("mirrored", _) => "Mirrored Surface Composite",

        ("reactive", "es") => "Compuesto de superficie reactiva",
        ("reactive", "fr") => "Composite à surface réactive",
        ("reactive", "de") => "Reaktiver Oberflächenkomposit",
        ("reactive", "ru") => "Реактивный композит",
        ("reactive", "pt") => "Compósito Reativo",
        _ => "Reactive Surface Composite"
    };

    private static string FormatFallbackWords(string raw)
    {
        var parts = raw.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string w = parts[i];
            parts[i] = w.Length > 1 ? char.ToUpperInvariant(w[0]) + w[1..] : w.ToUpperInvariant();
        }
        return string.Join(' ', parts);
    }
}
