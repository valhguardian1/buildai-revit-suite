using BuildAI.Core.APS;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using BuildAI.Core;
using BuildAI.Core.Issues;
using BuildAI.Core.Logging;
using BuildAI.Core.Presentation;
using BuildAI.Core.Sorting;
using BuildAI.RevitCompatibility;
using Plugin4.LinkComparatorAI.Export;
using Plugin4.LinkComparatorAI.Issues;
using Plugin4.LinkComparatorAI.Models;
using Plugin4.LinkComparatorAI.Revit;
namespace Plugin4.LinkComparatorAI.UI
{
    public sealed class ResultsPane : Window
    {
        private static ResultsPane _current;
        private readonly Button _summaryToggle = new Button { Content="Show more", HorizontalAlignment=HorizontalAlignment.Left, Padding=new Thickness(8,2,8,2), Margin=new Thickness(0,0,0,8) };
        private string _summaryFull=string.Empty;
        private bool _summaryExpanded;
        private const string BlankFilterValue = "<blank>";
        private readonly DataGrid _grid=new DataGrid();private readonly TextBlock _summary=new TextBlock();private readonly TextBlock _aiResult=new TextBlock();private readonly TextBlock _status=new TextBlock();private readonly ComboBox _filter=new ComboBox();private readonly TextBox _search=new TextBox();private readonly ComboBox _columnFilter=new ComboBox();private readonly ComboBox _valueFilter=new ComboBox();private readonly TextBlock _activeFilters=new TextBlock();private readonly Dictionary<string,string> _columnFilters=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);private readonly CheckBox _selectAllIssues=new CheckBox{IsThreeState=false,VerticalAlignment=VerticalAlignment.Center,ToolTip="Select or clear all currently visible results that do not already have an APS Issue."};private readonly CheckBox _calculateChanges=new CheckBox{Content="Calculate changes",IsChecked=false,Margin=new Thickness(8),VerticalAlignment=VerticalAlignment.Center};private readonly ChangeCalculationModePolicy _changeModePolicy=new ChangeCalculationModePolicy(message=>PluginLog.Info(message));private ComparisonReport _report=new ComparisonReport();private bool _updatingSelectAll;private bool _updatingFilterValues;private int _populateVersion;
        private ResultsPane()
        {
            Title="BuildAI — AR-ST COMPARATOR RESULTS";Icon=Plugin4.LinkComparatorAI.Revit.RibbonIconLoader.LoadBrand();Width=1350;Height=850;MinWidth=900;MinHeight=600;WindowStartupLocation=WindowStartupLocation.CenterScreen;Closed+=(s,e)=>_current=null;
            var root=new Grid{Margin=new Thickness(16)};for(var i=0;i<8;i++)root.RowDefinitions.Add(new RowDefinition{Height=i==3?new GridLength(3,GridUnitType.Star):i==5?new GridLength(1,GridUnitType.Star):GridLength.Auto});
            var header=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,0,0,10)};header.Children.Add(BrandIcon());header.Children.Add(new TextBlock{Text="AR-ST COMPARATOR RESULTS",FontSize=17,FontWeight=FontWeights.Bold,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0)});root.Children.Add(header);
            _summary.TextWrapping=TextWrapping.Wrap;_summary.Margin=new Thickness(0,0,0,4);var summaryPanel=new StackPanel();summaryPanel.Children.Add(_summary);_summaryToggle.Click+=(s,e)=>{_summaryExpanded=!_summaryExpanded;ShowSummary();PluginLog.Info("ACC_SUMMARY_TOGGLED module=ARST expanded="+_summaryExpanded);};summaryPanel.Children.Add(_summaryToggle);Grid.SetRow(summaryPanel,1);root.Children.Add(summaryPanel);
            var filters=new WrapPanel{Margin=new Thickness(0,0,0,8)};filters.Children.Add(new TextBlock{Text="Severity:",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,6,0)});foreach(var x in new[]{"All","Critical","Important","Minor"})_filter.Items.Add(x);_filter.SelectedIndex=0;_filter.Width=115;_filter.SelectionChanged+=(s,e)=>ApplyFilter();filters.Children.Add(_filter);filters.Children.Add(new TextBlock{Text="Search:",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,6,0)});_search.Width=220;_search.TextChanged+=(s,e)=>ApplyFilter();filters.Children.Add(_search);filters.Children.Add(new TextBlock{Text="Group column:",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,6,0)});foreach(var x in new[]{"Level","Category","Check type","Severity","AI result","AI severity","Issue"})_columnFilter.Items.Add(x);_columnFilter.SelectedIndex=0;_columnFilter.Width=115;_columnFilter.SelectionChanged+=(s,e)=>RefreshGroupValues();filters.Children.Add(_columnFilter);filters.Children.Add(new TextBlock{Text="Value:",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,6,0)});_valueFilter.Width=180;filters.Children.Add(_valueFilter);filters.Children.Add(Btn("Add filter",(s,e)=>AddGroupFilter(),"Add or replace an exact-value filter for this column."));filters.Children.Add(Btn("Clear filters",(s,e)=>ClearGroupFilters(),"Clear all exact-value group filters."));_activeFilters.VerticalAlignment=VerticalAlignment.Center;_activeFilters.Margin=new Thickness(12,3,0,0);_activeFilters.FontWeight=FontWeights.Bold;filters.Children.Add(_activeFilters);Grid.SetRow(filters,2);root.Children.Add(filters);
            _grid.AutoGenerateColumns=false;_grid.IsReadOnly=false;_grid.CanUserAddRows=false;_grid.SelectionMode=DataGridSelectionMode.Single;_grid.GridLinesVisibility=DataGridGridLinesVisibility.All;_grid.ColumnHeaderStyle=HeaderStyle();_grid.RowStyle=BuildRowStyle();_grid.MouseDoubleClick+=(s,e)=>Highlight();
            AddIssueColumn();AddAssigneeColumn();AddCol("Check type","CheckTypeText",110,"Comparison type.");AddCol("Level","Level",95,"Associated level.");AddCol("Category","Category",110,"Category.");AddCol("Issue","Title",190,"Issue summary.");AddCol("Description","Description",300,"Deterministic finding.");AddCol("Δ, mm","DeltaMm",90,"Measured difference.");Grid.SetRow(_grid,3);root.Children.Add(_grid);
            var aiHeader=new TextBlock{Text="AI Summary",FontWeight=FontWeights.Bold,Margin=new Thickness(0,8,0,4)};Grid.SetRow(aiHeader,4);root.Children.Add(aiHeader);var aiScroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,BorderThickness=new Thickness(1),BorderBrush=Brushes.Gray,Padding=new Thickness(6)};_aiResult.TextWrapping=TextWrapping.Wrap;aiScroll.Content=_aiResult;Grid.SetRow(aiScroll,5);root.Children.Add(aiScroll);
            var buttons=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0)};_calculateChanges.Click+=(s,e)=>_changeModePolicy.SetByUser(_calculateChanges.IsChecked==true);buttons.Children.Add(_calculateChanges);buttons.Children.Add(Btn("Show element",(s,e)=>Highlight(),"Focus selected result."));buttons.Children.Add(Btn("Clear selection",(s,e)=>Reset(),"Clear Revit selection."));buttons.Children.Add(Btn("Select visible group",(s,e)=>SelectVisibleGroup(),"Clear previous Issue selections and select only the currently visible filtered rows."));buttons.Children.Add(Btn("AI Analytics",async(s,e)=>await Analyze(),"Run expert AI filtering."));buttons.Children.Add(Btn("Create Issues",async(s,e)=>await CreateIssues(),"Create APS Issues only for checked rows in the currently visible filtered group."));buttons.Children.Add(Btn("Export",(s,e)=>Export(),"Export visible rows to XLSX or CSV."));Grid.SetRow(buttons,6);root.Children.Add(buttons);_status.Margin=new Thickness(0,6,0,0);_status.Text="Ready";Grid.SetRow(_status,7);root.Children.Add(_status);Content=root;
        }
        public static void ShowPane(Autodesk.Revit.UI.UIApplication app,ComparisonReport report){if(_current==null)_current=new ResultsPane();_current.Populate(report);if(!_current.IsVisible)_current.Show();_current.Activate();}
        public static void SetGlobalStatus(string text){if(_current!=null)_current._status.Text=text??"";}
        public static void BeginComparisonRefresh(string status){if(_current==null)return;_current._changeModePolicy.ResetOperation();_current._calculateChanges.IsChecked=false;_current._populateVersion++;_current._report=new ComparisonReport();_current._grid.ItemsSource=null;_current.SetSummary(status??"Refreshing AR and ST links...");_current._aiResult.Text="Waiting for the current AR-ST calculation.";_current._status.Text=status??"Refreshing AR and ST links...";}
        private async void Populate(ComparisonReport report){var version=++_populateVersion;var current=report??new ComparisonReport();_report=current;SetSummary($"AR: {current.ArchitecturalModel}\nST: {current.StructuralModel}"+(current.Diagnostics.Count>0?"\n\nDiagnostics:\n"+string.Join("\n",current.Diagnostics):"")+(current.Warnings.Count>0?"\n\nStatus:\n"+string.Join("\n",current.Warnings):""));RefreshGroupValues();ApplyFilter();_status.Text="Checking existing APS Issues...";await PluginContext.Issues.LoadExistingAsync(current,CurrentModelUid());if(version!=_populateVersion)return;if(!PluginContext.Issues.IsBuildAiAvailable)SetGlobalStatus("Local Analysis mode: BuildAI unavailable. AR-ST and AI remain available; APS Issues are unavailable.");else SetGlobalStatus("AR-ST comparison completed using refreshed links.");RefreshGroupValues();ApplyFilter();}
        private void SetSummary(string text){_summaryFull=text??string.Empty;_summaryExpanded=false;ShowSummary();}
        private void ShowSummary(){_summary.Text=SummaryLines.Visible(_summaryFull,_summaryExpanded);_summary.Visibility=_summary.Text.Length==0?Visibility.Collapsed:Visibility.Visible;_summaryToggle.Visibility=SummaryLines.HasMore(_summaryFull)?Visibility.Visible:Visibility.Collapsed;_summaryToggle.Content=_summaryExpanded?"Show less":"Show more";}
        private void ApplyFilter(){IEnumerable<ComparisonIssue>q=_report.Issues;var i=_filter.SelectedIndex;if(i==1)q=q.Where(x=>x.Severity==IssueSeverity.Critical);if(i==2)q=q.Where(x=>x.Severity==IssueSeverity.Important);if(i==3)q=q.Where(x=>x.Severity==IssueSeverity.Minor);foreach(var exact in _columnFilters.ToList()){var column=exact.Key;var value=exact.Value;q=q.Where(x=>string.Equals(NormalizeFilterValue(GetColumnValue(x,column)),value,StringComparison.OrdinalIgnoreCase));}var s=(_search.Text??"").Trim();if(s.Length>0)q=q.Where(x=>Contains(x.Level,s)||Contains(x.Category,s)||Contains(x.Title,s)||Contains(x.Description,s)||Contains(x.CheckTypeText,s)||Contains(x.AiComment,s));var visible=q.OrderBy(x=>x.CategorySortOrder).ThenBy(x=>x.ResultTypeSortOrder).ThenBy(x=>x.NaturalSortTitle,NaturalStringComparer.OrdinalIgnoreCase).ThenBy(x=>x.Z).ThenBy(x=>x.ArchitecturalElementId).ThenBy(x=>x.StructuralElementId).ToList();_grid.ItemsSource=visible;UpdateSelectAllState();UpdateActiveFilterText();var recommended=visible.Count(x=>x.IsSelectedForIssueCreation&&!x.HasApsIssue);_aiResult.Text=(string.IsNullOrWhiteSpace(_report.AiSummary)?"Not analyzed.":_report.AiSummary)+"\n\nVisible group: "+visible.Count+" rows. Selected visible rows for Issue creation: "+recommended;}

        private void RefreshGroupValues(){if(_updatingFilterValues)return;_updatingFilterValues=true;try{var column=_columnFilter.SelectedItem as string;var previous=_valueFilter.SelectedItem as string;_valueFilter.Items.Clear();foreach(var value in _report.Issues.Select(x=>NormalizeFilterValue(GetColumnValue(x,column))).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,NaturalStringComparer.OrdinalIgnoreCase))_valueFilter.Items.Add(value);if(previous!=null&&_valueFilter.Items.Cast<string>().Any(x=>string.Equals(x,previous,StringComparison.OrdinalIgnoreCase)))_valueFilter.SelectedItem=previous;else if(_valueFilter.Items.Count>0)_valueFilter.SelectedIndex=0;}finally{_updatingFilterValues=false;}}
        private void AddGroupFilter(){var column=_columnFilter.SelectedItem as string;var value=_valueFilter.SelectedItem as string;if(string.IsNullOrWhiteSpace(column)||string.IsNullOrWhiteSpace(value))return;_columnFilters[column]=value;ApplyFilter();SetGlobalStatus("Group filter applied: "+column+" = "+value+".");}
        private void ClearGroupFilters(){_columnFilters.Clear();ApplyFilter();SetGlobalStatus("All group filters cleared.");}
        private void UpdateActiveFilterText(){_activeFilters.Text=_columnFilters.Count==0?"No group filter":string.Join(" AND ",_columnFilters.OrderBy(x=>x.Key).Select(x=>x.Key+" = "+x.Value));}
        private static string GetColumnValue(ComparisonIssue row,string column){if(row==null)return "";switch(column){case "Level":return row.Level;case "Category":return row.Category;case "Check type":return row.CheckTypeText;case "Severity":return row.SeverityText;case "AI result":return row.AiAssessment;case "AI severity":return row.AiSeverity;case "Issue":return row.Title;default:return "";}}
        private static string NormalizeFilterValue(string value){return string.IsNullOrWhiteSpace(value)?BlankFilterValue:value.Trim();}
        private void SelectVisibleGroup(){var visible=VisibleIssueRows().ToList();foreach(var row in _report.Issues)row.IsSelectedForIssueCreation=false;foreach(var row in visible.Where(x=>!x.HasApsIssue))row.IsSelectedForIssueCreation=true;ApplyFilter();UpdateSelectAllState();var recommended=visible.Count(x=>x.IsSelectedForIssueCreation&&!x.HasApsIssue);_aiResult.Text=(string.IsNullOrWhiteSpace(_report.AiSummary)?"Not analyzed.":_report.AiSummary)+"\n\nVisible group: "+visible.Count+" rows. Selected visible rows for Issue creation: "+recommended;SetGlobalStatus("Only the currently visible group is selected for Issue creation.");}
        private async System.Threading.Tasks.Task Analyze(){try{IsEnabled=false;SetGlobalStatus("AI Analytics is running...");await PluginContext.Ai.AnalyzeAsync(_report,ComparatorSettings.Load());RefreshGroupValues();ApplyFilter();SetGlobalStatus("AI Analytics completed.");}catch(Exception ex){MessageBox.Show(ex.Message,"BuildAI",MessageBoxButton.OK,MessageBoxImage.Error);SetGlobalStatus(ex.Message);}finally{IsEnabled=true;}}
        private async System.Threading.Tasks.Task CreateIssues()
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);

            // ResultKey identifies one logical finding.  A recalculation can currently
            // return duplicate row objects for the same finding, so retain each key
            // only once and restore a single checkbox per key.
            // Active filters are an execution boundary: checked rows that are
            // currently hidden must not leak into this Issue batch.
            var manualAssignees = _report.Issues.Where(x => !string.IsNullOrWhiteSpace(x.ResultKey)).GroupBy(x => x.ResultKey, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First().ManualAssignee, StringComparer.Ordinal);
            var selectedKeys = VisibleIssueRows()
                .Where(x => x.IsSelectedForIssueCreation && !x.HasApsIssue)
                .Select(x => x.ResultKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (selectedKeys.Count == 0)
            {
                MessageBox.Show("No rows are selected for Issue creation. Select at least one result and try again.", "BuildAI", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var calculateChanges = _changeModePolicy.ConsumeForOperation();
            _calculateChanges.IsChecked = false;
            var issueLogPath = IssueCreationFileLog.BeginSession("AR-ST");
            IssueCreationFileLog.Write(
                BuildInfo.LoadedAssemblyIdentity(typeof(ResultsPane).Assembly,
                    PluginContext.UiApplication?.Application?.VersionNumber) + Environment.NewLine +
                "CHANGE CALCULATION MODE | explicitlyEnabled=" + calculateChanges.ToString().ToLowerInvariant() + Environment.NewLine +
                "enabled=" + calculateChanges.ToString().ToLowerInvariant() + Environment.NewLine +
                "Selected logical findings: " + selectedKeys.Count + Environment.NewLine +
                "Active group filters: " + (_columnFilters.Count==0?"none":string.Join(" AND ",_columnFilters.OrderBy(x=>x.Key).Select(x=>x.Key+" = "+x.Value))));

            if (calculateChanges)
            {
                var reportBeforeRefresh = _report;
                ComparisonReport refreshed;
                try
                {
                    SetGlobalStatus("Refreshing AR-ST results from the current model...");
                    BeginComparisonRefresh("Refreshing AR and ST links...");
                    var recalculateLog = new Progress<string>(x => IssueCreationFileLog.Write("RECALCULATE | " + x));
                    var refreshTask = PluginContext.ActionHandler.RequestRecalculate(recalculateLog);
                    PluginContext.ActionEvent.Raise();
                    refreshed = await refreshTask;
                    IssueCreationFileLog.Write("RECALCULATE COMPLETED | Results: " + (refreshed?.Issues?.Count ?? 0));
                }
                catch (Exception ex)
                {
                    IssueCreationFileLog.WriteFatal("RECALCULATE FAILED" + Environment.NewLine + ex);
                    _report = reportBeforeRefresh ?? new ComparisonReport();
                    RefreshGroupValues();
                    ApplyFilter();
                    SetGlobalStatus("AR-ST recalculation failed. Diagnostic log: " + issueLogPath);
                    MessageBox.Show(ex.Message + "\n\nDiagnostic log:\n" + issueLogPath, "BuildAI — Recalculate", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                _report = refreshed ?? new ComparisonReport();
                foreach (var row in _report.Issues) { row.IsSelectedForIssueCreation = false; if (manualAssignees.TryGetValue(row.ResultKey ?? "", out var manual)) row.ManualAssignee = manual; }
                foreach (var selectedKey in selectedKeys)
                {
                    var row = _report.Issues.FirstOrDefault(x =>
                        !x.HasApsIssue && string.Equals(x.ResultKey, selectedKey, StringComparison.Ordinal));
                    if (row != null) row.IsSelectedForIssueCreation = true;
                }
            }
            else IssueCreationFileLog.Write("RECALCULATE SKIPPED | Mode was not explicitly enabled by the user for this operation.");
            await PluginContext.Issues.LoadExistingAsync(_report, CurrentModelUid());
            ApplyFilter();
            var eligibleCount = _report.Issues.Count(x => !x.HasApsIssue);
            var alreadyLinkedCount = _report.Issues.Count(x => x.HasApsIssue);
            var selectedRows = VisibleIssueRows()
                .Where(x => x.IsSelectedForIssueCreation && !x.HasApsIssue)
                .GroupBy(x => x.ResultKey ?? "", StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();
            if (selectedRows.Count == 0)
            {
                MessageBox.Show("No rows are selected for Issue creation. Select at least one result and try again.", "BuildAI", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var progress = new IssueProgressWindow(issueLogPath) { Owner = this };
            try
            {
                IsEnabled = false;
                progress.Show();
                progress.Update(new IssueCreationProgress
                {
                    Current = 0,
                    Total = selectedRows.Count,
                    Succeeded = 0,
                    Failed = 0,
                    Message = "Selection snapshot captured",
                    Error = "SELECTION SNAPSHOT\nTotal rows: " + _report.Issues.Count +
                            "\nEligible rows: " + eligibleCount +
                            "\nSelected visible rows: " + selectedRows.Count +
                            "\nActive group filters: " + (_columnFilters.Count==0?"none":string.Join(" AND ",_columnFilters.OrderBy(x=>x.Key).Select(x=>x.Key+" = "+x.Value))) +
                            "\nAlready linked: " + alreadyLinkedCount
                });

                var uiProgress = new Progress<IssueCreationProgress>(progress.Update);
                IProgress<string> prepLog = new Progress<string>(x => progress.Update(new IssueCreationProgress { Current = 0, Total = selectedRows.Count, Succeeded = 0, Failed = 0, Message = "Preparing publication views", Error = x }));
                var prepOperation = PluginContext.ActionHandler.RequestPreparePublishViews(prepLog);
                var prep = await ExternalEventAwaiter.RaiseAndWaitAsync(
                    PluginContext.ActionEvent, prepOperation, ExternalEventAwaiter.DefaultStartTimeout, TimeSpan.FromMinutes(10));
                progress.Update(new IssueCreationProgress { Current = 0, Total = selectedRows.Count, Message = "Publication views prepared", Error = prep.Summary });

                var setup = MessageBox.Show(
                    "BuildAI prepared two 3D views:\n\n"
                    + "    • BuildAI Coordination\n"
                    + "    • BuildAI AR-ST\n\n"
                    + "A view reaches Autodesk ONLY if it belongs to the publish set. Creating the view in Revit is not enough.\n\n"
                    + "Open Collaborate → Publish Settings, add BOTH views to the active set, and save.\n\n"
                    + "BuildAI now verifies this against Autodesk BEFORE publishing, so a missing view is reported in seconds instead of after a full translation.",
                    "BuildAI — Publish Setup", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                if (setup != MessageBoxResult.OK) throw new OperationCanceledException("Issue creation was cancelled before model publication.");
                progress.Update(new IssueCreationProgress { Current = 0, Total = selectedRows.Count, Message = "Publish setup confirmed", Error = "Publish Settings dialog result: OK\nContinuing Issue creation.\nSelection snapshot retained: " + selectedRows.Count + " rows.\nSynchronizing the model before native APS publication." });

                if (prep.RequiresSynchronization)
                {
                    IProgress<string> syncLog = new Progress<string>(x => progress.Update(new IssueCreationProgress { Current = 0, Total = selectedRows.Count, Message = "Synchronizing model", Error = x }));
                    var syncOperation = PluginContext.ActionHandler.RequestSynchronizeForPublication(syncLog);
                    await ExternalEventAwaiter.RaiseAndWaitAsync(
                        PluginContext.ActionEvent, syncOperation, ExternalEventAwaiter.DefaultStartTimeout, TimeSpan.FromMinutes(60));
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
                await PluginContext.Issues.CreateSelectedAsync(_report, selectedRows, frozenModelUid, cloudModel, uiProgress);
                ApplyFilter();
                SetGlobalStatus("APS Issue creation completed.");
                progress.MarkFinished();
            }
            catch (ApsPublishSetException ex)
            {
                // A configuration problem the user can fix in Revit, not an APS
                // fault. Present it as an instruction rather than a stack of
                // Autodesk diagnostics, and never offer the BuildAI settings link.
                progress.ShowFatalError(ex.Message);
                MessageBox.Show(ex.Message, "BuildAI — View missing from Publish Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                progress.ShowFatalError(ex.Message);
                var url = ex.Data["url"] as string;
                var result = MessageBox.Show(ex.Message + (string.IsNullOrWhiteSpace(url) ? "" : "\n\nOpen BuildAI to configure the APS project?"), "BuildAI", string.IsNullOrWhiteSpace(url) ? MessageBoxButton.OK : MessageBoxButton.YesNo, MessageBoxImage.Error);
                if (result == MessageBoxResult.Yes) ComparatorIssueWorkflow.OpenIssue(url);
            }
            finally { IsEnabled = true; }
        }

        private void Highlight()
        {
            var selected = _grid.SelectedItem as ComparisonIssue;
            if (selected == null) return;
            PluginContext.ActionHandler.RequestHighlight(selected);
            PluginContext.ActionEvent.Raise();
        }

        private void Reset()
        {
            // Clear both the table selection and the visual focus created in Revit.
            _grid.UnselectAll();
            _grid.SelectedItem = null;
            PluginContext.ActionHandler.RequestReset();
            PluginContext.ActionEvent.Raise();
            SetGlobalStatus("Selection and focused view cleared.");
        }

        private void Export()
        {
            var dialog = new SaveFileDialog
            {
                Filter = TableExport.DialogFilter,
                FileName = "BuildAI_AR-ST_COMPARATOR_RESULTS.xlsx",
                AddExtension=true,OverwritePrompt=true
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var visibleRows = (_grid.ItemsSource as IEnumerable<ComparisonIssue> ?? _report.Issues).ToList();
                    var target=TableExport.Resolve(dialog.FileName,dialog.FilterIndex);
                    var headers=new[]{"APS Issue","Assignee","Check type","Level","Category","Issue","Description","Δ, mm"};
                    var values=visibleRows.Select(r=>(IReadOnlyList<string>)new[]{r.HasApsIssue?r.ApsIssueNumber:"",r.ManualAssignee?.DisplayName??"Automatic (by role)",r.CheckTypeText,r.Level,r.Category,r.Title,r.Description,r.DeltaMm.ToString(CultureInfo.CurrentCulture)}).ToList();
                    TableExport.Write(target.Path,target.Format,headers,values);
                    PluginLog.Info("ACC_TABLE_EXPORTED module=ARST format="+target.Format+" rows="+values.Count+" columns="+headers.Length);
                    SetGlobalStatus("Report saved: " + target.Path);
                    MessageBox.Show(this,"Report saved: "+target.Path,"BuildAI",MessageBoxButton.OK,MessageBoxImage.Information);
                }
                catch(Exception ex){PluginLog.Error("ACC_TABLE_EXPORT_FAILED module=ARST",ex);MessageBox.Show(this,"Report export failed.","BuildAI",MessageBoxButton.OK,MessageBoxImage.Error);}
            }
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
        private void AddIssueColumn(){_selectAllIssues.Click+=SelectAllIssuesClicked;var header=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center};header.Children.Add(_selectAllIssues);header.Children.Add(new TextBlock{Text="APS Issue",Margin=new Thickness(5,0,0,0),VerticalAlignment=VerticalAlignment.Center});var template=new DataTemplate();var panel=new FrameworkElementFactory(typeof(StackPanel));panel.SetValue(StackPanel.OrientationProperty,Orientation.Horizontal);var check=new FrameworkElementFactory(typeof(CheckBox));check.SetBinding(ToggleButton.IsCheckedProperty,new Binding("IsSelectedForIssueCreation"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});check.SetBinding(UIElement.VisibilityProperty,new Binding("HasApsIssue"){Converter=new InverseBoolVisibilityConverter()});check.AddHandler(CheckBox.ClickEvent,new RoutedEventHandler(IssueSelectionChanged));panel.AppendChild(check);var link=new FrameworkElementFactory(typeof(Button));link.SetBinding(ContentControl.ContentProperty,new Binding("ApsIssueNumber"));link.SetValue(FrameworkElement.ToolTipProperty,"Open this Issue in Autodesk Construction Cloud.");link.SetBinding(UIElement.VisibilityProperty,new Binding("HasApsIssue"){Converter=new BoolVisibilityConverter()});link.AddHandler(Button.ClickEvent,new RoutedEventHandler(OpenIssue));panel.AppendChild(link);template.VisualTree=panel;_grid.Columns.Add(new DataGridTemplateColumn{Header=header,CellTemplate=template,Width=115});}
        private void SelectAllIssuesClicked(object sender,RoutedEventArgs e){if(_updatingSelectAll)return;var target=_selectAllIssues.IsChecked==true;foreach(var row in VisibleIssueRows().Where(x=>!x.HasApsIssue))row.IsSelectedForIssueCreation=target;_grid.Items.Refresh();UpdateSelectAllState();}
        private void IssueSelectionChanged(object sender,RoutedEventArgs e){var check=sender as CheckBox;var row=check?.DataContext as ComparisonIssue;if(row!=null&&!row.HasApsIssue)row.IsSelectedForIssueCreation=check.IsChecked==true;UpdateSelectAllState();}
        private IEnumerable<ComparisonIssue> VisibleIssueRows(){return (_grid.ItemsSource as IEnumerable<ComparisonIssue>)??Enumerable.Empty<ComparisonIssue>();}
        private void UpdateSelectAllState(){var rows=VisibleIssueRows().Where(x=>!x.HasApsIssue).ToList();bool? state=rows.Count==0?false:rows.All(x=>x.IsSelectedForIssueCreation)?true:rows.All(x=>!x.IsSelectedForIssueCreation)?false:(bool?)null;_updatingSelectAll=true;_selectAllIssues.IsChecked=state;_selectAllIssues.IsEnabled=rows.Count>0;_updatingSelectAll=false;}
        private void OpenIssue(object sender,RoutedEventArgs e){var row=(sender as FrameworkElement)?.DataContext as ComparisonIssue;if(row!=null)ComparatorIssueWorkflow.OpenIssue(row.ApsIssueUrl);}
        private void AddCol(string h,string p,double w,string tip){_grid.Columns.Add(new DataGridTextColumn{Header=new TextBlock{Text=h,ToolTip=tip},Binding=new Binding(p),Width=w,IsReadOnly=true});}
        private static Style HeaderStyle(){var st=new Style(typeof(DataGridColumnHeader));st.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(149,185,218))));st.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.Black));st.Setters.Add(new Setter(Control.FontWeightProperty,FontWeights.Bold));st.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Center));st.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(5,8,5,8)));return st;}
        private static Style BuildRowStyle(){var style=new Style(typeof(DataGridRow));var noise=new DataTrigger{Binding=new Binding("AiAssessment"),Value="likely_noise"};noise.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(225,225,225))));style.Triggers.Add(noise);foreach(var pair in new[]{new[]{"critical","255,205,205"},new[]{"high","255,225,190"},new[]{"medium","255,248,190"},new[]{"low","215,240,215"}}){var t=new DataTrigger{Binding=new Binding("AiSeverity"),Value=pair[0]};var rgb=pair[1].Split(',').Select(byte.Parse).ToArray();t.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(rgb[0],rgb[1],rgb[2]))));style.Triggers.Add(t);}return style;}
        private static Image BrandIcon()=>new Image{Source=Plugin4.LinkComparatorAI.Revit.RibbonIconLoader.LoadBrand(),Width=28,Height=28};private static bool Contains(string a,string b)=>(a??"").IndexOf(b,StringComparison.OrdinalIgnoreCase)>=0;private static Button Btn(string text,RoutedEventHandler h,string tip){var b=new Button{Content=text,ToolTip=tip,Padding=new Thickness(10,5,10,5),Margin=new Thickness(6,0,0,0)};b.Click+=h;return b;}
        private static string CurrentModelUid(){try{return PluginContext.UiApplication?.ActiveUIDocument?.Document?.ProjectInformation?.UniqueId??_current?._report?.ArchitecturalModel??"";}catch{return _current?._report?.ArchitecturalModel??"";}}
    }
    internal sealed class IssueProgressWindow:Window
    {
        private readonly ProgressBar _bar=new ProgressBar{Height=22,Minimum=0,Margin=new Thickness(10,10,10,4)};
        private readonly TextBlock _text=new TextBlock{Margin=new Thickness(10,4,10,4),TextWrapping=TextWrapping.Wrap};
        private readonly TextBox _errors=new TextBox{Margin=new Thickness(10,4,10,8),Height=180,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Visibility=Visibility.Collapsed};
        private readonly Button _close=new Button{Content="Close",Width=90,Margin=new Thickness(10),HorizontalAlignment=HorizontalAlignment.Right,Visibility=Visibility.Collapsed};
        private int _failed;
        private bool _finished;
        public IssueProgressWindow(string logPath){Title="BuildAI — Creating APS Issues — v"+BuildInfo.Version;Width=720;Height=230;WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.CanResize;_close.Click+=(_,__)=>Close();var p=new StackPanel();p.Children.Add(new TextBlock{Text="Log file: "+logPath,FontWeight=FontWeights.Bold,Margin=new Thickness(10,8,10,2),TextWrapping=TextWrapping.Wrap});p.Children.Add(new TextBlock{Text=BuildInfo.Banner,FontWeight=FontWeights.Bold,Margin=new Thickness(10,8,10,2),TextWrapping=TextWrapping.Wrap});p.Children.Add(_text);p.Children.Add(_bar);p.Children.Add(_errors);p.Children.Add(_close);Content=p;}
        public void Update(IssueCreationProgress x){IssueCreationFileLog.WriteProgress(x);_failed=x.Failed;_bar.Maximum=Math.Max(1,x.Total);_bar.Value=x.Current;_text.Text=$"{x.Current}/{x.Total} — {x.Message}\nCreated: {x.Succeeded}; failed: {x.Failed}";if(!string.IsNullOrWhiteSpace(x.Error)){_errors.Visibility=Visibility.Visible;if(_errors.Text.Length>0)_errors.AppendText("\r\n\r\n");_errors.AppendText(x.Error);_errors.ScrollToEnd();Height=430;}if(x.IsCompleted)MarkFinished();}
        public void MarkFinished(){if(_finished)return;_finished=true;_close.Visibility=Visibility.Visible;if(_failed==0){_text.Text+="\nCompleted successfully.";if(IsVisible)Close();}else{_text.Text+="\nCompleted with errors. Error details are shown below.";Activate();}}
        public void ShowFatalError(string message){IssueCreationFileLog.WriteFatal(message);_errors.Visibility=Visibility.Visible;_errors.Text=message??"Unknown error.";_close.Visibility=Visibility.Visible;Height=430;}
    }
    internal sealed class BoolVisibilityConverter:IValueConverter{public object Convert(object v,Type t,object p,CultureInfo c)=>(v is bool b&&b)?Visibility.Visible:Visibility.Collapsed;public object ConvertBack(object v,Type t,object p,CultureInfo c)=>Binding.DoNothing;}internal sealed class InverseBoolVisibilityConverter:IValueConverter{public object Convert(object v,Type t,object p,CultureInfo c)=>(v is bool b&&b)?Visibility.Collapsed:Visibility.Visible;public object ConvertBack(object v,Type t,object p,CultureInfo c)=>Binding.DoNothing;}
}
