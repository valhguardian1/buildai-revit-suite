using Autodesk.Revit.UI;
using BuildAI.Core.Security;
using Plugin5.ClashFormaIntegration.AI;
using Plugin5.ClashFormaIntegration.Clash;
using Plugin5.ClashFormaIntegration.Models;
using Plugin5.ClashFormaIntegration.UI;
using System;
using System.IO;
using Newtonsoft.Json;
using Plugin5.ClashFormaIntegration.Issues;

namespace Plugin5.ClashFormaIntegration.Revit
{
    internal static class PluginContext
    {
        public static Plugin5Settings Settings;
        public static UIApplication UiApplication;
        public static ICredentialStore CredentialStore;
        public static ClashEngine Engine;
        public static ClashAiConnector Ai;
        public static ClashReport Report;

        /// <summary>
        /// The open chunked run, kept alive between pages so "Load more" can resume
        /// at the cursor instead of recollecting and re-sweeping. Null when the
        /// report was loaded from disk rather than computed in this session, in
        /// which case paging is unavailable and the UI says so.
        /// </summary>
        public static ClashSession Session;
        public static ResultsWindow Window;
        public static ModelActionHandler Handler;
        public static ExternalEvent Event;
        public static ClashIssueWorkflow Issues;

        private static string ReportPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BuildAI", "plugin5-last-clash-report.json");
        public static void SaveReport()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath, JsonConvert.SerializeObject(Report, Formatting.Indented)); } catch { }
        }
        public static ClashReport LoadReport()
        {
            try { return File.Exists(ReportPath) ? JsonConvert.DeserializeObject<ClashReport>(File.ReadAllText(ReportPath)) : null; } catch { return null; }
        }
    }
}
