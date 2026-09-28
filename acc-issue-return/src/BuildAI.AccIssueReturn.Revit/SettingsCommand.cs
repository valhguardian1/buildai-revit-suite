using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BuildAI.AccIssueReturn.Core;
using System;
using System.Diagnostics;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace BuildAI.AccIssueReturn.Revit;

[Transaction(TransactionMode.ReadOnly)]
public sealed class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        try
        {
            var full=AppDomain.CurrentDomain.GetAssemblies()
                .Select(a=>a.GetType("Plugin5.ClashFormaIntegration.UI.SettingsWindow",false))
                .FirstOrDefault(t=>t!=null);
            if(full!=null)
            {
                (Activator.CreateInstance(full,data.Application.ActiveUIDocument?.Document) as Window)?.ShowDialog();
                AccIssueReturnLog.Event("ACC_SETTINGS_OPENED",new{mode="FullSuite"});
            }
            else
            {
                new ReturnTokenSettingsWindow(WindowsCredentialTokenProvider.DetectBuildAiInstall()).ShowDialog();
                AccIssueReturnLog.Event("ACC_SETTINGS_OPENED",new{mode="ReturnPluginOnly"});
            }
            return Result.Succeeded;
        }
        catch(Exception ex)
        {
            AccIssueReturnLog.Event("ACC_SETTINGS_ERROR",new{errorType=ex.GetType().Name});
            message="Settings could not be opened.";
            TaskDialog.Show("BuildAI Settings",message);
            return Result.Failed;
        }
    }
}

internal sealed class ReturnTokenSettingsWindow : Window
{
    private const string SupportUrl="https://app.buildai.me/support";
    private readonly PasswordBox token=new();
    private readonly bool integrated;
    public ReturnTokenSettingsWindow(bool integratedMode)
    {
        integrated=integratedMode;
        Title="BuildAI ACC Issue Return "+typeof(SettingsCommand).Assembly.GetName().Version.Major+"."+typeof(SettingsCommand).Assembly.GetName().Version.Minor+" — Settings";Width=460;MinWidth=360;Height=270;MinHeight=240;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;SizeToContent=SizeToContent.Manual;
        var root=new StackPanel{Margin=new Thickness(18)};
        root.Children.Add(new TextBlock{Text="BuildAI API token",Margin=new Thickness(0,0,0,6)});
        token.MinHeight=28;root.Children.Add(token);
        string source;var existing=WindowsCredentialTokenProvider.ReadBuildAiApiToken(out source);
        root.Children.Add(new TextBlock{Text=string.IsNullOrWhiteSpace(existing)?"No token is saved.":"A token is saved. Leave the field empty to keep it.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,14)});
        var support=new Hyperlink(new Run("Contact Support")){NavigateUri=new Uri(SupportUrl),ToolTip=SupportUrl,Foreground=Brushes.DodgerBlue,Cursor=Cursors.Hand};
        support.Click+=(_,__)=>OpenSupport();
        var supportText=new TextBlock{Margin=new Thickness(0,0,0,12)};supportText.Inlines.Add(support);root.Children.Add(supportText);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content="Cancel",MinWidth=80,Margin=new Thickness(0,0,8,0),IsCancel=true};
        var save=new Button{Content="Save",MinWidth=80,IsDefault=true};
        save.Click+=(_,__)=>Save();buttons.Children.Add(cancel);buttons.Children.Add(save);root.Children.Add(buttons);
        Content=root;
    }
    private void OpenSupport()
    {
        try{Process.Start(new ProcessStartInfo(SupportUrl){UseShellExecute=true});}
        catch(Exception ex)
        {
            AccIssueReturnLog.Event("ACC_SUPPORT_OPEN_FAILED",new{errorType=ex.GetType().Name});
            MessageBox.Show(this,"The browser could not open the support page. Copy this URL into your browser:\n"+SupportUrl,"BuildAI Support",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
    }
    private void Save()
    {
        try
        {
            var value=token.Password?.Trim();
            if(!string.IsNullOrWhiteSpace(value))WindowsCredentialTokenProvider.SaveApiToken(value,integrated);
            AccIssueReturnLog.Event("ACC_SETTINGS_TOKEN_SAVED",new{mode=integrated?"FullSuite":"ReturnPluginOnly",updated=!string.IsNullOrWhiteSpace(value)});
            MessageBox.Show(this,"Settings saved securely.","BuildAI",MessageBoxButton.OK,MessageBoxImage.Information);
            DialogResult=true;
        }
        catch(Exception ex)
        {
            AccIssueReturnLog.Event("ACC_SETTINGS_SAVE_FAILED",new{errorType=ex.GetType().Name});
            MessageBox.Show(this,"The token could not be saved in Windows Credential Manager.","BuildAI",MessageBoxButton.OK,MessageBoxImage.Error);
        }
    }
}
