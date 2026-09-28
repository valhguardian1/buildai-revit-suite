using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Plugin5.ClashFormaIntegration.Models;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Grid = System.Windows.Controls.Grid;

namespace Plugin5.ClashFormaIntegration.UI
{
    /// <summary>
    /// Category picker for clash detection, grouped by discipline and seeded from the
    /// LOD 350 MEP specification.
    /// <para>
    /// The window exists because the filter is otherwise unreachable: the setting is
    /// stored per group but had no UI, so nobody could correct a bad selection without
    /// editing JSON. It also makes the cost of an empty selection explicit, since an
    /// empty list means "admit every category" to the engine rather than "none".
    /// </para>
    /// </summary>
    public sealed class ClashCategoryWindow : Window
    {
        private readonly List<CheckBox> _boxes = new List<CheckBox>();
        private readonly TextBlock _summary = new TextBlock { Margin = new Thickness(4, 8, 4, 4), TextWrapping = TextWrapping.Wrap };

        public List<int> SelectedCategoryIds { get; private set; }

        public ClashCategoryWindow(IEnumerable<int> current)
        {
            Title = "BuildAI — Clash categories";
            Width = 720;
            Height = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var selected = new HashSet<int>(current ?? Enumerable.Empty<int>());

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var intro = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 4, 10),
                Text = "Categories carrying real MEP geometry are enabled by default. " +
                       "Optional ones are off because they generate a high share of low-value hits. " +
                       "Clearing everything does NOT disable clash detection — the engine then admits every " +
                       "model category in the document, which is far noisier than any selection here."
            };
            Grid.SetRow(intro, 0);
            root.Children.Add(intro);

            var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var list = new StackPanel();

            foreach (var discipline in MepClashCategories.All
                         .Where(x => x.Mode != ClashCategoryMode.Never)
                         .GroupBy(x => x.Discipline))
            {
                list.Children.Add(new TextBlock
                {
                    Text = discipline.Key,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(4, 12, 4, 4)
                });

                foreach (var definition in discipline)
                {
                    int categoryId;
                    var available = MepClashCategories.TryResolve(definition.BuiltInCategoryName, out categoryId);

                    var label = definition.DisplayName + "  (" + definition.BuiltInCategoryName + ")";
                    if (definition.Mode == ClashCategoryMode.Optional) label += "  — optional";
                    if (definition.Mode == ClashCategoryMode.Conditional) label += "  — needs a family-name filter";
                    if (!available) label += "  — not available in this Revit version";

                    var box = new CheckBox
                    {
                        Content = label,
                        Margin = new Thickness(18, 2, 4, 2),
                        IsEnabled = available,
                        IsChecked = available && selected.Contains(categoryId),
                        Tag = available ? (object)categoryId : null,
                        ToolTip = string.IsNullOrWhiteSpace(definition.Note)
                            ? "Flexibility rank " + definition.FlexibilityRank
                            : "Flexibility rank " + definition.FlexibilityRank + ". " + definition.Note
                    };
                    box.Checked += (_, __) => UpdateSummary();
                    box.Unchecked += (_, __) => UpdateSummary();
                    _boxes.Add(box);
                    list.Children.Add(box);
                }
            }

            list.Children.Add(new TextBlock
            {
                Text = "Never eligible (no own solid geometry)",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(4, 16, 4, 4)
            });
            foreach (var definition in MepClashCategories.Excluded)
            {
                list.Children.Add(new TextBlock
                {
                    Text = "• " + definition.DisplayName + " (" + definition.BuiltInCategoryName + ") — " + definition.Note,
                    Margin = new Thickness(18, 1, 4, 1),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray
                });
            }

            scroller.Content = list;
            Grid.SetRow(scroller, 1);
            root.Children.Add(scroller);

            Grid.SetRow(_summary, 2);
            root.Children.Add(_summary);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var restore = new Button { Content = "Restore defaults", Margin = new Thickness(4), Padding = new Thickness(12, 7, 12, 7) };
            restore.Click += (_, __) => RestoreDefaults();
            var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(4), Padding = new Thickness(16, 7, 16, 7) };
            var save = new Button { Content = "OK", IsDefault = true, Margin = new Thickness(4), Padding = new Thickness(16, 7, 16, 7) };
            save.Click += (_, __) => Accept();
            buttons.Children.Add(restore);
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            Content = root;
            UpdateSummary();
        }

        private void RestoreDefaults()
        {
            var defaults = new HashSet<int>(MepClashCategories.DefaultCategoryIds());
            foreach (var box in _boxes)
            {
                if (!(box.Tag is int)) continue;
                box.IsChecked = defaults.Contains((int)box.Tag);
            }
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            var count = _boxes.Count(x => x.IsChecked == true);
            if (count == 0)
            {
                _summary.Foreground = Brushes.Firebrick;
                _summary.Text = "Nothing selected. The engine will fall back to admitting every model category.";
            }
            else
            {
                _summary.Foreground = Brushes.Black;
                _summary.Text = count + " categories selected.";
            }
        }

        private void Accept()
        {
            SelectedCategoryIds = _boxes
                .Where(x => x.IsChecked == true && x.Tag is int)
                .Select(x => (int)x.Tag)
                .Distinct()
                .ToList();

            if (SelectedCategoryIds.Count == 0)
            {
                var confirm = MessageBox.Show(
                    "No categories are selected.\n\n" +
                    "This does not narrow the search — it removes the filter entirely, and clash detection " +
                    "will compare every model category in the document.\n\nContinue anyway?",
                    "BuildAI — Clash categories",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
            }

            DialogResult = true;
        }
    }
}
