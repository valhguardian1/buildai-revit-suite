using System;
using System.Diagnostics;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows;
using System.Linq;
using Autodesk.Revit.DB;
using System.Windows.Controls;
using Plugin5.ClashFormaIntegration.Revit;
using BuildAI.Core.Localization;

using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using Grid = System.Windows.Controls.Grid;
using PasswordBox = System.Windows.Controls.PasswordBox;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace Plugin5.ClashFormaIntegration.UI
{
    public sealed class SettingsWindow : Window
    {
        private const string SupportUrl = "https://app.buildai.me/support";
        private readonly TextBox _assigneeRole = new TextBox();
        private readonly ComboBox _language = new ComboBox();
        private readonly PasswordBox _token = new PasswordBox();
        private readonly TextBlock _tokenState = new TextBlock { Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap };

        public SettingsWindow() : this(null) { }
        public SettingsWindow(Document document)
        {
            Title = "BuildAI " + BuildAI.Core.BuildInfo.Version + " — Settings";
            Width = 680;
            Height = 350;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < 7; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddRow(grid, 0, "BuildAI API token", _token);
            AddRow(grid, 1, "Assign Issues to role (optional)", _assigneeRole);

            _language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
            _language.Items.Add(new ComboBoxItem { Content = "עברית", Tag = "he" });
            _language.SelectedIndex = LanguageSettings.CurrentLanguage == "he" ? 1 : 0;
            AddRow(grid, 2, "Language", _language);

            Grid.SetRow(_tokenState, 3);
            Grid.SetColumn(_tokenState, 1);
            grid.Children.Add(_tokenState);

            var note = new TextBlock
            {
                Text = "The token is stored securely in Windows Credential Manager and is not written to the settings JSON file.",
                Margin = new Thickness(4, 10, 4, 10),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(note, 4);
            Grid.SetColumnSpan(note, 2);
            grid.Children.Add(note);

            var support = new Hyperlink(new Run("Contact Support")) { NavigateUri = new Uri(SupportUrl), ToolTip = SupportUrl, Foreground = Brushes.DodgerBlue, Cursor = Cursors.Hand };
            support.Click += (_, __) => OpenSupport();
            var supportText = new TextBlock { Margin = new Thickness(4, 8, 4, 8) };
            supportText.Inlines.Add(support);
            Grid.SetRow(supportText, 5); Grid.SetColumnSpan(supportText, 2); grid.Children.Add(supportText);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var clear = new Button { Content = "Remove token", Margin = new Thickness(4), Padding = new Thickness(12, 7, 12, 7) };
            clear.Click += (_, __) => ClearToken();
            var save = new Button { Content = "Save", Margin = new Thickness(4), Padding = new Thickness(16, 7, 16, 7), IsDefault = true };
            save.Click += (_, __) => Save();
            var cancel = new Button { Content = "Cancel", Margin = new Thickness(4), Padding = new Thickness(16, 7, 16, 7), IsCancel = true };
            buttons.Children.Add(clear);
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            Grid.SetRow(buttons, 6);
            Grid.SetColumnSpan(buttons, 2);
            grid.Children.Add(buttons);

            Content = grid;
            LoadValues();
        }

        private static void AddRow(Grid grid, int row, string label, Control control)
        {
            var text = new TextBlock { Text = label, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
            control.Margin = new Thickness(4);
            Grid.SetRow(text, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(text);
            grid.Children.Add(control);
        }

        private void LoadValues()
        {
            var settings = PluginContext.Settings;
            _assigneeRole.Text = settings.PreferredAssigneeRole ?? "";
            _assigneeRole.ToolTip = "Autodesk project role that receives every Issue. Leave empty to assign by responsible discipline.";

            var existing = PluginContext.CredentialStore.GetApiToken();
            if (string.IsNullOrWhiteSpace(existing) && !string.IsNullOrWhiteSpace(settings.BuildAiApiKey))
            {
                PluginContext.CredentialStore.SetApiToken(settings.BuildAiApiKey);
                settings.BuildAiApiKey = null;
                settings.Save();
                existing = PluginContext.CredentialStore.GetApiToken();
            }
            _tokenState.Text = string.IsNullOrWhiteSpace(existing)
                ? "No token is saved yet."
                : "A token is saved. Leave the field empty to keep it unchanged.";
        }

        private void Save()
        {
            var settings = PluginContext.Settings;
            settings.PreferredAssigneeRole = (_assigneeRole.Text ?? "").Trim();
            var newToken = (_token.Password ?? "").Trim();
            try
            {
                if (!string.IsNullOrWhiteSpace(newToken)) PluginContext.CredentialStore.SetApiToken(newToken);
                settings.Save();
                LanguageSettings.SaveAndApply((_language.SelectedItem as ComboBoxItem)?.Tag as string ?? "en");
                BuildAI.Core.Logging.PluginLog.Info("ACC_SETTINGS_TOKEN_SAVED mode=FullSuite updated="+(!string.IsNullOrWhiteSpace(newToken)));
                MessageBox.Show(this,"Settings saved securely.","BuildAI",MessageBoxButton.OK,MessageBoxImage.Information);
                DialogResult = true;
            }
            catch(Exception ex)
            {
                BuildAI.Core.Logging.PluginLog.Error("ACC_SETTINGS_SAVE_FAILED",ex);
                MessageBox.Show(this,"Settings could not be saved. Check access to Windows Credential Manager.","BuildAI",MessageBoxButton.OK,MessageBoxImage.Error);
            }
        }

        private void OpenSupport()
        {
            try { Process.Start(new ProcessStartInfo(SupportUrl) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                BuildAI.Core.Logging.PluginLog.Error("Support link could not be opened", ex);
                MessageBox.Show(this, "The browser could not open the support page. Copy this URL into your browser:\n" + SupportUrl, "BuildAI Support", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ClearToken()
        {
            PluginContext.CredentialStore.Clear();
            _token.Clear();
            _tokenState.Text = "Token removed.";
        }
    }
}
