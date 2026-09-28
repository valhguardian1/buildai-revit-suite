using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BuildAI.Core.Logging;

namespace Plugin5.ClashFormaIntegration.Models
{
    /// <summary>
    /// How a category participates in clash detection by default.
    /// </summary>
    public enum ClashCategoryMode
    {
        /// <summary>Enabled in every default group. Carries real MEP geometry.</summary>
        Default = 0,

        /// <summary>Available but off by default - high noise, enable deliberately.</summary>
        Optional = 1,

        /// <summary>Only meaningful together with a family-name filter.</summary>
        Conditional = 2,

        /// <summary>Never participates, even if selected.</summary>
        Never = 3
    }

    public sealed class ClashCategoryDefinition
    {
        public string BuiltInCategoryName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Discipline { get; set; } = "";
        public ClashCategoryMode Mode { get; set; }

        /// <summary>
        /// Flexibility rank 1-8 from the LOD 350 specification. 1 is the least
        /// flexible system (gravity drainage, mains) and moves last; 8 is the most
        /// flexible (conduit, small diameters) and moves first. The element with
        /// the HIGHER rank becomes the resolution owner.
        /// </summary>
        public int FlexibilityRank { get; set; }

        public string Note { get; set; } = "";
    }

    /// <summary>
    /// MEP clash category catalogue, transcribed from MEP_Clash_Elements_LOD350.xlsx.
    /// <para>
    /// Before this catalogue existed, the category filter lists defaulted to empty.
    /// ClashEngine.Collect treats an empty list as "no filter", so every model
    /// category in the document entered clash detection - architectural, structural,
    /// annotation-adjacent, analytical volumes and all. That is the root of the
    /// unusable result counts seen in production runs.
    /// </para>
    /// <para>
    /// Categories are resolved by NAME through Enum.TryParse rather than referenced
    /// as compile-time enum members. BuiltInCategory membership differs across the
    /// Revit 2023-2026 SDKs this suite targets - OST_PlumbingEquipment, for one,
    /// only appears from 2023 - and a direct reference would break the build for
    /// every version that lacks it. Unknown names are skipped and logged instead.
    /// </para>
    /// </summary>
    public static class MepClashCategories
    {
        /// <summary>
        /// Bumped whenever the catalogue changes. Settings saved with an older
        /// value have their category lists re-seeded, so an existing installation
        /// picks up the defaults instead of silently keeping an empty (= unfiltered)
        /// selection.
        /// </summary>
        public const int CatalogueVersion = 2;

