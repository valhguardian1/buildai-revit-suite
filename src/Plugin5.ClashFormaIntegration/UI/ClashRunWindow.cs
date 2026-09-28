using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Plugin5.ClashFormaIntegration.Models;
using Plugin5.ClashFormaIntegration.Revit;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;

namespace Plugin5.ClashFormaIntegration.UI
{
    public sealed class ClashRunWindow : Window
    {
        private readonly ComboBox _sourceA = new ComboBox { MinWidth = 360 };
        private readonly ComboBox _sourceB = new ComboBox { MinWidth = 360 };
        private readonly ComboBox _sourceC = new ComboBox { MinWidth = 360 };
        private readonly CheckBox _ab = new CheckBox { Content = "Compare A ↔ B", IsChecked = true, Margin = new Thickness(4) };
        private readonly CheckBox _ac = new CheckBox { Content = "Compare A ↔ C", IsChecked = true, Margin = new Thickness(4) };
        private readonly CheckBox _bc = new CheckBox { Content = "Compare B ↔ C", IsChecked = true, Margin = new Thickness(4) };
        private readonly TextBox _minimumVolume = new TextBox { Text = "0.1" };
        private readonly CheckBox _boxesOnly = new CheckBox { Content = "Diagnostic mode: bounding boxes only", IsChecked = false, Margin = new Thickness(4) };
        private readonly Button _categories = new Button { Content = "Categories...", Padding = new Thickness(12, 4, 12, 4) };
        private readonly TextBlock _categorySummary = new TextBlock { Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly IList<ClashSourceOption> _sources;
        public ClashRunOptions Options { get; private set; }

        public ClashRunWindow(Document document)
        {
            Title = "BuildAI — MEP Comparation"; Width = 680; Height = 430; WindowStartupLocation = WindowStartupLocation.CenterScreen; ResizeMode = ResizeMode.CanResize;
            _sources = PluginContext.Engine.GetSources(document);
            _sourceA.ItemsSource = _sources; _sourceB.ItemsSource = _sources; _sourceC.ItemsSource = _sources;
            _sourceA.SelectedIndex = 0; _sourceB.SelectedIndex = _sources.Count > 1 ? 1 : 0; _sourceC.SelectedIndex = _sources.Count > 2 ? 2 : 0;
            var grid = new Grid { Margin = new Thickness(18) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < 10; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddRow(grid,0,"Source A",_sourceA); AddRow(grid,1,"Source B",_sourceB); AddRow(grid,2,"Source C",_sourceC); AddRow(grid,3,"Minimum intersection volume, mm³",_minimumVolume);
            // Placed directly rather than through AddRow: that helper takes a Control,
            // and StackPanel derives from Panel, not Control.
            var categoryLabel = new TextBlock { Text = "Clash categories", Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(categoryLabel, 7); Grid.SetColumn(categoryLabel, 0); grid.Children.Add(categoryLabel);
            var categoryPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 4, 0) };
            categoryPanel.Children.Add(_categories);
            categoryPanel.Children.Add(_categorySummary);
            _categories.Click += (_, __) => EditCategories();
            Grid.SetRow(categoryPanel, 7); Grid.SetColumn(categoryPanel, 1); grid.Children.Add(categoryPanel);
            RefreshCategorySummary();
            var pairs=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,8)}; pairs.Children.Add(_ab);pairs.Children.Add(_ac);pairs.Children.Add(_bc);Grid.SetRow(pairs,4);Grid.SetColumn(pairs,1);grid.Children.Add(pairs);
            var note=new TextBlock{Margin=new Thickness(4,12,4,12),TextWrapping=TextWrapping.Wrap,Text="Hard clashes are calculated locally. Bounding boxes are used for candidate filtering, followed by exact Revit solid intersection."};Grid.SetRow(note,5);Grid.SetColumnSpan(note,2);grid.Children.Add(note); Grid.SetRow(_boxesOnly,6); Grid.SetColumn(_boxesOnly,1); grid.Children.Add(_boxesOnly);
            var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};var cancel=new Button{Content="Cancel",IsCancel=true,Margin=new Thickness(4),Padding=new Thickness(14,7,14,7)};var run=new Button{Content="Run",IsDefault=true,Margin=new Thickness(4),Padding=new Thickness(18,7,18,7)};run.Click+=(_,__)=>Accept();buttons.Children.Add(cancel);buttons.Children.Add(run);Grid.SetRow(buttons,8);Grid.SetColumnSpan(buttons,2);grid.Children.Add(buttons);Content=grid;
        }
        private static void AddRow(Grid grid,int row,string label,Control control){var text=new TextBlock{Text=label,Margin=new Thickness(4),VerticalAlignment=VerticalAlignment.Center};control.Margin=new Thickness(4);Grid.SetRow(text,row);Grid.SetRow(control,row);Grid.SetColumn(control,1);grid.Children.Add(text);grid.Children.Add(control);}
        private void Accept(){var a=_sourceA.SelectedItem as ClashSourceOption;var b=_sourceB.SelectedItem as ClashSourceOption;var c=_sourceC.SelectedItem as ClashSourceOption;if(a==null||b==null||c==null){MessageBox.Show("Select all three sources.","BuildAI",MessageBoxButton.OK,MessageBoxImage.Warning);return;}if(_ab.IsChecked!=true&&_ac.IsChecked!=true&&_bc.IsChecked!=true){MessageBox.Show("Select at least one comparison pair.","BuildAI",MessageBoxButton.OK,MessageBoxImage.Warning);return;}double minVolume;if(!double.TryParse(_minimumVolume.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out minVolume)||minVolume<0){MessageBox.Show("Enter a valid minimum intersection volume.","BuildAI",MessageBoxButton.OK,MessageBoxImage.Warning);return;}Options=new ClashRunOptions{SourceAKey=a.Key,SourceBKey=b.Key,SourceCKey=c.Key,CompareAB=_ab.IsChecked==true,CompareAC=_ac.IsChecked==true,CompareBC=_bc.IsChecked==true,MinimumIntersectionVolumeMm3=minVolume,GroupACategories=PluginContext.Settings.GroupACategories.ToList(),GroupBCategories=PluginContext.Settings.GroupBCategories.ToList(),GroupCCategories=PluginContext.Settings.GroupCCategories.ToList(),BoundingBoxesOnly=_boxesOnly.IsChecked==true};DialogResult=true;}

        /// <summary>
        /// Opens the category picker and applies the result to all three groups.
        /// The groups are set together because the specification defines one MEP
        /// scope, not a per-group scope; a user who needs asymmetric groups can
        /// still edit them individually in the settings file.
        /// </summary>
        private void EditCategories()
        {
            var window = new ClashCategoryWindow(PluginContext.Settings.GroupACategories) { Owner = this };
            if (window.ShowDialog() != true) return;

            PluginContext.Settings.GroupACategories = window.SelectedCategoryIds.ToList();
            PluginContext.Settings.GroupBCategories = window.SelectedCategoryIds.ToList();
            PluginContext.Settings.GroupCCategories = window.SelectedCategoryIds.ToList();
            PluginContext.Settings.ClashCategoryCatalogueVersion = MepClashCategories.CatalogueVersion;
            try { PluginContext.Settings.Save(); } catch { /* selection still applies to this run */ }
            RefreshCategorySummary();
        }

        private void RefreshCategorySummary()
        {
            var count = PluginContext.Settings.GroupACategories == null ? 0 : PluginContext.Settings.GroupACategories.Count;
            _categorySummary.Text = count == 0
                ? "no filter — every model category will be compared"
                : count + " MEP categories (LOD 350 defaults)";
        }

    }
}
