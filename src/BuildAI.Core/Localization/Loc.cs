using System.Globalization;
using System.Resources;
using System.Threading;

namespace BuildAI.Core.Localization
{
    /// <summary>
    /// Thin localization facade over a satellite-resource ResourceManager.
    /// Base languages: English (neutral) + Hebrew (he, RTL). Adding a language
    /// = dropping in Strings.&lt;culture&gt;.resx — no code change (specification §2.3).
    /// Language can switch at runtime. Kept WPF-free so Core stays netstandard;
    /// the WPF layer maps <see cref="IsRightToLeft"/> to FlowDirection.
    /// </summary>
    public static class Loc
    {
        private static readonly ResourceManager Rm =
            new ResourceManager("BuildAI.Core.Localization.Strings", typeof(Loc).Assembly);

        /// <summary>Set the active UI culture ("en", "he", ...). Empty = OS default.</summary>
        public static void SetLanguage(string culture)
        {
            if (string.IsNullOrWhiteSpace(culture)) return;
            var ci = CultureInfo.GetCultureInfo(culture);
            Thread.CurrentThread.CurrentUICulture = ci;
            CultureInfo.DefaultThreadCurrentUICulture = ci;
        }

        public static string T(string key)
            => Rm.GetString(key, Thread.CurrentThread.CurrentUICulture) ?? key;

        /// <summary>True for Hebrew/Arabic; the WPF layer sets FlowDirection from this.</summary>
        public static bool IsRightToLeft
            => Thread.CurrentThread.CurrentUICulture.TextInfo.IsRightToLeft;
    }
}