        private static readonly List<ClashCategoryDefinition> _all = new List<ClashCategoryDefinition>();
        private static readonly List<ClashCategoryDefinition> _excluded = new List<ClashCategoryDefinition>();
        private static readonly List<ClashCategoryDefinition> _hardExcludedOnly = new List<ClashCategoryDefinition>();
        private static readonly Dictionary<string, string> _exclusionReasons =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static MepClashCategories()
        {
            Add("OST_DuctCurves", "Ducts", "HVAC", ClashCategoryMode.Default, 2, "Rank 2 when the size exceeds 400 mm; otherwise rank 6");
            Add("OST_FlexDuctCurves", "Flex Ducts", "HVAC", ClashCategoryMode.Default, 6, "Spline centerline geometry; add 10 mm tolerance");
            Add("OST_DuctFitting", "Duct Fittings", "HVAC", ClashCategoryMode.Default, 2, "Exclude pairs with the host segment through ConnectorManager");
            Add("OST_DuctAccessory", "Duct Accessories", "HVAC", ClashCategoryMode.Default, 2, "Requires an ACCESS service-zone test");
            Add("OST_DuctTerminal", "Air Terminals", "HVAC", ClashCategoryMode.Default, 4, "Ceiling-hosted; check conflicts with sprinklers and lighting");
            Add("OST_DuctInsulations", "Duct Insulations", "HVAC", ClashCategoryMode.Optional, 2, "Separate solid. Disable for HARD; enable for CLEARANCE");
            Add("OST_DuctLinings", "Duct Linings", "HVAC", ClashCategoryMode.Never, 2, "Inside the duct; does not affect external clashes");
            Add("OST_MechanicalEquipment", "Mechanical Equipment", "HVAC", ClashCategoryMode.Default, 4, "ACCESS is mandatory: 600-900 mm front zone for filter and pipe removal");
            Add("OST_MechanicalControlDevices", "Mechanical Control Devices", "HVAC", ClashCategoryMode.Optional, 8, "Small components with high noise; enable selectively");
            Add("OST_PipeCurves", "Pipes", "PLUMBING", ClashCategoryMode.Default, 1, "Rank 1 for gravity pipes (slope); rank 5 for pressurized pipes");
            Add("OST_FlexPipeCurves", "Flex Pipes", "PLUMBING", ClashCategoryMode.Default, 6, "");
            Add("OST_PipeFitting", "Pipe Fittings", "PLUMBING", ClashCategoryMode.Default, 1, "Exclude pairs with the host segment through ConnectorManager");
            Add("OST_PipeAccessory", "Pipe Accessories", "PLUMBING", ClashCategoryMode.Default, 1, "Requires ACCESS for handle, spindle, and insert removal");
            Add("OST_PlumbingFixtures", "Plumbing Fixtures", "PLUMBING", ClashCategoryMode.Default, 4, "");
            Add("OST_PlumbingEquipment", "Plumbing Equipment", "PLUMBING", ClashCategoryMode.Default, 4, "Introduced in Revit 2023; verify API availability");
            Add("OST_PipeInsulations", "Pipe Insulations", "PLUMBING", ClashCategoryMode.Optional, 1, "Measure clearances from insulation, not the centerline");
            Add("OST_Sprinklers", "Sprinklers", "FIRE", ClashCategoryMode.Default, 3, "450 mm CLEARANCE below for the spray zone");
            Add("OST_ElectricalEquipment", "Electrical Equipment", "ELECTRICAL", ClashCategoryMode.Default, 4, "ACCESS is mandatory: 900 mm front working zone");
            Add("OST_ElectricalFixtures", "Electrical Fixtures", "ELECTRICAL", ClashCategoryMode.Optional, 8, "Small components; enable only for wall clashes");
            Add("OST_LightingFixtures", "Lighting Fixtures", "ELECTRICAL", ClashCategoryMode.Default, 4, "Recessed fixtures can conflict with ducts above the ceiling");
            Add("OST_LightingDevices", "Lighting Devices", "ELECTRICAL", ClashCategoryMode.Optional, 8, "");
            Add("OST_Conduit", "Conduits", "ELECTRICAL", ClashCategoryMode.Default, 8, "Most flexible system; almost always the resolution owner");
            Add("OST_ConduitFitting", "Conduit Fittings", "ELECTRICAL", ClashCategoryMode.Default, 8, "");
            Add("OST_CableTray", "Cable Trays", "ELECTRICAL", ClashCategoryMode.Default, 7, "Asymmetric CLEARANCE: 300 mm above for cable pulling and 50 mm below");
            Add("OST_CableTrayFitting", "Cable Tray Fittings", "ELECTRICAL", ClashCategoryMode.Default, 7, "");
            Add("OST_DataDevices", "Data Devices", "ELECTRICAL", ClashCategoryMode.Optional, 8, "");
            Add("OST_FireAlarmDevices", "Fire Alarm Devices", "ELECTRICAL", ClashCategoryMode.Optional, 8, "Enable when checking the ceiling plane");
            Add("OST_CommunicationDevices", "Communication Devices", "ELECTRICAL", ClashCategoryMode.Optional, 8, "");
            Add("OST_SecurityDevices", "Security Devices", "ELECTRICAL", ClashCategoryMode.Optional, 8, "");
            Add("OST_TelephoneDevices", "Telephone Devices", "ELECTRICAL", ClashCategoryMode.Optional, 8, "");
            Add("OST_NurseCallDevices", "Nurse Call Devices", "ELECTRICAL", ClashCategoryMode.Optional, 8, "Medical facilities only");
            Add("OST_FabricationDuctwork", "MEP Fabrication Ductwork", "FABRICATION", ClashCategoryMode.Optional, 2, "LOD 350+. Not a FamilyInstance; filter as FabricationPart");
            Add("OST_FabricationPipework", "MEP Fabrication Pipework", "FABRICATION", ClashCategoryMode.Optional, 1, "LOD 350+");
            Add("OST_FabricationHangers", "MEP Fabrication Hangers", "FABRICATION", ClashCategoryMode.Default, 1, "Key LOD 350 category; apply the HANGER test");
            Add("OST_FabricationContainment", "MEP Fabrication Containment", "FABRICATION", ClashCategoryMode.Optional, 7, "Revit 2019+");
            Add("OST_GenericModel", "Generic Models", "GENERIC", ClashCategoryMode.Conditional, 1, "ENABLE ONLY with a family-name filter (Hanger*, Support*, Frame*)");

            // Structural framing is not an MEP system and must never leak in through stale settings or fallback collection.
            AddHardExcluded("OST_StructuralFraming", "Structural category; excluded from the MEP Clash catalogue and publication view");

            // --- Never eligible: no own solid geometry ---
            AddExcluded("OST_DuctSystem", "Duct Systems", "Logical system with no geometry of its own");
            AddExcluded("OST_PipingSystem", "Piping Systems", "Logical system with no geometry of its own");
            AddExcluded("OST_ElectricalCircuit", "Electrical Circuits", "Logical circuit with no geometry");
            AddExcluded("OST_SwitchSystem", "Switch System", "Logical relationship with no geometry");
            AddExcluded("OST_Wire", "Wires", "2D view annotation that does not exist in 3D");
            AddExcluded("OST_MEPSpaces", "Spaces", "Analytical volume that produces false intersections");
            AddExcluded("OST_HVAC_Zones", "HVAC Zones", "Analytical volume");
            AddExcluded("OST_PlaceHolderDucts", "Duct Placeholders", "Bodyless line used at LOD 100-200");
            AddExcluded("OST_PlaceHolderPipes", "Pipe Placeholders", "Bodyless line used at LOD 100-200");
            AddExcluded("OST_DuctCurvesCenterLine", "Duct Curves Center Line", "View graphics, not an element");
            AddExcluded("OST_PipeCurvesCenterLine", "Pipe Curves Center Line", "View graphics, not an element");
            AddExcluded("OST_DuctCurvesInsulation", "Duct Curves Insulation", "Display subcategory");
            AddExcluded("OST_AnalyticalPipeConnections", "Analytical Pipe Connections", "Analytical systems model");
            AddExcluded("OST_PipeSegments", "Pipe Segments", "Type rather than a placed element");
            AddExcluded("OST_CableTrayRun", "Cable Tray Runs", "Run container; geometry belongs to the segments");
            AddExcluded("OST_ConduitRun", "Conduit Runs", "Run container; geometry belongs to the segments");
        }

