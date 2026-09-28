using System;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BuildAI.Core.Localization;
using BuildAI.Core;
using BuildAI.Core.Buffering;
using BuildAI.Core.Configuration;
using BuildAI.Core.Security;
using BuildAI.Core.Transport;
using BuildAI.Core.Logging;
using Plugin3.LinkChangeMonitor.Monitoring;
using Plugin3.LinkChangeMonitor.UI;

namespace Plugin3.LinkChangeMonitor.Revit
{
    public sealed class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication app)
        {
            LanguageSettings.LoadAndApply();
            try
            {
                PluginLog.Configure("Plugin3.LinkChangeMonitor", Assembly.GetExecutingAssembly().GetName().Version?.ToString(), app.ControlledApplication.VersionNumber);
                var options=BuildAiOptions.Load();
                PluginContext.Options=options;
                PluginContext.Client=new BuildAiClient(options,new RestBuildAiTransport(new WindowsCredentialStore()),new OfflineQueue("outbox.linkchecks.jsonl"));
                PluginContext.Uploader=new LinkCheckUploader(PluginContext.Client,options);
                PluginContext.Fingerprints=new LinkFingerprintService();
                PluginContext.Store = new SnapshotStore();
                PluginContext.Comparer = new LinkComparer(PluginContext.Store);
                PluginContext.ActionHandler = new LinkActionHandler();
                PluginContext.ActionEvent = ExternalEvent.Create(PluginContext.ActionHandler);

                BuildRibbon(app);
                app.ControlledApplication.DocumentChanged += OnDocumentChanged;
                app.Idling += OnIdling;
                PluginLog.Info("Plugin3.LinkChangeMonitor started.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Plugin3 startup failed", ex);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            try
            {
                app.ControlledApplication.DocumentChanged -= OnDocumentChanged;
                app.Idling -= OnIdling;
                try { PluginContext.Window?.Close(); } catch { }
                PluginContext.ActionEvent?.Dispose();
                PluginContext.Client?.Dispose();
            }
            catch (Exception ex) { PluginLog.Error("Plugin3 shutdown failed", ex); }
            return Result.Succeeded;
        }

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                var linkTouched = e.GetModifiedElementIds().Concat(e.GetAddedElementIds())
                    .Select(id => doc.GetElement(id))
                    .Any(x => x is RevitLinkInstance || x is RevitLinkType);
                if (linkTouched) { PluginContext.AutoComparisonPending = true; PluginContext.PendingSinceUtc = DateTime.UtcNow; }
            }
            catch (Exception ex) { PluginLog.Error("Link change event failed", ex); }
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            if (!PluginContext.AutoComparisonPending || PluginContext.ComparisonRunning) return;
            if ((DateTime.UtcNow-PluginContext.PendingSinceUtc).TotalSeconds < Math.Max(1,PluginContext.Options?.LinkCheckDebounceSeconds ?? 7)) return;
            var uiapp = sender as UIApplication;
            var doc = uiapp?.ActiveUIDocument?.Document;
            if (doc == null) return;

            PluginContext.AutoComparisonPending = false;
            if (PluginContext.Fingerprints != null && !PluginContext.Fingerprints.HasChanged(doc)) return;
            PluginContext.ComparisonRunning = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            PluginLog.Info("Automatic Check Links started after a link change event.");
            try
            {
                PluginContext.UiApplication = uiapp;
                var result = PluginContext.Comparer.CompareAndSave(doc);
                PluginContext.LastResult = result;
                PluginContext.Uploader?.Enqueue(result, doc.ProjectInformation?.UniqueId ?? doc.Title, doc.ProjectInformation?.Number ?? "", "automatic");
                if (result.Changes.Count > 0)
                {
                    MonitorWindow.ShowSingleton(result);
                    TaskDialog.Show("BuildAI", $"Detected {result.Changes.Count} linked-model changes.");
                }
            }
            catch (Exception ex) { PluginLog.Error("Automatic link comparison failed", ex); }
            finally
            {
                sw.Stop();
                PluginLog.Info($"Automatic Check Links finished in {sw.ElapsedMilliseconds} ms.");
                PluginContext.ComparisonRunning = false;
            }
        }

        private static void BuildRibbon(UIControlledApplication app)
        {
            const string tab = "BuildAI";
            try { app.CreateRibbonTab(tab); } catch { }
            var panel = app.CreateRibbonPanel(tab, "Link Monitor");
            var asm = Assembly.GetExecutingAssembly().Location;
            RibbonIconLoader.AddButton(panel, new PushButtonData("BuildAI_Link_Check", "Check links", asm,
                "Plugin3.LinkChangeMonitor.Revit.CheckLinksCommand"), "checklinks");
            RibbonIconLoader.AddButton(panel, new PushButtonData("BuildAI_Link_Results", "Changes", asm,
                "Plugin3.LinkChangeMonitor.Revit.OpenMonitorCommand"), "changes");
        }
    }
}
