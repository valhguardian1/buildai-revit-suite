using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Plugin3.LinkChangeMonitor.Models;
using BuildAI.Core.Sorting;
using BuildAI.Core.Presentation;
using BuildAI.Core.Logging;
using Plugin3.LinkChangeMonitor.Monitoring;
using Plugin3.LinkChangeMonitor.Revit;
namespace Plugin3.LinkChangeMonitor.UI
{
    public sealed class MonitorWindow : Window
    {
        private static MonitorWindow _instance;
        private readonly DataGrid _grid;
        private readonly ComboBox _typeFilter;
        private readonly TextBox _search;
        private readonly TextBlock _summary;
        private readonly TextBlock _added;
        private readonly TextBlock _modified;
        private readonly TextBlock _deleted;
        private List<LinkChangeItem> _all = new List<LinkChangeItem>();

        public static void ShowSingleton(ComparisonResult result)
        {
            if (_instance == null)
            {
                _instance = new MonitorWindow();
                _instance.Closed += (s, e) => { _instance = null; PluginContext.Window = null; };
                PluginContext.Window = _instance;
                _instance.Show();
            }
            _instance.Populate(result);
            _instance.Activate();
        }

        private MonitorWindow()
        {
            Title = "BuildAI — Linked Model Changes";
            Icon = Plugin3.LinkChangeMonitor.Revit.RibbonIconLoader.LoadBrand();
            Width = 1480;
            Height = 760;
            MinWidth = 1050;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White;

            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
            headerPanel.Children.Add(new Image { Source = Plugin3.LinkChangeMonitor.Revit.RibbonIconLoader.LoadBrand(), Width = 28, Height = 28 });
            var title = new TextBlock
            {
                Text = "LINKED MODEL CHANGE REPORT",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            title.Margin = new Thickness(8, 0, 0, 0); headerPanel.Children.Add(title);
            Grid.SetRow(headerPanel, 0);
            root.Children.Add(headerPanel);

            var top = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _summary = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            top.Children.Add(_summary);

            var cards = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _added = SummaryCard(cards, "Added", Color.FromRgb(226, 240, 217));
            _modified = SummaryCard(cards, "Modified", Color.FromRgb(255, 242, 204));
            _deleted = SummaryCard(cards, "Deleted", Color.FromRgb(244, 204, 204));
            Grid.SetColumn(cards, 1);
            top.Children.Add(cards);
            Grid.SetRow(top, 1);
            root.Children.Add(top);

            var filters = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };
            var filterLabel = new TextBlock { Text = "Status:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            filters.Children.Add(filterLabel);
            _typeFilter = new ComboBox { Width = 160, Margin = new Thickness(0, 0, 12, 0) };
            _typeFilter.Items.Add("All changes");
            _typeFilter.Items.Add("Added");
            _typeFilter.Items.Add("Modified");
            _typeFilter.Items.Add("Deleted");
            _typeFilter.SelectedIndex = 0;
            _typeFilter.SelectionChanged += (s, e) => ApplyFilter();
            filters.Children.Add(_typeFilter);
            filters.Children.Add(new TextBlock { Text = "Search:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _search = new TextBox { Width = 380, ToolTip = "Search by model, level, category, type, ID, or description" };
            _search.TextChanged += (s, e) => ApplyFilter();
            filters.Children.Add(_search);
            Grid.SetRow(filters, 2);
            root.Children.Add(filters);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Single,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserReorderColumns = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                HorizontalGridLinesBrush = Brushes.Black,
                VerticalGridLinesBrush = Brushes.Black,
                AlternatingRowBackground = Brushes.White,
                RowBackground = ColorBrush(255, 230, 153),
                ColumnHeaderStyle = HeaderStyle(),
                RowStyle = ChangeRowStyle()
            };

            _grid.Columns.Add(TextColumn("#", "RowNumber", 48, "Sequential row number in the current filtered result."));
            _grid.Columns.Add(TextColumn("Status", "ChangeTypeText", 92, "Whether the linked element was added, modified, or deleted."));
            _grid.Columns.Add(TextColumn("Element ID", "PositionCode", 105, "Revit element identifier in the linked model."));
            _grid.Columns.Add(TextColumn("Description", "ElementName", 220, "Element name or short description."));
            _grid.Columns.Add(TextColumn("Linked model", "LinkInstanceName", 190, "Linked Revit model that contains the changed element."));
            _grid.Columns.Add(TextColumn("Level", "Level", 125, "Level associated with the changed element."));
            _grid.Columns.Add(TextColumn("Category", "Category", 155, "Revit category of the changed element."));
            _grid.Columns.Add(TextColumn("Type", "TypeName", 180, "Revit family type or element type."));
            _grid.Columns.Add(TextColumn("Changed parameters", "ChangedParameters", 240, "Parameters whose values changed since the previous baseline."));
            _grid.Columns.Add(TextColumn("Before", "BeforeSummary", 300, "Recorded values before the change."));
            _grid.Columns.Add(TextColumn("After", "AfterSummary", 300, "Recorded values after the change."));
            _grid.MouseDoubleClick += (s, e) => HighlightSelected();
            Grid.SetRow(_grid, 3);
            root.Children.Add(_grid);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            buttons.Children.Add(Button("Show element", (s, e) => HighlightSelected()));
            buttons.Children.Add(Button("Clear selection", (s, e) => ResetSelection()));
            buttons.Children.Add(Button("Export", (s, e) => Export()));
            Grid.SetRow(buttons, 4);
            root.Children.Add(buttons);
        }

        public void Populate(ComparisonResult result)
        {
            result = result ?? new ComparisonResult();
            _all = result.Changes ?? new List<LinkChangeItem>();
            _summary.Text = $"Linked models checked: {result.LinksScanned}. Baselines created: {result.BaselinesCreated}." +
                            (result.Warnings.Count > 0 ? "\n" + string.Join("\n", result.Warnings) : "");
            _added.Text = _all.Count(x => x.ChangeType == LinkChangeType.Added).ToString();
            _modified.Text = _all.Count(x => x.ChangeType == LinkChangeType.Modified).ToString();
            _deleted.Text = _all.Count(x => x.ChangeType == LinkChangeType.Deleted).ToString();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            IEnumerable<LinkChangeItem> rows = _all;
            var selected = _typeFilter.SelectedItem as string;
            if (selected == "Added") rows = rows.Where(x => x.ChangeType == LinkChangeType.Added);
            if (selected == "Modified") rows = rows.Where(x => x.ChangeType == LinkChangeType.Modified);
            if (selected == "Deleted") rows = rows.Where(x => x.ChangeType == LinkChangeType.Deleted);

            var q = (_search.Text ?? "").Trim();
            if (q.Length > 0)
            {
                rows = rows.Where(x =>
                    Contains(x.Level, q) || Contains(x.Category, q) || Contains(x.ElementName, q) ||
                    Contains(x.LinkInstanceName, q) || Contains(x.TypeName, q) || Contains(x.PositionCode, q) ||
                    Contains(x.ChangedParameters, q));
            }

            var result = rows.OrderBy(x => x.CategorySortOrder)
                             .ThenBy(x => x.Category, NaturalStringComparer.OrdinalIgnoreCase)
                             .ThenBy(x => x.ChangeTypeSortOrder)
                             .ThenBy(x => x.NaturalSortName, NaturalStringComparer.OrdinalIgnoreCase)
                             .ThenBy(x => x.LevelElevation)
                             .ThenBy(x => x.ElementId, NaturalStringComparer.OrdinalIgnoreCase)
                             .Select((item, index) => new ChangeRow(item, index + 1))
                             .ToList();
            _grid.ItemsSource = result;
        }

        private void HighlightSelected()
        {
            var row = _grid.SelectedItem as ChangeRow;
            var item = row?.Item;
            if (item == null || !item.CanHighlight) return;
            PluginContext.ActionHandler.RequestHighlight(item);
            PluginContext.ActionEvent.Raise();
        }

        private void ResetSelection()
        {
            _grid.UnselectAll();
            _grid.SelectedItem = null;
            PluginContext.ActionHandler.RequestReset();
            PluginContext.ActionEvent.Raise();
        }

        private void Export()
        {
            var visible = (_grid.ItemsSource as IEnumerable<ChangeRow>)?.ToList() ?? _all.Select((item,index)=>new ChangeRow(item,index+1)).ToList();
            var dialog = new SaveFileDialog
            {
                Filter = TableExport.DialogFilter,
                FileName = "BuildAI_Link_Changes",
                AddExtension = true,
                DefaultExt = ".xlsx",
                OverwritePrompt=true
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                var target=TableExport.Resolve(dialog.FileName,dialog.FilterIndex);
                var headers=new[]{"#","Status","Element ID","Description","Linked model","Level","Category","Type","Changed parameters","Before","After"};
                var values=visible.Select(r=>(IReadOnlyList<string>)new[]{r.RowNumber.ToString(),r.ChangeTypeText,r.PositionCode,r.ElementName,r.LinkInstanceName,r.Level,r.Category,r.TypeName,r.ChangedParameters,r.BeforeSummary,r.AfterSummary}).ToList();
                TableExport.Write(target.Path,target.Format,headers,values);
                PluginLog.Info("ACC_TABLE_EXPORTED module=LinkMonitor format="+target.Format+" rows="+values.Count+" columns="+headers.Length);
                MessageBox.Show(this, "Report saved: "+target.Path, "BuildAI", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                PluginLog.Error("ACC_TABLE_EXPORT_FAILED module=LinkMonitor",ex);
                MessageBox.Show(this, "Failed to export the report.", "BuildAI", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static TextBlock SummaryCard(Panel parent, string caption, Color color)
        {
            var value = new TextBlock { FontSize = 18, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = caption, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
            panel.Children.Add(value);
            parent.Children.Add(new Border
            {
                Background = new SolidColorBrush(color),
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(6, 0, 0, 0),
                Child = panel,
                MinWidth = 105
            });
            return value;
        }

        private static Style HeaderStyle()
        {
            var style = new Style(typeof(DataGridColumnHeader));
            style.Setters.Add(new Setter(Control.BackgroundProperty, ColorBrush(149, 185, 218)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
            style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5, 8, 5, 8)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Black));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            return style;
        }

        private static Style ChangeRowStyle()
        {
            var style = new Style(typeof(DataGridRow));
            style.Setters.Add(new Setter(Control.BackgroundProperty, ColorBrush(255, 230, 153)));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Top));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0, 3, 0, 3)));

            var added = new DataTrigger { Binding = new Binding("ChangeType"), Value = LinkChangeType.Added };
            added.Setters.Add(new Setter(Control.BackgroundProperty, ColorBrush(226, 240, 217)));
            style.Triggers.Add(added);

            var modified = new DataTrigger { Binding = new Binding("ChangeType"), Value = LinkChangeType.Modified };
            modified.Setters.Add(new Setter(Control.BackgroundProperty, ColorBrush(255, 242, 204)));
            style.Triggers.Add(modified);

            var deleted = new DataTrigger { Binding = new Binding("ChangeType"), Value = LinkChangeType.Deleted };
            deleted.Setters.Add(new Setter(Control.BackgroundProperty, ColorBrush(244, 204, 204)));
            style.Triggers.Add(deleted);
            return style;
        }

        private static DataGridTextColumn TextColumn(string header, string path, double width, string tooltip)
        {
            return new DataGridTextColumn
            {
                Header = new TextBlock { Text = header, ToolTip = tooltip },
                Binding = new Binding(path),
                Width = width,
                ElementStyle = CellTextStyle()
            };
        }

        private static Style CellTextStyle()
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Top));
            style.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(4, 2, 4, 2)));
            return style;
        }

        private static Button Button(string text, RoutedEventHandler onClick)
        {
            var button = new Button { Content = text, MinWidth = 130, Height = 32, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 0, 10, 0) };
            button.Click += onClick;
            return button;
        }

        private static SolidColorBrush ColorBrush(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
        private static bool Contains(string value, string query) => (value ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        private sealed class ChangeRow
        {
            public ChangeRow(LinkChangeItem item, int rowNumber) { Item = item; RowNumber = rowNumber; }
            public LinkChangeItem Item { get; }
            public int RowNumber { get; }
            public LinkChangeType ChangeType => Item.ChangeType;
            public string ChangeTypeText => Item.ChangeTypeText;
            public string PositionCode => Item.PositionCode;
            public string ElementName => Item.ElementName;
            public string LinkInstanceName => Item.LinkInstanceName;
            public string Level => Item.Level;
            public string Category => Item.Category;
            public string TypeName => Item.TypeName;
            public string ChangedParameters => Item.ChangedParameters;
            public string BeforeSummary => Item.BeforeSummary;
            public string AfterSummary => Item.AfterSummary;
        }
    }
}