        private static void Add(string bic, string display, string discipline, ClashCategoryMode mode, int rank, string note)
        {
            _all.Add(new ClashCategoryDefinition
            {
                BuiltInCategoryName = bic,
                DisplayName = display,
                Discipline = discipline,
                Mode = mode,
                FlexibilityRank = rank,
                Note = note ?? ""
            });
        }

        private static void AddHardExcluded(string bic, string reason)
        {
            _hardExcludedOnly.Add(new ClashCategoryDefinition
            {
                BuiltInCategoryName = bic,
                DisplayName = "",
                Mode = ClashCategoryMode.Never,
                Note = reason ?? ""
            });
            _exclusionReasons[bic] = reason ?? "";
        }

        private static void AddExcluded(string bic, string display, string reason)
        {
            _excluded.Add(new ClashCategoryDefinition
            {
                BuiltInCategoryName = bic,
                DisplayName = display,
                Mode = ClashCategoryMode.Never,
                Note = reason ?? ""
            });
            _exclusionReasons[bic] = reason ?? "";
        }

        public static IReadOnlyList<ClashCategoryDefinition> All => _all;
        public static IReadOnlyList<ClashCategoryDefinition> Excluded => _excluded;

        /// <summary>
        /// Categories enabled out of the box: everything the specification marks as
        /// carrying real, clash-relevant geometry.
        /// </summary>
        public static IEnumerable<ClashCategoryDefinition> DefaultDefinitions =>
            _all.Where(x => x.Mode == ClashCategoryMode.Default);

