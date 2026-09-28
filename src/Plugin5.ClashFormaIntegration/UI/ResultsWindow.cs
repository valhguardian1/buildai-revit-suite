using Plugin5.ClashFormaIntegration.Clash;
using BuildAI.Core.APS;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BuildAI.Core;
using BuildAI.Core.Presentation;
using BuildAI.Core.Logging;
using Microsoft.Win32;
using BuildAI.Core.Issues;
using BuildAI.RevitCompatibility;
using Plugin5.ClashFormaIntegration.Issues;
using Plugin5.ClashFormaIntegration.Models;
using Plugin5.ClashFormaIntegration.Revit;

using Button = System.Windows.Controls.Button;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridTextColumn = System.Windows.Controls.DataGridTextColumn;
using DockPanel = System.Windows.Controls.DockPanel;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Plugin5.ClashFormaIntegration.UI
{
    public sealed class ResultsWindow : Window
    {
        private readonly DataGrid _grid = new DataGrid { AutoGenerateColumns=false, IsReadOnly=false, Margin=new Thickness(8), SelectionMode=DataGridSelectionMode.Single, EnableRowVirtualization=true, EnableColumnVirtualization=true };
        private readonly TextBlock _summary = new TextBlock { Margin=new Thickness(8), FontSize=14, TextWrapping=TextWrapping.Wrap };
        private readonly Button _summaryToggle = new Button { Content="Show more", HorizontalAlignment=HorizontalAlignment.Left, Margin=new Thickness(8,0,8,6), Padding=new Thickness(8,2,8,2) };
        private string _summaryFull=string.Empty;
        private bool _summaryExpanded;
        private readonly TextBlock _aiSummary = new TextBlock { Margin=new Thickness(8), TextWrapping=TextWrapping.Wrap };
        private readonly CheckBox _selectAllIssues = new CheckBox { IsThreeState=false, VerticalAlignment=VerticalAlignment.Center, ToolTip="Select or clear all results that do not already have an APS Issue." };
        private const string BlankFilterValue = "<blank>";
        private readonly TextBox _search = new TextBox { Width=210 };
        private readonly ComboBox _stateFilter = new ComboBox { Width=125 };
        private readonly ComboBox _columnFilter = new ComboBox { Width=125 };
        private readonly ComboBox _valueFilter = new ComboBox { Width=180 };
        private readonly TextBlock _activeFilters = new TextBlock { Margin=new Thickness(12,3,0,0), FontWeight=FontWeights.Bold, VerticalAlignment=VerticalAlignment.Center };
        private readonly TextBlock _pagingStatus = new TextBlock { Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        private readonly Button _loadMore = new Button { Content = "Load more", Margin = new Thickness(8), Padding = new Thickness(12, 6, 12, 6) };
        private readonly Button _loadAll = new Button { Content = "Check all remaining", Margin = new Thickness(8), Padding = new Thickness(12, 6, 12, 6) };

        private readonly CheckBox _groupByCategories = new CheckBox { Content="Group by categories", IsChecked=true, Margin=new Thickness(12,3,0,0), VerticalAlignment=VerticalAlignment.Center };
        private readonly Dictionary<string,string> _columnFilters = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        private bool _updatingSelectAll;
        private bool _updatingFilterValues;
        private bool _issueCreationInProgress;

        public ResultsWindow()
        {
            Title="BuildAI — Clash Detection Results"; Icon=Plugin5.ClashFormaIntegration.Revit.RibbonIconLoader.LoadBrand(); Width=1580; Height=820;
            AddIssueColumn();AddAssigneeColumn();
            AddColumn("State","CreationStateText",145,"Issue creation state.");
            AddColumn("Issue ID","ApsIssueId",180,"Autodesk Issue identifier.");
            AddColumn("Created UTC","ApsIssueCreatedAt",145,"Issue creation time reported by BuildAI.","u");
            AddColumn("Source A","DisplaySourceA",150,"First model source.");
            AddColumn("Element A","ElementA",180,"First element.");
            AddColumn("Category A","CategoryA",110,"Category of first element.");
            AddColumn("Source B","DisplaySourceB",150,"Second model source.");
            AddColumn("Element B","ElementB",180,"Second element.");
            AddColumn("Category B","CategoryB",110,"Category of second element.");
            AddColumn("Level","Level",90,"Closest Revit level.");
            AddColumn("Volume, mm³","IntersectionVolumeMm3",105,"Intersection volume.","N3");
            AddColumn("Confidence","AiConfidence",85,"AI confidence from 0 to 1.","P0");
            _grid.GridLinesVisibility=DataGridGridLinesVisibility.All; _grid.ColumnHeaderStyle=HeaderStyle(); _grid.RowStyle=BuildRowStyle();
            _grid.GroupStyle.Add(BuildCategoryGroupStyle());

            var root=new DockPanel();
            var titlePanel=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,10,0,6)};
            titlePanel.Children.Add(new Image{Source=Plugin5.ClashFormaIntegration.Revit.RibbonIconLoader.LoadBrand(),Width=28,Height=28});
            titlePanel.Children.Add(new TextBlock{Text="CLASH DETECTION RESULTS",FontSize=17,FontWeight=FontWeights.Bold,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0)});
            DockPanel.SetDock(titlePanel,Dock.Top);root.Children.Add(titlePanel);
            var summaryPanel=new StackPanel();summaryPanel.Children.Add(_summary);_summaryToggle.Click+=(_,__)=>{_summaryExpanded=!_summaryExpanded;ShowSummary();PluginLog.Info("ACC_SUMMARY_TOGGLED module=Clash expanded="+_summaryExpanded);};summaryPanel.Children.Add(_summaryToggle);DockPanel.SetDock(summaryPanel,Dock.Top);root.Children.Add(summaryPanel);
            var aiBox=new Border{BorderBrush=Brushes.Gray,BorderThickness=new Thickness(1),Margin=new Thickness(8,0,8,6),Padding=new Thickness(6),Child=_aiSummary};DockPanel.SetDock(aiBox,Dock.Top);root.Children.Add(aiBox);
            var filters=new WrapPanel{Margin=new Thickness(8,0,8,6)};
            filters.Children.Add(new TextBlock{Text="Search:",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,6,0)});
            _search.TextChanged+=(_,__)=>ApplyFilter();filters.Children.Add(_search);
            filters.Children.Add(new TextBlock{Text="Issue state:",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,6,0)});
            foreach(var x in new[]{"All","New","Already created","Failed"})_stateFilter.Items.Add(x);
            _stateFilter.SelectedIndex=0;_stateFilter.SelectionChanged+=(_,__)=>ApplyFilter();filters.Children.Add(_stateFilter);
            filters.Children.Add(new TextBlock{Text="Column:",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,6,0)});
            foreach(var x in new[]{"Source A","Source type A","Element A","Element A ID","Category A","Source B","Source type B","Element B","Element B ID","Category B","Level","Clash type","Severity","Issue status","AI result","AI severity"})_columnFilter.Items.Add(x);
            _columnFilter.SelectedIndex=0;_columnFilter.SelectionChanged+=(_,__)=>RefreshFilterValues();filters.Children.Add(_columnFilter);
            filters.Children.Add(new TextBlock{Text="Value:",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,6,0)});filters.Children.Add(_valueFilter);
            filters.Children.Add(MakeButton("Add filter",(_,__)=>AddExactFilter()));
            filters.Children.Add(MakeButton("Remove filter",(_,__)=>RemoveSelectedFilter()));
            filters.Children.Add(MakeButton("Clear filters",(_,__)=>ClearFilters()));
            _groupByCategories.Click+=(_,__)=>ApplyFilter();filters.Children.Add(_groupByCategories);filters.Children.Add(_activeFilters);
            DockPanel.SetDock(filters,Dock.Top);root.Children.Add(filters);
            // Paging footer, above the action bar so the partial-results notice sits
            // next to the grid it describes rather than being buried by buttons.
            var paging=new DockPanel{LastChildFill=true,Margin=new Thickness(0,4,0,0)};
            var pagingButtons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
            _loadMore.Click+=async(_,__)=>await LoadMoreAsync(false);
            _loadAll.Click+=async(_,__)=>await LoadMoreAsync(true);
            pagingButtons.Children.Add(_loadMore);pagingButtons.Children.Add(_loadAll);
            DockPanel.SetDock(pagingButtons,Dock.Right);paging.Children.Add(pagingButtons);paging.Children.Add(_pagingStatus);
            DockPanel.SetDock(paging,Dock.Bottom);root.Children.Add(paging);
            var bar=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
            bar.Children.Add(MakeButton("Show in model",(_,__)=>ShowSelected()));
            bar.Children.Add(MakeButton("Select visible group",(_,__)=>SelectVisibleGroup()));
            bar.Children.Add(MakeButton("AI Analytics",async(_,__)=>await RunAiAsync()));
            bar.Children.Add(MakeButton("Create Issues",async(_,__)=>await CreateIssuesAsync()));
            bar.Children.Add(MakeButton("Export",(_,__)=>Export()));
            DockPanel.SetDock(bar,Dock.Bottom);root.Children.Add(bar);root.Children.Add(_grid);Content=root;
            _grid.MouseDoubleClick+=(_,__)=>ShowSelected();
            RefreshPagingStatus();
        }

        private System.Threading.Tasks.Task<IReadOnlyList<ApsAssigneeResolution>> _assigneeLoad;
        private void AddAssigneeColumn()
        {
            var factory = new FrameworkElementFactory(typeof(ComboBox));
            factory.SetValue(ItemsControl.DisplayMemberPathProperty, "DisplayName");
            factory.SetValue(FrameworkElement.ToolTipProperty, "Select an Autodesk project member. Automatic uses the configured role.");
            factory.SetBinding(Selector.SelectedItemProperty, new Binding("ManualAssignee") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            var style = new Style(typeof(ComboBox));
            var existing = new DataTrigger { Binding = new Binding("HasApsIssue"), Value = true };
            existing.Setters.Add(new Setter(UIElement.IsEnabledProperty, false));
            style.Triggers.Add(existing);
            factory.SetValue(FrameworkElement.StyleProperty, style);
            factory.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(AssigneeLoaded));
            _grid.Columns.Add(new DataGridTemplateColumn { Header = "Assignee", Width = 240, CellTemplate = new DataTemplate { VisualTree = factory } });
        }

        private void AssigneeLoaded(object sender, RoutedEventArgs e)
        {
            var combo = (ComboBox)sender;
            var selected = combo.SelectedItem as ApsAssigneeResolution;
            combo.DropDownOpened -= AssigneeOpened;
            combo.DropDownOpened += AssigneeOpened;
            combo.ItemsSource = new[] { new ApsAssigneeResolution { DisplayName = "Automatic (by role)" }, selected }.Where(x => x != null).ToList();
            combo.SelectedItem = selected ?? combo.Items[0];
        }

        private async void AssigneeOpened(object sender, EventArgs e)
        {
            var combo = (ComboBox)sender;
            var row = combo.DataContext;
            var selected = combo.SelectedItem as ApsAssigneeResolution;
            try
            {
                if (_assigneeLoad == null) _assigneeLoad = PluginContext.Issues.GetProjectAssigneesAsync();
                var choices = (await _assigneeLoad).ToList();
                if (!ReferenceEquals(row, combo.DataContext)) return;
                choices.Insert(0, new ApsAssigneeResolution { DisplayName = "Automatic (by role)" });
                if (selected != null && selected.IsResolved && !choices.Any(x => x.AssignedTo == selected.AssignedTo && x.ProjectId == selected.ProjectId)) choices.Add(selected);
                combo.ItemsSource = choices;
                combo.SelectedItem = choices.FirstOrDefault(x => x.AssignedTo == selected?.AssignedTo && x.ProjectId == selected?.ProjectId) ?? choices[0];
            }
            catch (Exception ex) { _assigneeLoad = null; MessageBox.Show("Could not load project members. " + ex.Message, "BuildAI - Assignee", MessageBoxButton.OK, MessageBoxImage.Warning); }

        }
        private void AddIssueColumn()
        {
            _selectAllIssues.Click+=SelectAllIssuesClicked;
            var header=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center};
            header.Children.Add(_selectAllIssues);
            header.Children.Add(new TextBlock{Text="APS Issue",Margin=new Thickness(5,0,0,0),VerticalAlignment=VerticalAlignment.Center});
            var template=new DataTemplate();var panel=new FrameworkElementFactory(typeof(StackPanel));panel.SetValue(StackPanel.OrientationProperty,Orientation.Horizontal);
            var check=new FrameworkElementFactory(typeof(CheckBox));check.SetBinding(ToggleButton.IsCheckedProperty,new Binding("IsSelectedForIssueCreation"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});check.SetBinding(UIElement.VisibilityProperty,new Binding("HasApsIssue"){Converter=new InverseBoolVisibilityConverter()});check.SetValue(FrameworkElement.ToolTipProperty,"Select this result for APS Issue creation.");check.AddHandler(CheckBox.ClickEvent,new RoutedEventHandler(IssueSelectionChanged));panel.AppendChild(check);
            var link=new FrameworkElementFactory(typeof(Button));link.SetBinding(ContentControl.ContentProperty,new Binding("ApsIssueNumber"));link.SetValue(FrameworkElement.ToolTipProperty,"Open this Issue in Autodesk Construction Cloud.");link.SetValue(Button.PaddingProperty,new Thickness(5,1,5,1));link.SetBinding(UIElement.VisibilityProperty,new Binding("HasApsIssueUrl"){Converter=new BoolVisibilityConverter()});link.AddHandler(Button.ClickEvent,new RoutedEventHandler(OpenIssue));panel.AppendChild(link);
            template.VisualTree=panel;_grid.Columns.Add(new DataGridTemplateColumn{Header=header,CellTemplate=template,Width=115});
        }

        private void SelectAllIssuesClicked(object sender,RoutedEventArgs e)
        {
            if(_updatingSelectAll)return;
            var target=_selectAllIssues.IsChecked==true;
            foreach(var row in VisibleIssueRows().Where(x=>!x.HasApsIssue))row.IsSelectedForIssueCreation=target;
            SafeDataGridRefresh.Request(_grid, UpdateSelectAllState);
        }

        private void IssueSelectionChanged(object sender,RoutedEventArgs e){var check=sender as CheckBox;var row=check?.DataContext as ClashItem;if(row!=null&&!row.HasApsIssue)row.IsSelectedForIssueCreation=check.IsChecked==true;SafeDataGridRefresh.Request(_grid,UpdateSelectAllState);}
        private IEnumerable<ClashItem> VisibleIssueRows(){return (_grid.ItemsSource as IEnumerable)?.Cast<object>().OfType<ClashItem>()??Enumerable.Empty<ClashItem>();}
        private void Export()
        {
            var dialog=new SaveFileDialog{Filter=TableExport.DialogFilter,FileName="BuildAI_MEP_Comparation_Results.xlsx",AddExtension=true,OverwritePrompt=true};
            if(dialog.ShowDialog(this)!=true)return;
            try
            {
                var rows=VisibleIssueRows().ToList();var target=TableExport.Resolve(dialog.FileName,dialog.FilterIndex);
                var headers=new[]{"APS Issue","Assignee","State","Issue ID","Created UTC","Source A","Element A","Category A","Source B","Element B","Category B","Level","Volume, mm³","Confidence"};
                var values=rows.Select(r=>(IReadOnlyList<string>)new[]{r.HasApsIssue?r.ApsIssueNumber:"",r.ManualAssignee?.DisplayName??"Automatic (by role)",r.CreationStateText,r.ApsIssueId,r.ApsIssueCreatedAt?.ToString("u")??"",r.DisplaySourceA,r.ElementA,r.CategoryA,r.DisplaySourceB,r.ElementB,r.CategoryB,r.Level,r.IntersectionVolumeMm3.ToString("N3",CultureInfo.CurrentCulture),r.AiConfidence.ToString("P0",CultureInfo.CurrentCulture)}).ToList();
                TableExport.Write(target.Path,target.Format,headers,values);
                PluginLog.Info("ACC_TABLE_EXPORTED module=Clash format="+target.Format+" rows="+values.Count+" columns="+headers.Length);
                MessageBox.Show(this,"Report saved: "+target.Path,"BuildAI",MessageBoxButton.OK,MessageBoxImage.Information);
            }
            catch(Exception ex){PluginLog.Error("ACC_TABLE_EXPORT_FAILED module=Clash",ex);MessageBox.Show(this,"Report export failed.","BuildAI",MessageBoxButton.OK,MessageBoxImage.Error);}
        }
        private void UpdateSelectAllState(){var rows=VisibleIssueRows().Where(x=>!x.HasApsIssue).ToList();bool? state=rows.Count==0?false:rows.All(x=>x.IsSelectedForIssueCreation)?true:rows.All(x=>!x.IsSelectedForIssueCreation)?false:(bool?)null;_updatingSelectAll=true;_selectAllIssues.IsChecked=state;_selectAllIssues.IsEnabled=rows.Count>0;_updatingSelectAll=false;}

        private void OpenIssue(object sender,RoutedEventArgs e){var row=(sender as FrameworkElement)?.DataContext as ClashItem;if(row!=null)ClashIssueWorkflow.OpenIssue(row.ApsIssueUrl);}
        private void AddColumn(string header,string path,double width,string tooltip,string format=null){var binding=new Binding(path);if(!string.IsNullOrWhiteSpace(format))binding.StringFormat=format;_grid.Columns.Add(new DataGridTextColumn{Header=new TextBlock{Text=header,ToolTip=tooltip},Binding=binding,Width=width,IsReadOnly=true});}

        private async System.Threading.Tasks.Task RunAiAsync()
        {
            if(PluginContext.Report==null)return;IsEnabled=false;
            try{await PluginContext.Ai.AnalyzeAsync(PluginContext.Report.Items.Where(x=>x.CreationState!=IssueCreationState.PreviouslyCreatedNotDetected).ToList(),PluginContext.Settings);PluginContext.SaveReport();RefreshGrid();}
            catch(Exception ex){MessageBox.Show(ex.Message,"BuildAI — AI Analytics",MessageBoxButton.OK,MessageBoxImage.Error);}finally{IsEnabled=true;}
        }

        private async System.Threading.Tasks.Task CreateIssuesAsync()
        {
            if (PluginContext.Report == null || _issueCreationInProgress) return;

            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);

            var eligibleCount = PluginContext.Report.Items.Count(x => !x.HasApsIssue);
            var alreadyLinkedCount = PluginContext.Report.Items.Count(x => x.HasApsIssue);
            var selectedRows = VisibleIssueRows()
                .Where(x => x.IsSelectedForIssueCreation && !x.HasApsIssue)
                .GroupBy(x => string.IsNullOrWhiteSpace(x.ResultKey) ? x.Id : x.ResultKey, StringComparer.Ordinal)
                .Select(x => x.First()).ToList();
            if (selectedRows.Count == 0)
            {
                MessageBox.Show("No rows are selected for Issue creation. Select at least one result and try again.", "BuildAI — Create Issues", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show("Create " + selectedRows.Count + " APS Issue(s) from the currently visible filtered rows?", "BuildAI — Confirm Issue creation", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _issueCreationInProgress = true;
            var uid = CurrentModelUid();
            var issueLogPath = IssueCreationFileLog.BeginSession("Clash");
            var progress = new IssueProgressWindow(issueLogPath) { Owner = this };
            progress.Show();
            IsEnabled = false;
            try
            {
                progress.Update(new IssueCreationProgress
                {
                    Current = 0,
                    Total = selectedRows.Count,
                    Succeeded = 0,
                    Failed = 0,
                    Message = "Selection snapshot captured",
                    Error = "SELECTION SNAPSHOT\nTotal rows: " + PluginContext.Report.Items.Count +
                            "\nEligible rows: " + eligibleCount +
                            "\nSelected rows: " + selectedRows.Count +
                            "\nAlready linked: " + alreadyLinkedCount
                });

                var uiProgress = new Progress<IssueCreationProgress>(progress.Update);
                IProgress<string> prepLog = new Progress<string>(x => progress.Update(new IssueCreationProgress { Current = 0, Total = selectedRows.Count, Succeeded = 0, Failed = 0, Message = "Preparing publication views", Error = x }));
                var prepOperation = PluginContext.Handler.RequestPreparePublishViews(prepLog);
                var prep = await ExternalEventAwaiter.RaiseAndWaitAsync(
                    PluginContext.Event, prepOperation, ExternalEventAwaiter.DefaultStartTimeout, TimeSpan.FromMinutes(10));
                progress.Update(new IssueCreationProgress { Current = 0, Total = selectedRows.Count, Message = "Publication views prepared", Error = prep.Summary });

                var setup = MessageBox.Show(
                    "BuildAI prepared the Clash 3D view:\n\n"
                    + "    • BuildAI Coordination\n\n"
                    + "A view reaches Autodesk ONLY if it belongs to the publish set. Creating the view in Revit is not enough.\n\n"
                    + "Open Collaborate → Publish Settings, add BuildAI Coordination to the active set, and save.\n\n"
                    + "BuildAI AR-ST belongs to the AR-ST workflow and is not changed by Clash publication.\n\n"
                    + "BuildAI now verifies this against Autodesk BEFORE publishing, so a missing view is reported in seconds instead of after a full translation.",
                    "BuildAI — Publish Setup", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                if (setup != MessageBoxResult.OK) throw new OperationCanceledException("Issue creation was cancelled before model publication.");
                progress.Update(new IssueCreationProgress { Current = 0, Total = selectedRows.Count, Message = "Publish setup confirmed", Error = "Publish Settings dialog result: OK\nContinuing Issue creation.\nSelection snapshot retained: " + selectedRows.Count + " rows.\nSynchronizing the model before native APS publication." });

                if (prep.RequiresSynchronization)
                {
                    IProgress<string> syncLog = new Progress<string>(x => progress.Update(new IssueCreationProgress { Current = 0, Total = selectedRows.Count, Message = "Synchronizing model", Error = x }));
                    var syncOperation = PluginContext.Handler.RequestSynchronizeForPublication(syncLog);
                    await ExternalEventAwaiter.RaiseAndWaitAsync(
                        PluginContext.Event, syncOperation, ExternalEventAwaiter.DefaultStartTimeout, TimeSpan.FromMinutes(60));
                    progress.Update(new IssueCreationProgress { Current = 0, Total = selectedRows.Count, Message = "Synchronization completed", Error = "Safe mode: model synchronized and a new publication will be created.\nSelection snapshot retained: " + selectedRows.Count + " rows." });
                }
                else
                {
                    progress.Update(new IssueCreationProgress { Current = 0, Total = selectedRows.Count, Message = "Fast publication mode", Error = "No pre-existing model changes were detected and both BuildAI views already existed. Synchronize and publish are skipped; the latest published model will be reused." });
                }

                var cloudModel = new ApsCloudModelIdentity { HubId = prep.HubId, ProjectId = prep.ProjectId, ProjectGuid = prep.ProjectGuid, ModelGuid = prep.ModelGuid, DocumentTitle = prep.DocumentTitle, SharedOffsetX = prep.SharedOffsetX, SharedOffsetY = prep.SharedOffsetY, SharedOffsetZ = prep.SharedOffsetZ, SharedAngleRadians = prep.SharedAngleRadians, UseExistingPublication = !prep.RequiresSynchronization };
                var frozenModelUid = prep.HostModelUid;
                if (string.IsNullOrWhiteSpace(frozenModelUid))
                    throw new InvalidOperationException("The active Revit model UID could not be frozen during publication preparation.");
                if (!string.Equals(uid, frozenModelUid, StringComparison.Ordinal))
                    progress.Update(new IssueCreationProgress { Current = 0, Total = selectedRows.Count, Message = "Model identity refreshed", Error = "The UI model UID changed after selection. The UID frozen inside the Revit publication event will be used: " + frozenModelUid });
                await PluginContext.Issues.CreateSelectedAsync(PluginContext.Report, selectedRows, frozenModelUid, cloudModel, uiProgress);
                PluginContext.SaveReport();
                RefreshGrid();
                progress.MarkFinished();
            }
            catch (ApsPublishSetException ex)
            {
                // A configuration problem the user can fix in Revit, not an APS
                // fault. Present it as an instruction rather than a stack of
                // Autodesk diagnostics, and never offer the BuildAI settings link.
                PluginContext.SaveReport();
                progress.ShowFatalError(ex.Message);
                MessageBox.Show(ex.Message, "BuildAI — View missing from Publish Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                // Preserve APS ids written to rows even when the later BuildAI
                // batch reports PARTIAL. The durable outbox will retry only the
                // BuildAI synchronization on the next run.
                PluginContext.SaveReport();
                progress.ShowFatalError(ex.Message);
                var url = ex.Data["url"] as string;
                var msg = ex.Message + (string.IsNullOrWhiteSpace(url) ? "" : "\n\nOpen BuildAI to configure the APS project?");
                if (MessageBox.Show(msg, "BuildAI — Create Issues", string.IsNullOrWhiteSpace(url) ? MessageBoxButton.OK : MessageBoxButton.YesNo, MessageBoxImage.Error) == MessageBoxResult.Yes) ClashIssueWorkflow.OpenIssue(url);
            }
            finally { IsEnabled = true; _issueCreationInProgress = false; }
        }

        private void ShowSelected(){var item=_grid.SelectedItem as ClashItem;if(item==null)return;PluginContext.Handler.Selected=item;PluginContext.Handler.Action=ModelAction.Select;PluginContext.Event.Raise();}

        public async void RefreshData()
        {
            var report=PluginContext.Report;if(report==null){SetSummary("No calculation has been run.");return;}
            await PluginContext.Issues.LoadExistingAsync(report,CurrentModelUid());
            RefreshGrid();
        }

        private void RefreshGrid()
        {
            var report=PluginContext.Report;if(report==null){_grid.ItemsSource=null;UpdateSelectAllState();return;}RefreshFilterValues();ApplyFilter();
            var warnings=report.Warnings.Count==0?"":"\nWarnings: "+string.Join(" | ",report.Warnings);
            var currentCount=report.Items.Count(x=>x.CreationState!=IssueCreationState.PreviouslyCreatedNotDetected);
            var historicalCount=report.Items.Count-currentCount;
            SetSummary(($"Results: {currentCount}; historical Issues: {historicalCount}; candidates: {report.CandidatePairs}; exact checks: {report.ExactChecks}; boolean failures: {report.BooleanFailures}; time: {report.DurationMs/1000.0:N1} s.\n"+$"{report.SourceA} | {report.SourceB} | {report.SourceC}"+warnings).Replace("; ",";\n"));
            var recommended=report.Items.Count(x=>x.IsSelectedForIssueCreation&&!x.HasApsIssue);var offline=PluginContext.Issues!=null&&!PluginContext.Issues.IsBuildAiAvailable?"\n\nLocal Analysis mode: BuildAI is unavailable. Clash and AI remain available; APS Issue synchronization is unavailable.":"";_aiSummary.Text=(string.IsNullOrWhiteSpace(report.AiSummary)?$"AI Summary\nNot analyzed. Recommended for Issue creation: {recommended}":"AI Summary\n"+report.AiSummary+"\nRecommended for Issue creation: "+recommended)+offline;
        }

        private void SetSummary(string text){_summaryFull=text??string.Empty;_summaryExpanded=false;ShowSummary();}
        private void ShowSummary(){_summary.Text=SummaryLines.Visible(_summaryFull,_summaryExpanded);_summary.Visibility=_summary.Text.Length==0?Visibility.Collapsed:Visibility.Visible;_summaryToggle.Visibility=SummaryLines.HasMore(_summaryFull)?Visibility.Visible:Visibility.Collapsed;_summaryToggle.Content=_summaryExpanded?"Show less":"Show more";}

        private void ApplyFilter()
        {
            var report=PluginContext.Report;
            if(report==null){_grid.ItemsSource=null;UpdateSelectAllState();return;}
            IEnumerable<ClashItem> query=report.Items;
            switch(_stateFilter.SelectedItem as string)
            {
                case "New": query=query.Where(x=>x.CreationState==IssueCreationState.New); break;
                case "Already created": query=query.Where(x=>x.CreationState==IssueCreationState.AlreadyCreated || x.CreationState==IssueCreationState.CreatedThisRun || x.CreationState==IssueCreationState.PreviouslyCreatedNotDetected); break;
                case "Failed": query=query.Where(x=>x.CreationState==IssueCreationState.CreationFailed); break;
            }
            foreach(var exact in _columnFilters.ToList())
                query=query.Where(x=>string.Equals(NormalizeFilterValue(GetColumnValue(x,exact.Key)),exact.Value,StringComparison.OrdinalIgnoreCase));
            var search=(_search.Text??"").Trim();
            if(search.Length>0)query=query.Where(x=>Contains(x.DisplaySourceA,search)||Contains(x.DisplaySourceB,search)||Contains(x.CategoryA,search)||Contains(x.CategoryB,search)||Contains(x.ElementA,search)||Contains(x.ElementB,search)||Contains(x.ElementAId.ToString(CultureInfo.InvariantCulture),search)||Contains(x.ElementBId.ToString(CultureInfo.InvariantCulture),search)||Contains(x.ResultKey,search)||Contains(x.Level,search)||Contains(x.ApsIssueStatus,search)||Contains(x.AiComment,search));
            var visible=query.OrderBy(x=>x.CategoryA,StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.CategoryB,StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.Level,StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.Id,StringComparer.Ordinal).ToList();
            var view=new ListCollectionView(visible);
            if(_groupByCategories.IsChecked==true)view.GroupDescriptions.Add(new PropertyGroupDescription("CategoryPair"));
            _grid.ItemsSource=view;
            UpdateSelectAllState();
            _activeFilters.Text=(_columnFilters.Count==0?"No filters":string.Join(" AND ",_columnFilters.OrderBy(x=>x.Key).Select(x=>x.Key+" = "+x.Value)))+"; visible: "+visible.Count;
        }

        private void RefreshFilterValues()
        {
            if(_updatingFilterValues)return;_updatingFilterValues=true;
            try
            {
                var column=_columnFilter.SelectedItem as string;var previous=_valueFilter.SelectedItem as string;_valueFilter.Items.Clear();
                foreach(var value in (PluginContext.Report?.Items??new List<ClashItem>()).Select(x=>NormalizeFilterValue(GetColumnValue(x,column))).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))_valueFilter.Items.Add(value);
                if(previous!=null&&_valueFilter.Items.Cast<string>().Any(x=>string.Equals(x,previous,StringComparison.OrdinalIgnoreCase)))_valueFilter.SelectedItem=previous;else if(_valueFilter.Items.Count>0)_valueFilter.SelectedIndex=0;
            }
            finally{_updatingFilterValues=false;}
        }

        private void AddExactFilter(){var column=_columnFilter.SelectedItem as string;var value=_valueFilter.SelectedItem as string;if(string.IsNullOrWhiteSpace(column)||string.IsNullOrWhiteSpace(value))return;_columnFilters[column]=value;ApplyFilter();}
        private void RemoveSelectedFilter(){var column=_columnFilter.SelectedItem as string;if(string.IsNullOrWhiteSpace(column))return;_columnFilters.Remove(column);ApplyFilter();}
        private void ClearFilters(){_columnFilters.Clear();_stateFilter.SelectedIndex=0;ApplyFilter();}
        private void SelectVisibleGroup(){foreach(var row in PluginContext.Report?.Items??new List<ClashItem>())row.IsSelectedForIssueCreation=false;foreach(var row in VisibleIssueRows().Where(x=>!x.HasApsIssue))row.IsSelectedForIssueCreation=true;SafeDataGridRefresh.Request(_grid,UpdateSelectAllState);}
        private static string GetColumnValue(ClashItem row,string column){if(row==null)return "";switch(column){case "Source A":return row.DisplaySourceA;case "Source type A":return row.LinkInstanceAId.HasValue?"Revit Link":"Host";case "Element A":return row.ElementA;case "Element A ID":return row.ElementAId.ToString(CultureInfo.InvariantCulture);case "Category A":return row.CategoryA;case "Source B":return row.DisplaySourceB;case "Source type B":return row.LinkInstanceBId.HasValue?"Revit Link":"Host";case "Element B":return row.ElementB;case "Element B ID":return row.ElementBId.ToString(CultureInfo.InvariantCulture);case "Category B":return row.CategoryB;case "Level":return row.Level;case "Clash type":return row.Kind.ToString();case "Severity":return row.Severity.ToString();case "Issue status":return row.CreationStateText;case "AI result":return row.AiAssessment;case "AI severity":return row.AiSeverity;default:return "";}}
        private static string NormalizeFilterValue(string value)=>string.IsNullOrWhiteSpace(value)?BlankFilterValue:value.Trim();
        private static bool Contains(string value,string search)=>(value??"").IndexOf(search,StringComparison.OrdinalIgnoreCase)>=0;

        private static string CurrentModelUid(){try{return PluginContext.UiApplication?.ActiveUIDocument?.Document?.ProjectInformation?.UniqueId??PluginContext.Report?.DocumentTitle??"";}catch{return PluginContext.Report?.DocumentTitle??"";}}
        /// <summary>
        /// Describes how much of the candidate set has been checked.
        /// <para>
        /// Two things this must never do. It must not present the candidate count
        /// as a clash count - they differ by orders of magnitude and conflating
        /// them makes the tool look broken. And it must not hide that results are
        /// partial: anyone creating Issues from a partial set needs to know it is
        /// partial, or "BuildAI found 187 clashes" becomes a coordination claim
        /// that is not true.
        /// </para>
        /// </summary>
        private void RefreshPagingStatus()
        {
            var session = PluginContext.Session;
            var report = PluginContext.Report;

            if (session == null)
            {
                _pagingStatus.Text = report == null ? "" :
                    "Loaded from the saved report. Run clash detection again to continue checking.";
                _loadMore.Visibility = Visibility.Collapsed;
                _loadAll.Visibility = Visibility.Collapsed;
                return;
            }

            var cursor = session.Cursor;
            var results = report == null || report.Items == null ? 0 : report.Items.Count;

            if (cursor.IsExhausted)
            {
                _pagingStatus.Text = "All " + cursor.CandidateCount.ToString("N0") +
                    " candidate pairs checked. " + results.ToString("N0") + " clashes found.";
                _loadMore.Visibility = Visibility.Collapsed;
                _loadAll.Visibility = Visibility.Collapsed;
                return;
            }

            var percent = cursor.CandidateCount == 0 ? 0 :
                (int)Math.Round(100.0 * cursor.NextCandidateIndex / cursor.CandidateCount);
            var next = ClashPageBudget.ForCandidates(cursor.CandidateCount, false, cursor.MsPerExactCheck);

            _pagingStatus.Text =
                "PARTIAL RESULTS: " + results.ToString("N0") + " clashes from " +
                cursor.NextCandidateIndex.ToString("N0") + " of " + cursor.CandidateCount.ToString("N0") +
                " candidate pairs (" + percent + "%). " +
                cursor.RemainingCandidates.ToString("N0") + " pairs not yet checked.";
            _loadMore.Content = "Load " + next.MaxResults + " more";
            _loadMore.Visibility = Visibility.Visible;
            _loadAll.Visibility = Visibility.Visible;
        }

        private async System.Threading.Tasks.Task LoadMoreAsync(bool all)
        {
            var session = PluginContext.Session;
            if (session == null) return;

            var document = PluginContext.UiApplication?.ActiveUIDocument?.Document;
            var report = PluginContext.Report;
            if (document != null && report != null &&
                !session.MatchesModel(document, report.ElementsA, report.ElementsB, report.ElementsC))
            {
                // Continuing against a changed model checks pairs whose geometry no
                // longer matches what was indexed. Neither continuing nor restarting
                // is safe to decide silently, so the user decides.
                var answer = MessageBox.Show(
                    "The model has changed since the candidate list was built.\n\n" +
                    "Continuing will check pairs against the element set as it was when detection started, " +
                    "which may no longer match the model.\n\n" +
                    "Continue anyway? Choose No to close this and run clash detection again.",
                    "BuildAI \u2014 Model changed", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }

            _loadMore.IsEnabled = false;
            _loadAll.IsEnabled = false;
            var previousCursor = System.Windows.Input.Mouse.OverrideCursor;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                var budget = all
                    ? ClashPageBudget.Unlimited()
                    : ClashPageBudget.ForCandidates(session.Cursor.CandidateCount, false, session.Cursor.MsPerExactCheck);

                // Revit geometry and Boolean operations are not thread-safe, so this
                // stays on the calling thread. The await exists only so the dispatcher
                // can repaint before a long page starts.
                await System.Threading.Tasks.Task.Yield();
                var page = session.RunPage(budget);
                session.ReleaseGeometryCaches();
                ClashEngine.FinalizeReport(session.Report);
                PluginContext.Report = session.Report;
                PluginContext.SaveReport();

                RefreshFilterValues();
                ApplyFilter();
                RefreshPagingStatus();

                if (page.Items.Count == 0 && !session.Cursor.IsExhausted)
                    MessageBox.Show(
                        "No new clashes in this batch: " + page.ExactChecksPerformed.ToString("N0") +
                        " pairs checked, none intersecting.\n\n" +
                        session.Cursor.RemainingCandidates.ToString("N0") + " candidate pairs remain.",
                        "BuildAI", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "BuildAI \u2014 Load more", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                System.Windows.Input.Mouse.OverrideCursor = previousCursor;
                _loadMore.IsEnabled = true;
                _loadAll.IsEnabled = true;
            }
        }

        private static Button MakeButton(string text,RoutedEventHandler handler){var b=new Button{Content=text,Margin=new Thickness(8),Padding=new Thickness(12,6,12,6)};b.Click+=handler;return b;}
        private static Style HeaderStyle(){var style=new Style(typeof(DataGridColumnHeader));style.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(149,185,218))));style.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.Black));style.Setters.Add(new Setter(Control.FontWeightProperty,FontWeights.Bold));style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Center));style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(5,8,5,8)));return style;}
        private static Style BuildRowStyle(){var style=new Style(typeof(DataGridRow));var trigger=new DataTrigger{Binding=new Binding("AiAssessment"),Value="likely_noise"};trigger.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(225,225,225))));style.Triggers.Add(trigger);foreach(var pair in new[]{new[]{"critical","255,205,205"},new[]{"high","255,225,190"},new[]{"medium","255,248,190"},new[]{"low","215,240,215"}}){var t=new DataTrigger{Binding=new Binding("AiSeverity"),Value=pair[0]};var rgb=pair[1].Split(',').Select(byte.Parse).ToArray();t.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(rgb[0],rgb[1],rgb[2]))));style.Triggers.Add(t);}return style;}
        private static GroupStyle BuildCategoryGroupStyle(){var template=new DataTemplate();var text=new FrameworkElementFactory(typeof(TextBlock));text.SetValue(TextBlock.FontWeightProperty,FontWeights.Bold);text.SetValue(TextBlock.PaddingProperty,new Thickness(8,5,8,5));text.SetValue(TextBlock.BackgroundProperty,new SolidColorBrush(Color.FromRgb(225,235,245)));text.SetBinding(TextBlock.TextProperty,new Binding("."){Converter=new CategoryGroupHeaderConverter()});template.VisualTree=text;return new GroupStyle{HeaderTemplate=template};}
    }

    internal sealed class CategoryGroupHeaderConverter:IValueConverter
    {
        public object Convert(object value,Type targetType,object parameter,CultureInfo culture){var group=value as CollectionViewGroup;var rows=group?.Items.Cast<object>().OfType<ClashItem>().ToList()??new List<ClashItem>();return (group?.Name??"<blank>")+" — total: "+rows.Count+"; selected: "+rows.Count(x=>x.IsSelectedForIssueCreation&&!x.HasApsIssue)+"; Issues: "+rows.Count(x=>x.HasApsIssue);}
        public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture){throw new NotSupportedException();}
    }

    internal sealed class IssueProgressWindow:Window
    {
        private readonly ProgressBar _bar = new ProgressBar { Height=22, Minimum=0, Margin=new Thickness(10,10,10,4) };
        private readonly TextBlock _text = new TextBlock { Margin=new Thickness(10,4,10,4), TextWrapping=TextWrapping.Wrap };
        private readonly TextBox _errors = new TextBox { Margin=new Thickness(10,4,10,8), Height=180, IsReadOnly=true, TextWrapping=TextWrapping.Wrap, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, Visibility=Visibility.Collapsed };
        private readonly Button _close = new Button { Content="Close", Width=90, Margin=new Thickness(10), HorizontalAlignment=HorizontalAlignment.Right, Visibility=Visibility.Collapsed };
        private int _failed;
        private bool _finished;

        public IssueProgressWindow(string logPath)
        {
            Title="BuildAI — Creating APS Issues"; Width=620; Height=190; WindowStartupLocation=WindowStartupLocation.CenterOwner; ResizeMode=ResizeMode.CanResize;
            _close.Click += (_,__) => Close();
            var p=new StackPanel(); p.Children.Add(new TextBlock{Text="Log file: "+logPath,FontWeight=FontWeights.Bold,Margin=new Thickness(10,8,10,2),TextWrapping=TextWrapping.Wrap}); p.Children.Add(_text); p.Children.Add(_bar); p.Children.Add(_errors); p.Children.Add(_close); Content=p;
        }

        public void Update(IssueCreationProgress x)
        {
            IssueCreationFileLog.WriteProgress(x);
            _failed=x.Failed; _bar.Maximum=Math.Max(1,x.Total); _bar.Value=x.Current;
            _text.Text=$"{x.Current}/{x.Total} — {x.Message}\nCreated: {x.Succeeded}; failed: {x.Failed}";
            if(!string.IsNullOrWhiteSpace(x.Error))
            {
                _errors.Visibility=Visibility.Visible;
                if(_errors.Text.Length>0)_errors.AppendText("\r\n\r\n");
                _errors.AppendText(x.Error); _errors.ScrollToEnd(); Height=430;
            }
            if(x.IsCompleted)MarkFinished();
        }

        public void MarkFinished()
        {
            if(_finished)return; _finished=true;
            _close.Visibility=Visibility.Visible;
            if(_failed==0)
            {
                _text.Text += "\nCompleted successfully.";
                if(IsVisible) Close();
            }
            else
            {
                _text.Text += "\nCompleted with errors. Error details are shown below.";
                Activate();
            }
        }

        public void ShowFatalError(string message)
        {
            IssueCreationFileLog.WriteFatal(message);
            _errors.Visibility=Visibility.Visible; _errors.Text=message??"Unknown error."; _close.Visibility=Visibility.Visible; Height=430;
        }
    }
    internal sealed class BoolVisibilityConverter:IValueConverter{public object Convert(object v,Type t,object p,CultureInfo c)=>(v is bool b&&b)?Visibility.Visible:Visibility.Collapsed;public object ConvertBack(object v,Type t,object p,CultureInfo c)=>Binding.DoNothing;}
    internal sealed class InverseBoolVisibilityConverter:IValueConverter{public object Convert(object v,Type t,object p,CultureInfo c)=>(v is bool b&&b)?Visibility.Collapsed:Visibility.Visible;public object ConvertBack(object v,Type t,object p,CultureInfo c)=>Binding.DoNothing;}
}
