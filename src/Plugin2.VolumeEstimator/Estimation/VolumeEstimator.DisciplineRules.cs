using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using BuildAI.Core.Configuration;

namespace Plugin2.VolumeEstimator.Estimation
{
    /// <summary>
    /// The ownership rule for de-duplicating a federated model (scenario A).
    ///
    /// Each material is counted once, from the discipline that "owns" it:
    ///   ST  — concrete/rebar/steel, structural foundations/columns/framing,
    ///         and structural walls/floors;
    ///   AR  — partitions & non-structural walls/floors, finishes, doors,
    ///         windows, ceilings, roofs, stairs, railings, casework;
    ///   MEP — pipes, ducts, trays, conduits, MEP equipment and fixtures.
    ///
    /// Matching is by <see cref="BuiltInCategory"/> (language-independent — the
    /// models here are Hebrew), and the structural checkbox disambiguates the
    /// two categories that legitimately appear in both AR and ST (walls, floors).
    /// Categories with no rule return null → "belongs to whatever model it is
    /// in" (counted once, since that element is not the cross-discipline copy).
    /// </summary>
    public static class DisciplineRules
    {
        public const string AR  = "AR";
        public const string ST  = "ST";
        public const string IOS = "MEP";

        /// <summary>Owning discipline for an element, or null if no rule applies.</summary>
        public static string OwnerByCategory(Element el)
        {
            var cat = el?.Category;
            if (cat == null) return null;

            BuiltInCategory bic;
            try { bic = cat.BuiltInCategory; } catch { return null; } // Revit 2023+

            switch (bic)
            {
                // Ambiguous: present in both AR and ST → structural flag decides.
                case BuiltInCategory.OST_Walls:
                case BuiltInCategory.OST_Floors:
                    return IsStructural(el) ? ST : AR;

                // ST — structure
                case BuiltInCategory.OST_StructuralFoundation:
                case BuiltInCategory.OST_StructuralColumns:
                case BuiltInCategory.OST_StructuralFraming:
                case BuiltInCategory.OST_StructuralStiffener:
                case BuiltInCategory.OST_StructConnections:
                case BuiltInCategory.OST_Rebar:
                case BuiltInCategory.OST_AreaRein:
                case BuiltInCategory.OST_PathRein:
                case BuiltInCategory.OST_FabricAreas:
                case BuiltInCategory.OST_FabricReinforcement:
                    return ST;

                // AR — architecture
                case BuiltInCategory.OST_Doors:
                case BuiltInCategory.OST_Windows:
                case BuiltInCategory.OST_Ceilings:
                case BuiltInCategory.OST_Roofs:
                case BuiltInCategory.OST_Stairs:
                case BuiltInCategory.OST_StairsRailing:
                case BuiltInCategory.OST_Railings:
                case BuiltInCategory.OST_Ramps:
                case BuiltInCategory.OST_CurtainWallPanels:
                case BuiltInCategory.OST_CurtainWallMullions:
                case BuiltInCategory.OST_Furniture:
                case BuiltInCategory.OST_FurnitureSystems:
                case BuiltInCategory.OST_Casework:
                    return AR;

                // MEP — MEP
                case BuiltInCategory.OST_PipeCurves:
                case BuiltInCategory.OST_PipeFitting:
                case BuiltInCategory.OST_PipeAccessory:
                case BuiltInCategory.OST_FlexPipeCurves:
                case BuiltInCategory.OST_DuctCurves:
                case BuiltInCategory.OST_DuctFitting:
                case BuiltInCategory.OST_DuctAccessory:
                case BuiltInCategory.OST_FlexDuctCurves:
                case BuiltInCategory.OST_DuctTerminal:
                case BuiltInCategory.OST_CableTray:
                case BuiltInCategory.OST_CableTrayFitting:
                case BuiltInCategory.OST_Conduit:
                case BuiltInCategory.OST_ConduitFitting:
                case BuiltInCategory.OST_Wire:
                case BuiltInCategory.OST_MechanicalEquipment:
                case BuiltInCategory.OST_ElectricalEquipment:
                case BuiltInCategory.OST_ElectricalFixtures:
                case BuiltInCategory.OST_LightingFixtures:
                case BuiltInCategory.OST_LightingDevices:
                case BuiltInCategory.OST_PlumbingFixtures:
                case BuiltInCategory.OST_Sprinklers:
                    return IOS;

                default:
                    return null;
            }
        }

        /// <summary>True if a wall/floor has its "Structural" checkbox set.</summary>
        private static bool IsStructural(Element el)
        {
            try
            {
                var p = el.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)
                        ?? el.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
                if (p != null && p.StorageType == StorageType.Integer)
                    return p.AsInteger() == 1;
            }
            catch { }
            return false;
        }

        // ---- source (model) discipline ---------------------------------------
        private static readonly Dictionary<string, string[]> Keywords = new Dictionary<string, string[]>
        {
            // Order of evaluation is fixed in ResolveSource (ST, MEP, then AR).
            { ST,  new[] { "kr", "kzh", "kr", "kj", "str", "struct", "konstr", "str" } },
            { IOS, new[] { "ios", "ov", "vk", "eom", "es", "ss", "ak", "mep", "hvac", "ios", "pozh" } },
            { AR,  new[] { "ar", "arkh", "ar", "arch", "arc" } },
        };

        /// <summary>
        /// Resolve a model's discipline from explicit overrides first, then from
        /// tokens in its title. Returns "" when undetermined (caller counts the
        /// source as-is and flags it, since dedup can't be guaranteed for it).
        /// </summary>
        public static string ResolveSource(string modelTitle, BuildAiOptions options)
        {
            if (string.IsNullOrWhiteSpace(modelTitle)) return "";

            if (options?.DisciplineOverrides != null)
                foreach (var kv in options.DisciplineOverrides)
                    if (!string.IsNullOrEmpty(kv.Key) &&
                        modelTitle.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                        return kv.Value;

            var tokens = Regex.Split(modelTitle.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
                              .Where(t => t.Length > 0).ToArray();

            foreach (var disc in new[] { ST, IOS, AR }) // ST/MEP before AR (short "ar" is greedy)
                foreach (var kw in Keywords[disc])
                    if (tokens.Any(t => t == kw || t.StartsWith(kw)))
                        return disc;

            return "";
        }
    }
}
