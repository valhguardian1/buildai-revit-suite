using System;
using System.IO;

namespace BuildAI.Core.Localization
{
    public static class LanguageSettings
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BuildAI", "language.txt");

        public static string CurrentLanguage { get; private set; } = "en";

        public static void LoadAndApply()
        {
            var language = "en";
            try
            {
                if (File.Exists(FilePath)) language = File.ReadAllText(FilePath).Trim();
            }
            catch { }
            Apply(language);
        }

        public static void SaveAndApply(string language)
        {
            Apply(language);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, CurrentLanguage);
        }

        public static void Apply(string language)
        {
            CurrentLanguage = string.Equals(language, "he", StringComparison.OrdinalIgnoreCase) ? "he" : "en";
            Loc.SetLanguage(CurrentLanguage);
        }
    }
}
