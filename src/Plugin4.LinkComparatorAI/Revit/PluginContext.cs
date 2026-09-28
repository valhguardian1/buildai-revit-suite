using System;
using Autodesk.Revit.UI;
using BuildAI.Core.Issues;
using BuildAI.Core.Security;
using Plugin4.LinkComparatorAI.AI;
using Plugin4.LinkComparatorAI.Comparison;
using Plugin4.LinkComparatorAI.Issues;
using Plugin4.LinkComparatorAI.Models;

namespace Plugin4.LinkComparatorAI.Revit
{
    internal static class PluginContext
    {
        private static readonly object InitializationGate = new object();

        public static UIApplication UiApplication { get; set; }
        public static ComparatorSettings Settings { get; set; }
        public static ComparatorEngine Engine { get; set; }
        public static AiConnector Ai { get; set; }
        public static ComparisonReport LastReport { get; set; }
        public static ComparatorActionHandler ActionHandler { get; set; }
        public static ICredentialStore CredentialStore { get; set; }
        public static ComparatorIssueWorkflow Issues { get; set; }
        public static ExternalEvent ActionEvent { get; set; }
        public static Exception InitializationError { get; set; }

        public static void EnsureInitialized()
        {
            lock (InitializationGate)
            {
                if (Settings == null) Settings = ComparatorSettings.Load();
                if (Engine == null) Engine = new ComparatorEngine();
                if (Ai == null) Ai = new AiConnector();
                if (CredentialStore == null) CredentialStore = new WindowsCredentialStore();
                if (Issues == null)
                    Issues = new ComparatorIssueWorkflow(
                        new IssueIntegrationClient("https://app.buildai.me", CredentialStore));
                if (ActionHandler == null) ActionHandler = new ComparatorActionHandler();
                if (ActionEvent == null) ActionEvent = ExternalEvent.Create(ActionHandler);
                InitializationError = null;
            }
        }
    }
}
