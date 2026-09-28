using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BuildAI.Core;
using BuildAI.Core.Configuration;
using BuildAI.Core.Localization;
using BuildAI.Core.Logging;
using BuildAI.Core.Security;
using Plugin1.TimeModelAnalytics.UI;

namespace Plugin1.TimeModelAnalytics.Revit
{
    /// <summary>Prompt for the API token and store it in Credential Manager.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ConnectCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var store = new WindowsCredentialStore();
                var current = store.GetApiToken();
                if (TokenDialog.TryPrompt(current, out var token))
                {
                    store.SetApiToken(token);
                    PluginContext.Client?.Resume();
                    TaskDialog.Show(Loc.T("Ribbon_Tab"), Loc.T("Token_Saved"));
                }
                return Result.Succeeded;
            }
            catch (Exception ex) { PluginLog.Error("ConnectCommand failed", ex); message = ex.Message; return Result.Failed; }
        }
    }

    /// <summary>Toggle tracking between paused and active.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class PauseResumeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var client = PluginContext.Client;
            if (client == null) return Result.Succeeded;
            if (client.State == ConnectionState.Paused) client.Resume();
            else client.Pause();
            TaskDialog.Show(Loc.T("Ribbon_Tab"),
                client.State == ConnectionState.Paused ? Loc.T("Status_Paused") : Loc.T("Status_Connected"));
            return Result.Succeeded;
        }
    }

    /// <summary>Show the modeless session-status window (RTL-aware).</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ShowStatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try { StatusWindow.ShowSingleton(PluginContext.Session); return Result.Succeeded; }
            catch (Exception ex) { PluginLog.Error("ShowStatusCommand failed", ex); message = ex.Message; return Result.Failed; }
        }
    }
}
