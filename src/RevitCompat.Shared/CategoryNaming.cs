using System;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using BuildAI.Core.Localization;

namespace BuildAI.RevitCompatibility
{
    /// <summary>
    /// Produces a category name in the language the user configured, never in the
    /// language Revit happens to be running in.
    ///
    /// Element.Category.Name returns the name localised to the Revit UI. On a
    /// Russian-language Revit it is Russian, and no application setting overrides
    /// that. Since it was written straight into Issue titles and into the payload
    /// sent to BuildAI, it was the one place Russian could enter a product that must
    /// never contain any. The fix is to stop asking Revit for the text at all: the
    /// BuiltInCategory enum value is a stable identifier, and the display name comes
    /// from the plugin's own resources.
    ///
    /// The enum member is resolved by name rather than referenced directly, for the
    /// same reason MepClashCategories does: BuiltInCategory membership differs across
    /// the 2023-2026 SDKs, and a direct reference to a member missing from one of
    /// them breaks that build.
    /// </summary>
    internal static class CategoryNaming
    {
        private static readonly Regex Cyrillic = new Regex("[\u0400-\u04FF]", RegexOptions.Compiled);

        /// <summary>Stable key such as "OST_Walls"; empty when the element has no category.</summary>
        public static string Key(Element element)
        {
            if (element?.Category == null) return "";
            try
            {
#if REVIT2026_OR_GREATER
                var value = (BuiltInCategory)element.Category.Id.Value;
#else
                var value = (BuiltInCategory)element.Category.Id.IntegerValue;
#endif
                return Enum.GetName(typeof(BuiltInCategory), value) ?? "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// Display name for the current UI language.
        ///
        /// Resource first, then the enum name with its OST_ prefix stripped. That
        /// fallback is deliberately English rather than Category.Name: a category
        /// missing from the resource file should read "StructuralFraming", which is
        /// merely unpolished, not a Russian string in an Israeli client's Issue.
        /// </summary>
        public static string Display(Element element)
        {
            var key = Key(element);
            if (string.IsNullOrEmpty(key)) return "";
            var resourceKey = "Cat_" + key;
            var localized = Loc.T(resourceKey);
            if (!string.Equals(localized, resourceKey, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(localized))
                return localized;
            return Humanize(key);
        }

        /// <summary>
        /// Last line of defence for values that reach an Issue from somewhere this
        /// class does not control - a type name, a level name, a link title. Those are
        /// authored by the user and can legitimately be in any language, but the
        /// product forbids Russian anywhere, so a value containing Cyrillic is
        /// replaced by the caller's neutral substitute rather than passed through.
        /// </summary>
        public static string OrFallback(string value, string fallback)
            => string.IsNullOrWhiteSpace(value) || Cyrillic.IsMatch(value) ? (fallback ?? "") : value;

        public static bool ContainsCyrillic(string value)
            => !string.IsNullOrEmpty(value) && Cyrillic.IsMatch(value);

        private static string Humanize(string key)
        {
            var name = key.StartsWith("OST_", StringComparison.Ordinal) ? key.Substring(4) : key;
            return Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");
        }
    }
}
