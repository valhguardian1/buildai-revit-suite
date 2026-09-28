using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BuildAI.Core;
using BuildAI.Core.Localization;
using BuildAI.Core.Logging;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;

namespace Plugin4.LinkComparatorAI.UI
{
    public sealed class GeneralSettingsWindow : Window
    {
        private const string SupportUrl = "https://app.buildai.me/support";
        private readonly ComboBox _language = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 0, 0, 12) };

        public GeneralSettingsWindow()
        {
            Title = "BuildAI " + BuildInfo.Version + " — Settings";
            Width = 380; Height = 220; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(new TextBlock { Text = "Language", Margin = new Thickness(0, 0, 0, 6) });
            _language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
            _language.Items.Add(new ComboBoxItem { Content = "עברית", Tag = "he" });
            _language.SelectedIndex = LanguageSettings.CurrentLanguage == "he" ? 1 : 0;
            root.Children.Add(_language);
            var link = new Hyperlink(new Run("Contact Support")) { NavigateUri = new Uri(SupportUrl), ToolTip = SupportUrl, Foreground = Brushes.DodgerBlue, Cursor = Cursors.Hand };
            link.Click += (_, __) => OpenSupport();
            var support = new TextBlock { Margin = new Thickness(0, 0, 0, 14) };
            support.Inlines.Add(link); root.Children.Add(support);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "Cancel", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var save = new Button { Content = "Save", Width = 80, IsDefault = true };
            save.Click += (_, __) => { LanguageSettings.SaveAndApply((_language.SelectedItem as ComboBoxItem)?.Tag as string ?? "en"); DialogResult = true; };
            buttons.Children.Add(cancel); buttons.Children.Add(save); root.Children.Add(buttons);
            Content = root;
        }

        private void OpenSupport()
        {
            try { Process.Start(new ProcessStartInfo(SupportUrl) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                PluginLog.Error("Support link could not be opened", ex);
                MessageBox.Show(this, "The browser could not open the support page. Copy this URL into your browser:\n" + SupportUrl, "BuildAI Support", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
