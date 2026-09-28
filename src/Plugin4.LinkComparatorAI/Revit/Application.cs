using System;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;
using BuildAI.Core.Localization;
using BuildAI.Core.Logging;
using BuildAI.Core.Presentation;

namespace Plugin4.LinkComparatorAI.Revit
{
    public sealed class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication app)
        {
            try { LanguageSettings.LoadAndApply(); } catch { }
            PluginLog.Configure(
                "Plugin4.LinkComparatorAI",
                Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                app.ControlledApplication.VersionNumber);

            // Ribbon registration must not depend on APS, Viewer Probe,
            // settings, WebView2, or ExternalEvent initialization. If one of
            // those services fails, returning Failed here makes Revit remove
            // the entire AR-ST panel and leaves no command from which the user
            // can retry or collect diagnostics.
            try
            {
                RegisterRibbon(app);
            }
            catch (Exception ex)
            {
                PluginLog.Error("Plugin4 ribbon registration failed", ex);
                return Result.Failed;
            }

            try
            {
                PluginContext.EnsureInitialized();
                PluginLog.Info("Plugin4.LinkComparatorAI started.");
            }
            catch (Exception ex)
            {
                // Keep the four AR-ST commands visible. Each command retries
                // initialization in a valid Revit API context and reports the
                // concrete error if the dependency is still unavailable.
                PluginContext.InitializationError = ex;
                PluginLog.Error(
                    "Plugin4 services failed during startup. Ribbon remains available and initialization will be retried from the selected command.",
                    ex);
            }

            return Result.Succeeded;
        }

        private static void RegisterRibbon(UIControlledApplication app)
        {
            const string tab = "BuildAI";
            try { app.CreateRibbonTab(tab); } catch { }

            var panelName = "AR/ST Comparation";
            RibbonPanel panel;
            try
            {
                panel = app.CreateRibbonPanel(tab, panelName);
            }
            catch
            {
                panel = app.GetRibbonPanels(tab)
                    .FirstOrDefault(x => string.Equals(x.Name, panelName, StringComparison.Ordinal));
                if (panel == null) throw;
            }

            var asm = Assembly.GetExecutingAssembly().Location;
            AddButtonIfMissing(panel, new PushButtonData(
                "BuildAI_P4_Compare", "Compare", asm,
                "Plugin4.LinkComparatorAI.Revit.CompareModelsCommand"), "compare");
            AddButtonIfMissing(panel, new PushButtonData(
                "BuildAI_P4_Rooms", "Rooms", asm,
                "Plugin4.LinkComparatorAI.Revit.CheckRoomsCommand"), "rooms");
            AddButtonIfMissing(panel, new PushButtonData(
                "BuildAI_P4_Results", "Results", asm,
                "Plugin4.LinkComparatorAI.Revit.OpenResultsCommand"), "results");
            var settingsPanel=app.GetRibbonPanels(tab).FirstOrDefault(x=>x.Name=="Settings")??app.CreateRibbonPanel(tab,"Settings");
            if(SettingsRibbonPolicy.ShouldCreate(settingsPanel.GetItems().Select(x=>x.Name)))
            {
                RibbonIconLoader.AddButton(settingsPanel,new PushButtonData("BuildAI_Settings","Settings",asm,"Plugin4.LinkComparatorAI.Revit.SettingsCommand"),"settings");
                PluginLog.Info("ACC_SETTINGS_BUTTON_CREATED host=ARST");
            }
            else PluginLog.Info("ACC_SETTINGS_BUTTON_REUSED host=ARST");
        }

        private static void AddButtonIfMissing(RibbonPanel panel, PushButtonData data, string iconName)
        {
            if (panel.GetItems().Any(x => string.Equals(x.Name, data.Name, StringComparison.Ordinal))) return;
            RibbonIconLoader.AddButton(panel, data, iconName);
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            try { PluginContext.ActionEvent?.Dispose(); } catch { }
            return Result.Succeeded;
        }
    }
}