        /// <summary>
        /// Available in the picker but off by default - small devices and insulation,
        /// which generate a high share of low-value hits.
        /// </summary>
        public static IEnumerable<ClashCategoryDefinition> OptionalDefinitions =>
            _all.Where(x => x.Mode == ClashCategoryMode.Optional);

        /// <summary>
        /// Resolves a BuiltInCategory name to its integer id on the running Revit
        /// version, returning false when this version does not define it.
        /// </summary>
        public static bool TryResolve(string builtInCategoryName, out int categoryId)
        {
            categoryId = 0;
            BuiltInCategory parsed;
            if (!Enum.TryParse(builtInCategoryName, false, out parsed)) return false;
            if (!Enum.IsDefined(typeof(BuiltInCategory), parsed)) return false;
            categoryId = (int)parsed;
            return true;
        }

        /// <summary>
        /// The default category id list for a clash group on the running Revit version.
        /// Names this version does not know are skipped, so the same catalogue is safe
        /// across Revit 2023-2026.
        /// </summary>
        public static List<int> DefaultCategoryIds()
        {
            var ids = new List<int>();
            var skipped = new List<string>();
            foreach (var definition in DefaultDefinitions)
            {
                int id;
                if (TryResolve(definition.BuiltInCategoryName, out id)) ids.Add(id);
                else skipped.Add(definition.BuiltInCategoryName);
            }
            if (skipped.Count > 0)
            {
                PluginLog.Info("Clash category defaults: names not available in this Revit version were skipped",
                    new { skipped = string.Join(", ", skipped) });
            }
            return ids;
        }

        /// <summary>
        /// Category ids that must never reach clash detection regardless of user
        /// selection: logical systems, analytical volumes, view graphics, placeholders
        /// and run containers, none of which own real solid geometry. Also covers
        /// entries the catalogue marks Never, such as duct linings, whose geometry sits
        /// inside its host duct and cannot clash with anything external.
        /// </summary>
        public static HashSet<int> HardExcludedCategoryIds()
        {
            var ids = new HashSet<int>();
            foreach (var definition in _hardExcludedOnly.Concat(_excluded).Concat(_all.Where(x => x.Mode == ClashCategoryMode.Never)))
            {
                int id;
                if (TryResolve(definition.BuiltInCategoryName, out id)) ids.Add(id);
            }
            return ids;
        }

        public static string ExclusionReason(string builtInCategoryName)
        {
            string reason;
            return _exclusionReasons.TryGetValue(builtInCategoryName ?? "", out reason) ? reason : "";
        }

        /// <summary>
        /// Flexibility rank for a resolved category id, or 0 when unknown.
        /// </summary>
        public static int FlexibilityRankOf(int categoryId)
        {
            foreach (var definition in _all)
            {
                int id;
                if (TryResolve(definition.BuiltInCategoryName, out id) && id == categoryId)
                    return definition.FlexibilityRank;
            }
            return 0;
        }
    }
}
