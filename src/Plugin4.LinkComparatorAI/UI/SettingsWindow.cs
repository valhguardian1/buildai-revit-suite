using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using BuildAI.Core.Localization;
using BuildAI.Core.Logging;
using Plugin4.LinkComparatorAI.Comparison;
using Plugin4.LinkComparatorAI.Models;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;

namespace Plugin4.LinkComparatorAI.UI
{
    public sealed class SettingsWindow : Window
    {
        private readonly ComboBox _ar = new ComboBox();
        private readonly ComboBox _st = new ComboBox();
        private readonly TextBox _position = new TextBox();
        private readonly TextBox _elevation = new TextBox();
        public ComparatorSettings Settings { get; }

        public SettingsWindow(Document doc, ComparatorSettings settings)
        {
            Settings = settings ?? new ComparatorSettings();
            Title = "AR-ST Comparison Settings";
            Width = 650; Height = 310; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FlowDirection = Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            var root = new Grid { Margin = new Thickness(18) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var links = LinkResolver.GetLoadedLinks(doc).Select(x => new LinkChoice(x.UniqueId, x.Name)).ToList();
            links.Insert(0, new LinkChoice(ComparatorSettings.HostSourceId, "Host Model — " + doc.Title));
            _ar.ItemsSource = links; _st.ItemsSource = links;
            _ar.DisplayMemberPath = "Name"; _st.DisplayMemberPath = "Name";
            _ar.SelectedItem = links.FirstOrDefault(x => x.UniqueId == Settings.ArchitecturalLinkUniqueId) ?? links.FirstOrDefault();
            _st.SelectedItem = links.FirstOrDefault(x => x.UniqueId == Settings.StructuralLinkUniqueId) ?? links.Skip(1).FirstOrDefault() ?? links.FirstOrDefault();
            _position.Text = Settings.PositionToleranceMm.ToString();
            _elevation.Text = Settings.ElevationToleranceMm.ToString();
            Add(root, 0, "Architectural source (AR)", _ar);
            Add(root, 1, "Structural source (ST)", _st);
            Add(root, 2, "Position tolerance, mm", _position);
            Add(root, 3, "Elevation tolerance, mm", _elevation);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var compare = new Button { Content = "Compare", Width = 110, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
            compare.Click += Confirm;
            var cancel = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            cancel.Click += (_, __) => { PluginLog.Info("ARST_SETTINGS_CANCELLED"); DialogResult = false; };
            buttons.Children.Add(cancel); buttons.Children.Add(compare);
            Grid.SetRow(buttons, 4); Grid.SetColumnSpan(buttons, 2); root.Children.Add(buttons);
            Content = root;
        }

        private static void Add(Grid grid, int row, string label, Control control)
        {
            var text = new TextBlock { Text = label, Margin = new Thickness(0, 7, 12, 7), VerticalAlignment = VerticalAlignment.Center };
            control.Margin = new Thickness(0, 5, 0, 5); control.MinHeight = 28;
            Grid.SetRow(text, row); Grid.SetRow(control, row); Grid.SetColumn(control, 1);
            grid.Children.Add(text); grid.Children.Add(control);
        }

        private void Confirm(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(_position.Text, out var position) || !double.TryParse(_elevation.Text, out var elevation)
                || double.IsNaN(position) || double.IsInfinity(position) || position < 0
                || double.IsNaN(elevation) || double.IsInfinity(elevation) || elevation < 0
                || !(_ar.SelectedItem is LinkChoice ar) || !(_st.SelectedItem is LinkChoice st))
            {
                PluginLog.Info("ARST_SETTINGS_VALIDATION_FAILED");
                MessageBox.Show(this, Loc.IsRightToLeft ? "בדוק את המקורות ואת ערכי הסבילות." : "Select both sources and enter non-negative numeric tolerances.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Settings.ArchitecturalLinkUniqueId = ar.UniqueId;
            Settings.StructuralLinkUniqueId = st.UniqueId;
            Settings.PositionToleranceMm = position;
            Settings.ElevationToleranceMm = elevation;
            PluginLog.Info("ARST_SETTINGS_VALIDATED");
            PluginLog.Info("ARST_SETTINGS_CONFIRMED");
            DialogResult = true;
        }

        private sealed class LinkChoice
        {
            public string UniqueId { get; }
            public string Name { get; }
            public LinkChoice(string id, string name) { UniqueId = id; Name = name; }
        }
    }
}
