using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Data;
using Autodesk.Revit.DB;
using BuildAI.AccIssueReturn.Core;
using Microsoft.Win32;

namespace BuildAI.AccIssueReturn.Revit;

public partial class ImportWindow : Window
{
    private readonly Autodesk.Revit.UI.UIDocument uidoc; private readonly Document hostDocument; private readonly RevitActionQueue actions; private readonly string hostModelUid; private readonly IAccIssueClient client; private readonly IAccAuthProvider auth; private readonly IBuildAiRevitIssuesClient buildAiClient; private readonly ImportService service; private CancellationTokenSource cts=new();
    private IReadOnlyList<AccHub> hubs=Array.Empty<AccHub>(); private IReadOnlyList<AccProject> projects=Array.Empty<AccProject>(); private IReadOnlyList<AccModel> models=Array.Empty<AccModel>(); private List<IssueRow> allRows=new(); private List<IssueRow> filteredRows=new(); private List<IssueRow> selectedRows=new(); private List<IssueRow> reviewQueue=new(); private int currentIssueIndex=-1; private IssueRow? currentIssue; private string pendingOpenIssueId="",lastContextScope=""; private CancellationTokenSource prefetchCts=new(); private readonly FastIssueOpenService fastOpen; private Dictionary<Guid,BuildAiRevitIssueRecord> buildAiByIssueId=new(); private HashSet<Guid> ambiguousBuildAiIds=new(); private bool isUpdatingSelection,isApplyingFilters,isRefreshing,isRevitActionRunning,documentContextStale; private readonly string windowInstanceId=Guid.NewGuid().ToString("N");
    internal Document HostDocument=>hostDocument;
    internal void NotifyDocumentChanged(){prefetchCts.Cancel();actions.CancelPending();fastOpen.Invalidate();documentContextStale=true;ModeText.Text="Active Revit document changed. Return to the original document and press Refresh, or open ACC Issue Return for the new document.";}
    internal ImportWindow(Autodesk.Revit.UI.UIDocument document,IAccIssueClient issueClient,IAccAuthProvider authProvider,RevitActionQueue actionQueue){InitializeComponent();try{Icon=RibbonIconLoader.Load(32);BrandImage.Source=Icon;}catch(Exception ex){AccIssueReturnLog.Event("ACC_BRAND_ICON_WARNING",new{errorType=ex.GetType().Name});}uidoc=document;hostDocument=document.Document;actions=actionQueue;hostModelUid=document.Document.ProjectInformation?.UniqueId??"";client=issueClient;auth=authProvider;buildAiClient=new BuildAiRevitIssuesClient(authProvider,new System.Net.Http.HttpClient{Timeout=TimeSpan.FromSeconds(60)});service=new ImportService(document,issueClient);fastOpen=new FastIssueOpenService(document);ModeText.Text="Mode: "+auth.Mode+" | API: "+auth.BackendUrl;Loaded+=WindowLoaded;Closed+=WindowClosed;AccIssueReturnLog.Event("ACC_WINDOW_OPENED",new{windowMode="Modeless",windowInstanceId,documentTitleHash=BuildAiRevitIssuesClient.DiagnosticKey(document.Document.Title).Hash,sessionRestored=false,issuesAlreadyLoaded=false});}
    private async void WindowClosed(object? sender,EventArgs e){cts.Cancel();prefetchCts.Cancel();actions.CancelPending();try{var cleanup=actions.Enqueue("CloseReview",null,_=>{fastOpen.Dispose();return true;});await Task.WhenAny(cleanup,Task.Delay(1000));}catch{}finally{actions.Dispose();cts.Dispose();prefetchCts.Dispose();}}
    private async void WindowLoaded(object sender,RoutedEventArgs e){try{IsEnabled=false;await InitializeAsync();}catch(OperationCanceledException){ModeText.Text="Loading cancelled.";}catch(Exception ex){AccIssueReturnLog.Event("ACC_WINDOW_INITIALIZE_FAILED",new{error=ex.Message});ShowError(ex);}finally{IsEnabled=true;}}
    private async Task InitializeAsync(){hubs=await client.GetHubsAsync(cts.Token);HubBox.ItemsSource=hubs;HubBox.DisplayMemberPath="Name";}
    private async Task LoadHubsAsync(){await InitializeAsync();}
    private async void HubChanged(object sender,SelectionChangedEventArgs e){if(HubBox.SelectedItem is not AccHub hub)return;try{projects=await client.GetProjectsAsync(hub.Id,cts.Token);ProjectBox.ItemsSource=projects;ProjectBox.DisplayMemberPath="Name";}catch(Exception ex){ShowError(ex);}}
    private async void ProjectChanged(object sender,SelectionChangedEventArgs e){if(ProjectBox.SelectedItem is not AccProject project)return;try{(client as AccIssueClient)?.ResetContextCache();lastContextScope="";models=await client.GetModelsAsync(project.Id,cts.Token);ModelList.ItemsSource=models;ModelList.DisplayMemberPath="DisplayLabel";}catch(Exception ex){ShowError(ex);}}
    private async void LoadIssuesClicked(object sender,RoutedEventArgs e){AccIssueReturnLog.Event("ACC_LOAD_ISSUES_STARTED",new{projectSelected=ProjectBox.SelectedItem!=null,selectedModels=ModelList.SelectedItems.Count});if(ProjectBox.SelectedItem is not AccProject project){AccIssueReturnLog.Event("ACC_LOAD_ISSUES_SKIPPED",new{reason="No project"});ShowError(new InvalidOperationException("Select an ACC project first."));return;}var selected=ModelList.SelectedItems.Cast<AccModel>().ToList();try{var contextScope=project.Id+"|"+string.Join(";",selected.Select(m=>m.ItemUrn+"|"+m.VersionUrn+"|"+m.ViewableGuid).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase));if(!string.Equals(lastContextScope,contextScope,StringComparison.OrdinalIgnoreCase)){(client as AccIssueClient)?.ResetContextCache();lastContextScope=contextScope;}prefetchCts.Cancel();prefetchCts.Dispose();prefetchCts=new CancellationTokenSource();await actions.Enqueue("RefreshRevitContext",null,_=>{fastOpen.Reset();return true;});cts.Dispose();cts=new CancellationTokenSource();var issues=await client.GetIssuesAsync(project,selected,cts.Token,new Progress<string>(message=>AccIssueReturnLog.Event("ACC_LOAD_ISSUES_PROGRESS",new{projectId=Short(project.Id),message})));allRows=issues.Select(x=>new IssueRow(x)).ToList(); buildAiByIssueId=new(); ambiguousBuildAiIds=new(); try{
            var modelUid=string.IsNullOrWhiteSpace(hostModelUid)?project.Id:hostModelUid;
            var buildAiRows=new List<BuildAiRevitIssueRecord>();
            var recordSources=new Dictionary<BuildAiRevitIssueRecord,string>();
            foreach(var source in new[]{"ar-st","clash"}){
                var sourceRows=await buildAiClient.GetAsync(modelUid,source,cts.Token);
                buildAiRows.AddRange(sourceRows);
                foreach(var row in sourceRows)recordSources[row]=source;
            }
            var arIds=new HashSet<string>(recordSources.Where(x=>x.Value=="ar-st").Select(x=>x.Key.IssueId),StringComparer.OrdinalIgnoreCase);
            var clashIds=new HashSet<string>(recordSources.Where(x=>x.Value=="clash").Select(x=>x.Key.IssueId),StringComparer.OrdinalIgnoreCase);
            if(arIds.Count>0&&arIds.SetEquals(clashIds))AccIssueReturnLog.Event("BUILDAI_SOURCE_FILTER_SUSPECT",new{requestedSources=new[]{"ar-st","clash"},arStCount=arIds.Count,clashCount=clashIds.Count,identicalIssueIds=true,backendFilteringNeedsVerification=true});
            var buildAi=buildAiRows;
            var index=new Dictionary<Guid,BuildAiRevitIssueRecord>();
            var ambiguousKeys=new HashSet<Guid>();
            var buildAiIdsMissing=0; var buildAiIdsParsed=0; var buildAiIdsInvalid=0;
            foreach(var record in buildAi){
                if(string.IsNullOrWhiteSpace(record.IssueId)){buildAiIdsMissing++;continue;}
                if(!BuildAiRevitIssuesClient.TryNormalizeIssueId(record.IssueId,out var key)){buildAiIdsInvalid++;continue;}
                buildAiIdsParsed++;
                if(index.TryGetValue(key,out var existing)){if(!SameBuildAiMapping(existing,record))ambiguousKeys.Add(key);}
                else index.Add(key,record);
            }
            buildAiByIssueId=index; ambiguousBuildAiIds=ambiguousKeys;
            var fingerprints=index.Keys.Take(5).Select(BuildAiRevitIssuesClient.DiagnosticKey).ToList();
            AccIssueReturnLog.Event("BUILDAI_MAPPING_LOOKUP_BUILT",new{
                receivedRecords=buildAi.Count,recordsWithRawIssueId=buildAi.Count(x=>x.RawIssueIdPresent),recordsWithValidGuid=buildAiIdsParsed,
                recordsWithoutIssueId=buildAiIdsMissing,invalidGuidCount=buildAiIdsInvalid,uniqueNormalizedKeys=index.Count,
                duplicateNormalizedKeys=buildAiIdsParsed-index.Count,conflictingNormalizedKeys=ambiguousKeys.Count,sources=new[]{"ar-st","clash"},
                firstFiveKeySuffixes=fingerprints.Select(x=>x.Suffix).ToArray(),firstFiveKeyHashes=fingerprints.Select(x=>x.Hash).ToArray()
            });
            var matched=0; var matchedByGuid=0; var accIdsParsed=0; var accIdsInvalid=0; var missesLogged=0; var matchesLogged=0; var accIndex=0;
            foreach(var issueRow in allRows){
                var accRaw=issueRow.Issue.Id;
                var accValid=BuildAiRevitIssuesClient.TryNormalizeIssueId(accRaw,out var accKey);
                if(accValid)accIdsParsed++;else accIdsInvalid++;
                var accFingerprint=accValid?BuildAiRevitIssuesClient.DiagnosticKey(accKey):("","");
                if(accIndex<5)AccIssueReturnLog.Event("ACC_MAPPING_KEY_PARSED",new{recordIndex=accIndex,sourceJsonProperty="id",rawValuePresent=!string.IsNullOrWhiteSpace(accRaw),rawLength=accRaw?.Length??0,guidParseSucceeded=accValid,normalizedIdSuffix=accFingerprint.Item1,normalizedIdHash=accFingerprint.Item2});
                accIndex++;
                if(!accValid||!index.TryGetValue(accKey,out var mapping)||ambiguousKeys.Contains(accKey)){
                    if(accValid&&ambiguousKeys.Contains(accKey)){issueRow.Issue.BuildAiMappingStatus=BuildAiMappingStatus.Ambiguous;issueRow.Issue.RevitMappingSource="BuildAIAmbiguous";}
                    if(missesLogged<5){
                        var sameSuffix=accValid&&index.Keys.Any(x=>BuildAiRevitIssuesClient.DiagnosticKey(x).Suffix==accFingerprint.Item1);
                        var sameHash=accValid&&index.Keys.Any(x=>BuildAiRevitIssuesClient.DiagnosticKey(x).Hash==accFingerprint.Item2);
                        var reason=string.IsNullOrWhiteSpace(accRaw)?"AccIdMissing":!accValid?"AccIdInvalidGuid":ambiguousKeys.Contains(accKey)?"Unknown":buildAi.Count>0&&buildAiIdsMissing==buildAi.Count?"BuildAiIssueIdMissing":index.Count==0?"BuildAiLookupEmpty":sameSuffix&&!sameHash?"SameSuffixDifferentHash":sameHash?"Unknown":"KeyNotPresent";
                        AccIssueReturnLog.Event("BUILDAI_MAPPING_MISS",new{accIssueIdSuffix=accFingerprint.Item1,accIssueIdHash=accFingerprint.Item2,accGuidParseSucceeded=accValid,buildAiLookupCount=index.Count,sameSuffixFound=sameSuffix,sameHashFound=sameHash,closestDiagnosticReason=reason});
                        missesLogged++;
                    }
                    continue;
                }
                issueRow.Issue.BuildAiRecord=mapping;issueRow.Issue.BuildAiMappingStatus=BuildAiMappingStatus.Exact;issueRow.Issue.RevitMappingSource="BuildAI";matched++;matchedByGuid++;
                if(matchesLogged<5){
                    var primary=mapping.PrimaryElement;var secondary=mapping.SecondaryElement;
                    AccIssueReturnLog.Event("BUILDAI_MAPPING_MATCH",new{issueIdSuffix=accFingerprint.Item1,issueIdHash=accFingerprint.Item2,source=recordSources[mapping],primaryElementPresent=primary!=null,secondaryElementPresent=secondary!=null,primaryLinkInstanceUidPresent=!string.IsNullOrWhiteSpace(primary?.LinkInstanceUid),primaryElementUniqueIdPresent=!string.IsNullOrWhiteSpace(primary?.ElementUniqueId),secondaryLinkInstanceUidPresent=!string.IsNullOrWhiteSpace(secondary?.LinkInstanceUid),secondaryElementUniqueIdPresent=!string.IsNullOrWhiteSpace(secondary?.ElementUniqueId)});
                    matchesLogged++;
                }
                AccIssueReturnLog.Event("BUILDAI_ISSUE_MATCH",new{issueId=Short(issueRow.Issue.Id),matched=true,source="BuildAI",primaryPresent=mapping.PrimaryElement!=null,secondaryPresent=mapping.SecondaryElement!=null});
            }
            AccIssueReturnLog.Event("BUILDAI_REVIT_ISSUES_LOADED",new{received=buildAi.Count,uniqueIssueIds=index.Count,duplicates=buildAi.Count-index.Count,matchedAccIssues=matched,accIssuesWithoutBuildAiMapping=allRows.Count-matched,accIdsParsed,accIdsInvalid,buildAiIdsParsed,buildAiIdsMissing,buildAiIdsInvalid,lookupKeys=index.Count,matchedByGuid,unmatchedByGuid=allRows.Count-matchedByGuid});
        }catch(Exception ex){AccIssueReturnLog.Event("BUILDAI_REVIT_ISSUES_FAILED",new{error=ex.Message});}var resolved=await actions.Enqueue("BuildIndexes",null,_=>{fastOpen.BuildIndexes(allRows.Select(r=>r.Issue));return allRows.Select(r=>fastOpen.Resolve(r.Issue,false)).ToArray();});for(var i=0;i<allRows.Count;i++)UpdateOpenStatus(allRows[i],resolved[i]);models=MergeIssueReferencedModels(models,issues);ModelList.ItemsSource=models;ModelList.DisplayMemberPath="DisplayLabel";FillFilters();ApplyFilters("LoadIssues");AccIssueReturnLog.Event("ACC_LOAD_ISSUES_FINISHED",new{projectId=Short(project.Id),selectedModels=selected.Count,loaded=allRows.Count,withObjectSet=allRows.Count(x=>x.Issue.HasObjectSet),withoutObjectSet=allRows.Count(x=>!x.Issue.HasObjectSet)});}catch(OperationCanceledException){AccIssueReturnLog.Event("ACC_LOAD_ISSUES_CANCELLED",new{projectId=Short(project.Id)});ModeText.Text="Issue loading cancelled.";}catch(Exception ex){AccIssueReturnLog.Event("ACC_LOAD_ISSUES_FAILED",new{projectId=Short(project.Id),error=ex.Message,errorType=ex.GetType().Name});ShowError(ex);}}
    private IReadOnlyList<AccModel> MergeIssueReferencedModels(IReadOnlyList<AccModel> catalog,IReadOnlyList<AccIssue> issues){var result=catalog.ToList();foreach(var group in issues.Where(x=>!string.IsNullOrWhiteSpace(x.ModelUrn)||!string.IsNullOrWhiteSpace(x.ViewableGuid)).GroupBy(x=>string.Join("|",x.ModelUrn,x.LineageUrn,x.VersionUrn,x.SeedUrn,x.ViewableGuid),StringComparer.OrdinalIgnoreCase)){var first=group.First();var canonical=result.FirstOrDefault(m=>ApsUrnNormalizer.Same(first.ModelUrn,m.ItemUrn)||ApsUrnNormalizer.Same(first.ModelUrn,m.LineageUrn)||ApsUrnNormalizer.Same(first.LineageUrn,m.LineageUrn)||ApsUrnNormalizer.Same(first.SeedUrn,m.SeedUrn)); if(canonical!=null){canonical.RelatedIssueCount+=group.Count(); if(!string.IsNullOrWhiteSpace(first.ViewableGuid))canonical.ViewableGuid=first.ViewableGuid; canonical.ResolutionEvidence="Catalog identity + Issue reference"; AccIssueReturnLog.Event("ACC_ISSUE_MODEL_MERGE",new{issueModel=Short(first.ModelUrn),catalogMatch=true,canonicalItemId=Short(canonical.ItemUrn),rootModelCreated=false,viewableAdded=!string.IsNullOrWhiteSpace(first.ViewableGuid),versionInherited=!string.IsNullOrWhiteSpace(canonical.VersionUrn),conflicts=Array.Empty<string>()}); continue;}result.Add(new AccModel{ProjectId=first.ProjectId,Id=first.ModelUrn,Name=string.IsNullOrWhiteSpace(first.ViewableGuid)?"Referenced ACC model":"Issue viewable "+Short(first.ViewableGuid),ItemUrn=first.ModelUrn,LineageUrn=first.LineageUrn,VersionUrn=first.VersionUrn,SeedUrn=first.SeedUrn,DerivativeUrn=first.DerivativeUrn,ViewableGuid=first.ViewableGuid,Region=projectRegion(),RelatedIssueCount=group.Count(),IsLatest=false,VersionStatus=string.IsNullOrWhiteSpace(first.VersionUrn)?VersionResolutionStatus.Unknown:VersionResolutionStatus.InvalidCandidate,ResolutionEvidence=string.IsNullOrWhiteSpace(first.VersionUrn)?"MissingIssueVersion":"CandidateVersionNotVerifiedForItem",Source=AccModelSource.IssueLinkedDocument,ResolutionStatus=string.IsNullOrWhiteSpace(first.VersionUrn)?AccModelResolutionStatus.PartiallyResolved:AccModelResolutionStatus.Resolved});AccIssueReturnLog.Event("ACC_ISSUE_MODEL_REFERENCE_ADDED",new{modelUrn=Short(first.ModelUrn),viewableGuid=Short(first.ViewableGuid),issues=group.Count()});}AccIssueReturnLog.Event("ACC_ISSUE_MODEL_REFERENCES_MERGED",new{issueReferenced=result.Count(x=>x.Source==AccModelSource.IssueLinkedDocument),totalModels=result.Count});return result;}
    private string projectRegion()=>ProjectBox.SelectedItem is AccProject p?p.Region:"US";
    private void FillFilters(){isApplyingFilters=true;try{AssignedBox.ItemsSource=new[]{"<all>","<unassigned>"}.Concat(allRows.Select(x=>x.Issue.AssignedUserId).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)).ToList();TypeBox.ItemsSource=new[]{"<all>"}.Concat(allRows.Select(x=>x.Issue.Type).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)).ToList();IssueModelBox.ItemsSource=new[]{"<all>"}.Concat(allRows.SelectMany(x=>new[]{x.Issue.ModelUrn,x.Issue.LineageUrn,x.Issue.VersionUrn,x.Issue.ViewableGuid,x.Issue.SeedUrn}).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)).ToList();StatusBox.ItemsSource=new[]{"<all>"}.Concat(allRows.Select(x=>x.Issue.Status).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)).ToList();AssignedBox.SelectedIndex=TypeBox.SelectedIndex=IssueModelBox.SelectedIndex=StatusBox.SelectedIndex=0;}finally{isApplyingFilters=false;}}
    private void FilterChanged(object sender,EventArgs e){if(!IsLoaded||isApplyingFilters)return;Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>ApplyFilters("FilterChanged")));}
    private void ApplyFilters(string reason="FilterChanged")
    {
        if(isRefreshing||isApplyingFilters)return;
        var timer=Stopwatch.StartNew();isApplyingFilters=true;bool committed=false,deferred=false,adding=false,editing=false;
        try
        {
            if(!ReferenceEquals(IssueGrid.ItemsSource,allRows))IssueGrid.ItemsSource=allRows;
            var view=IssueGrid.Items as IEditableCollectionView;
            if(view is IEditableCollectionView editable)
            {
                adding=editable.IsAddingNew;editing=editable.IsEditingItem;
                if(adding||editing){IssueGrid.CommitEdit(DataGridEditingUnit.Cell,true);IssueGrid.CommitEdit(DataGridEditingUnit.Row,true);if(editable.IsAddingNew)editable.CommitNew();if(editable.IsEditingItem)editable.CommitEdit();committed=true;}
            }
            var filter=new IssueFilterState{AssignedUser=Value(AssignedBox),Type=Value(TypeBox),Model=Value(IssueModelBox),Status=Value(StatusBox),Search=SearchBox?.Text??""};
            var visible=new HashSet<AccIssue>(IssueFiltering.Apply(allRows.Select(x=>x.Issue),filter));
            CollectionViewSource.GetDefaultView(allRows).Filter=item=>item is IssueRow row&&visible.Contains(row.Issue);
            filteredRows=IssueGrid.Items.Cast<object>().OfType<IssueRow>().ToList();
            currentIssueIndex=currentIssue==null?-1:filteredRows.IndexOf(currentIssue);
        }
        catch(InvalidOperationException ex) when(ex.Message.Contains("AddNew")||ex.Message.Contains("EditItem"))
        {deferred=true;Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>ApplyFilters(reason)));}
        finally{isApplyingFilters=false;if(!deferred)UpdateSelection("FilterChanged");timer.Stop();AccIssueReturnLog.Event("ACC_COLLECTION_REFRESH",new{reason,isAddingNew=adding,isEditingItem=editing,editCommitted=committed,deferred,elapsedMs=timer.ElapsedMilliseconds});}
    }
    private void UpdateCounts()=>CountsText.Text=$"Loaded: {allRows.Count} | Filtered: {IssueGrid.Items.Count} | Selected: {selectedRows.Count} | Ready: {allRows.Count(x=>x.State==ImportState.PreviewReady)} | Warnings: {allRows.Count(x=>x.State==ImportState.Warning)} | Blocked: {allRows.Count(x=>x.State==ImportState.Blocked)}";
    private void UpdateNavigationButtons(){OpenButton.IsEnabled=!isRevitActionRunning&&(selectedRows.Count>0||currentIssue!=null);var sequence=reviewQueue.Count>1?reviewQueue:IssueGrid.Items.Cast<object>().OfType<IssueRow>().ToList();var index=currentIssue==null?-1:sequence.IndexOf(currentIssue);PreviousButton.IsEnabled=!isRevitActionRunning&&index>0;NextButton.IsEnabled=!isRevitActionRunning&&index>=0&&index<sequence.Count-1;}
    private static string Value(ComboBox b)=>b?.SelectedItem is string s&&s!="<all>"?s:"";
    private void SelectAllClicked(object sender,RoutedEventArgs e){try{isUpdatingSelection=true;IssueGrid.SelectAll();}catch(Exception ex){ShowError(ex);}finally{isUpdatingSelection=false;UpdateSelection("SelectAll");}}
    private void ClearSelectionClicked(object sender,RoutedEventArgs e){try{isUpdatingSelection=true;IssueGrid.UnselectAll();reviewQueue.Clear();}catch(Exception ex){ShowError(ex);}finally{isUpdatingSelection=false;UpdateSelection("ClearSelection");}}
    private void IssueDoubleClicked(object sender,MouseButtonEventArgs e){if(IssueGrid.SelectedItem is IssueRow row)_=OpenRow(row);}
    private void IssueSelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(isUpdatingSelection||isApplyingFilters)return;
        UpdateSelection("SelectionChanged",e.AddedItems.Count,e.RemovedItems.Count);
        if(OpenOnSelectionBox?.IsChecked==true&&selectedRows.Count==1&&IssueGrid.SelectedItem is IssueRow row)_=OpenRow(row);
    }
    private void UpdateSelection(string source,int added=0,int removed=0)
    {
        var visible=IssueGrid.Items.Cast<object>().OfType<IssueRow>().ToHashSet();
        selectedRows=IssueGrid.SelectedItems.Cast<object>().OfType<IssueRow>().Where(visible.Contains).ToList();
        if(selectedRows.Count>0)currentIssue=IssueGrid.SelectedItem as IssueRow??selectedRows[0];
        if(source=="FilterChanged"&&currentIssue!=null&&!visible.Contains(currentIssue))currentIssue=selectedRows.FirstOrDefault()??IssueGrid.Items.Cast<object>().OfType<IssueRow>().FirstOrDefault();
        reviewQueue=selectedRows.Count>1?IssueGrid.Items.Cast<object>().OfType<IssueRow>().Where(selectedRows.Contains).ToList():new List<IssueRow>();
        if(reviewQueue.Count>0&&currentIssue!=null&&!reviewQueue.Contains(currentIssue))currentIssue=reviewQueue[0];
        var view=CollectionViewSource.GetDefaultView(allRows) as IEditableCollectionView;
        AccIssueReturnLog.Event("ACC_DATAGRID_SELECTION_CHANGED",new{selectedCount=selectedRows.Count,addedCount=added,removedCount=removed,isAddingNew=view?.IsAddingNew??false,isEditingItem=view?.IsEditingItem??false,refreshRequested=false,refreshDeferred=false});
        AccIssueReturnLog.Event("ACC_REVIEW_QUEUE_CHANGED",new{selectedCount=selectedRows.Count,queueCount=reviewQueue.Count,currentIndex=reviewQueue.Count>0?reviewQueue.IndexOf(currentIssue!)+1:0,source,filteredCount=IssueGrid.Items.Count});
        UpdateCounts();UpdateNavigationButtons();
    }
    private void OpenClicked(object sender,RoutedEventArgs e){var row=reviewQueue.Count>1?reviewQueue[0]:selectedRows.FirstOrDefault()??currentIssue;if(row!=null)_=OpenRow(row);else ShowError(new InvalidOperationException("Select an Issue row first."));}
    private void PreviousClicked(object sender,RoutedEventArgs e)=>Navigate(-1);
    private void NextClicked(object sender,RoutedEventArgs e)=>Navigate(1);
    private async void Navigate(int delta){var rows=reviewQueue.Count>1?reviewQueue.ToList():IssueGrid.Items.Cast<object>().OfType<IssueRow>().ToList();if(rows.Count==0)return;var i=currentIssue==null?-1:rows.IndexOf(currentIssue);var next=i+delta;if(next<0||next>=rows.Count)return;var from=Short(currentIssue?.Issue.Id??"");var target=rows[next];var timer=Stopwatch.StartNew();try{await OpenRow(target);}catch(Exception ex){target.Warning=ex.Message;}finally{AccIssueReturnLog.Event("ACC_ISSUE_NAVIGATION",new{direction=delta<0?"Previous":"Next",fromIssueId=from,toIssueId=Short(target.Issue.Id),visibleIndex=next+1,visibleCount=rows.Count,queueCount=reviewQueue.Count,cacheHit=fastOpen.HasCached(target.Issue.Id),success=ReferenceEquals(currentIssue,target),elapsedMs=timer.ElapsedMilliseconds,failureStage=ReferenceEquals(currentIssue,target)?"":"FastOpen"});}}
    private async Task OpenRow(IssueRow row)
    {
        if(documentContextStale){ReviewText.Text="Revit document changed. Return to the original document and press Refresh.";return;}
        if(!double.TryParse(PaddingBox.Text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.CurrentCulture,out var padding)||padding<=0){ShowError(new InvalidOperationException("Section padding must be a positive number of metres."));return;}
        var rows=reviewQueue.Count>1?reviewQueue.ToList():IssueGrid.Items.Cast<object>().OfType<IssueRow>().ToList();var index=rows.IndexOf(row);
        if(index<0){rows=IssueGrid.Items.Cast<object>().OfType<IssueRow>().ToList();index=rows.IndexOf(row);if(index<0)return;}
        if(isRevitActionRunning&&pendingOpenIssueId==row.Issue.Id)return;
        var openTimer=Stopwatch.StartNew();
        var hit=fastOpen.HasCached(row.Issue.Id);
        AccIssueReturnLog.Event("ACC_FAST_OPEN_STARTED",new{issueIdSuffix=Short(row.Issue.Id),currentIndex=index+1,totalFiltered=rows.Count,mappingFound=row.Issue.BuildAiRecord!=null,cacheHit=hit});
        pendingOpenIssueId=row.Issue.Id;isRevitActionRunning=true;UpdateNavigationButtons();
        try
        {
            var coordinates=row.Preview?.Coordinates;
            var validatedPoint=coordinates?.CoordinateValidated==true&&coordinates.RevitPointFeet?.Length==3?coordinates.RevitPointFeet.ToArray():null;
            var catalogModel=models.FirstOrDefault(m=>m.Source==AccModelSource.ProjectCatalog&&ApsUrnNormalizer.Same(m.ItemUrn,row.Issue.ModelUrn));
            var versionMismatch=catalogModel!=null&&!string.IsNullOrWhiteSpace(row.Issue.VersionUrn)&&!string.IsNullOrWhiteSpace(catalogModel.VersionUrn)&&!ApsUrnNormalizer.Same(row.Issue.VersionUrn,catalogModel.VersionUrn);
            var context=await actions.Enqueue("OpenIssue",row.Issue.Id,_=>{
                if(Math.Abs(fastOpen.PaddingMetres-padding)>0.00001)fastOpen.PaddingMetres=padding;
                var point=validatedPoint==null?null:new XYZ(validatedPoint[0],validatedPoint[1],validatedPoint[2]);
                var resolved=fastOpen.Resolve(row.Issue,false,point);
                if(versionMismatch&&!resolved.Warning.Contains("Version mismatch"))resolved.Warning=(resolved.Warning+"; Version mismatch; current Revit geometry used").Trim(' ',';');
                if(resolved.CanOpenInRevit)fastOpen.Open(resolved,openTimer.ElapsedMilliseconds);
                return resolved;
            },coalesceOpen:true);
            UpdateOpenStatus(row,context);
            row.Warning=context.Warning;
            if(!context.CanOpenInRevit){ReviewText.Text=context.BlockingReason;AccIssueReturnLog.Event("ACC_FAST_OPEN_FINISHED",new{issueIdSuffix=Short(row.Issue.Id),success=false,canOpenInRevit=false,primaryResolved=context.Primary!=null,secondaryResolved=context.Secondary!=null,sectionBoxApplied=false,highlightApplied=false,warning=context.Warning,blockingReason=context.BlockingReason,totalElapsedMs=openTimer.ElapsedMilliseconds});return;}
            currentIssue=row;
            currentIssueIndex=index;
            ReviewText.Text=$"Issue {index+1} of {rows.Count}: {row.Issue.DisplayId} — {context.Warning}";
            SchedulePrefetch(rows,index);
        }
        catch(TaskCanceledException){}
        catch(Exception ex){row.Warning=ex.Message;ReviewText.Text="Open failed: "+ex.Message;AccIssueReturnLog.Event("ACC_FAST_OPEN_ROW_FAILED",new{issueIdSuffix=Short(row.Issue.Id),error=ex.Message});}
        finally{if(pendingOpenIssueId==row.Issue.Id)pendingOpenIssueId="";isRevitActionRunning=actions.IsRevitActionRunning||actions.QueueLength>0;UpdateNavigationButtons();}
    }
    private async void SchedulePrefetch(List<IssueRow> rows,int index)
    {
        prefetchCts.Cancel();prefetchCts.Dispose();prefetchCts=new CancellationTokenSource();var token=prefetchCts.Token;
        var next=rows.Skip(index+1).Take(8).Where(r=>!fastOpen.HasCached(r.Issue.Id)).ToArray();
        try{foreach(var row in next){if(token.IsCancellationRequested)return;var result=await actions.Enqueue("Prefetch",row.Issue.Id,_=>token.IsCancellationRequested?null:fastOpen.Resolve(row.Issue,false));if(result!=null&&!token.IsCancellationRequested)UpdateOpenStatus(row,result);}}
        catch(TaskCanceledException){}catch(Exception ex){AccIssueReturnLog.Event("ACC_PREFETCH_FAILED",new{errorType=ex.GetType().Name});}
    }
    private static void UpdateOpenStatus(IssueRow row,ResolvedIssueContext c)
    {
        row.CanOpenInRevit=c.CanOpenInRevit;
        row.PrimaryElementStatus=c.Primary==null?"Missing":c.Primary.Method;
        row.SecondaryElementStatus=c.Secondary==null?"Missing":c.Secondary.Method;
        row.SectionBoxStatus=c.SectionBox==null?"Unavailable":"Cached";
        row.OpenStatus=!c.CanOpenInRevit?c.BlockingReason:c.Primary!=null&&c.Secondary!=null?"Primary + Secondary resolved":c.Primary!=null?"Primary resolved; Secondary missing":c.Secondary!=null?"Secondary resolved; Primary missing":"Validated coordinate only";
        row.Warning=c.Warning;
    }
    private async void PreviewClicked(object sender,RoutedEventArgs e)
    {
        var selected=selectedRows.ToList();
        AccIssueReturnLog.Event("ACC_PREVIEW_STARTED",new{selected=selected.Count,total=allRows.Count,models=models.Count});
        if(selected.Count==0){AccIssueReturnLog.Event("ACC_PREVIEW_NOT_STARTED",new{reason="NoCheckedIssues"});ShowError(new InvalidOperationException("Select at least one Issue to preview."));return;}
        var completed=0;var ready=0;var blocked=0;var failed=0;var windowOpened=false;
        try
        {
            cts.Dispose();cts=new CancellationTokenSource();
            foreach(var row in selected)
            {
                var mappingPassedToPreview=false;
                var exactBuildAiElement=false;
                try
                {
                    AccIssueReturnLog.Event("ACC_PREVIEW_ROW_STARTED",new{issueId=Short(row.Issue.Id)});
                    exactBuildAiElement=row.Issue.BuildAiRecord!=null&&await actions.Enqueue("PreviewElementCheck",row.Issue.Id,_=>service.HasExactBuildAiElement(row.Issue));
                    var candidates=models.Where(x=>AccModelMatcher.Matches(row.Issue,x)).ToList();
                    var lineageModels=models.Where(x=>x.Source==AccModelSource.ProjectCatalog&&
                        (ApsUrnNormalizer.Same(row.Issue.ModelUrn,x.ItemUrn)||ApsUrnNormalizer.Same(row.Issue.ModelUrn,x.LineageUrn)||ApsUrnNormalizer.Same(row.Issue.LineageUrn,x.LineageUrn)||ApsUrnNormalizer.Same(row.Issue.LineageUrn,x.ItemUrn))).ToList();
                    if(row.Issue.BuildAiRecord!=null&&exactBuildAiElement){
                        var modelUid=new[]{row.Issue.BuildAiRecord.PrimaryElement?.ModelUid,row.Issue.BuildAiRecord.SecondaryElement?.ModelUid}.FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x))??"";
                        var mappedModels=string.IsNullOrWhiteSpace(modelUid)?new List<AccModel>():models.Where(x=>ApsUrnNormalizer.Same(modelUid,x.ItemUrn)||ApsUrnNormalizer.Same(modelUid,x.LineageUrn)).ToList();
                        var accModels=models.Where(x=>AccModelMatcher.Matches(row.Issue,x)).ToList();
                        if(mappedModels.Count>0)candidates=mappedModels;
                        else if(lineageModels.Count>0)candidates=lineageModels;
                        else if(accModels.Count>0)candidates=accModels;
                    }
                    if(lineageModels.Count==1&&!candidates.Any(x=>x.Source==AccModelSource.ProjectCatalog))candidates=lineageModels;
                    // Several viewables or historical versions may share one ACC item. Prefer the
                    // issue version when it is in that lineage, then the canonical catalog version.
                    if(candidates.Count>1){
                        var exactVersion=candidates.Where(x=>!string.IsNullOrWhiteSpace(row.Issue.VersionUrn)&&ApsUrnNormalizer.Same(x.VersionUrn,row.Issue.VersionUrn)&&x.VersionStatus!=VersionResolutionStatus.InvalidCandidate).ToList();
                        if(exactVersion.Count==1)candidates=exactVersion;
                        else{
                            var catalog=candidates.Where(x=>x.Source==AccModelSource.ProjectCatalog&&!string.IsNullOrWhiteSpace(x.VersionUrn)).ToList();
                            if(catalog.Count==1)candidates=catalog;
                        }
                    }
                    var match=candidates.Count==1?AccModelMatcher.Match(row.Issue,candidates[0]):new AccModelMatchResult{Reason=candidates.Count==0?"ModelNotFound":"MultipleModelsMatch"};
                    AccIssueReturnLog.Event("ACC_PREVIEW_MODEL_MATCH",new{issueId=Short(row.Issue.Id),candidates=candidates.Count,issueVersion=Short(row.Issue.VersionUrn),issueModel=Short(row.Issue.ModelUrn),viewableGuid=Short(row.Issue.ViewableGuid),candidateVersion=candidates.Count==1?candidates[0].VersionNumber:null,candidateVersionStatus=candidates.Count==1?candidates[0].VersionStatus.ToString():"",matchReason=match.Reason});
                    if(candidates.Count!=1)
                    {
                        row.Preview=new PreviewRow{Issue=row.Issue,Coordinates=new CoordinateResult{Quality=CoordinateQuality.AmbiguousModel,Warning=candidates.Count==0?"ACC model/viewable was not resolved.":"Multiple ACC model contexts matched this Issue."},BlockingReasons=new List<string>{candidates.Count==0?"Source model or viewable could not be resolved.":"Multiple model/viewable candidates matched."}};row.State=ImportState.Blocked;AccIssueReturnLog.Event("ACC_PREVIEW_VERSION_RESOLUTION",new{issueIdSuffix=Short(row.Issue.Id),issueVersion=Short(row.Issue.VersionUrn),catalogVersion=(int?)null,selectedVersion=(int?)null,selectedVersionUrnPresent=false,source="None",fallbackUsed=false,versionMismatch=false,canonicalItemMatched=false,viewableGuidMatched=false});AccIssueReturnLog.Event("ACC_PREVIEW_FINAL_DECISION",new{issueIdSuffix=Short(row.Issue.Id),versionResolved=false,versionMismatch=false,accExternalIdMatched=false,buildAiExactElementMatched=exactBuildAiElement,externalElementMatched=false,coordinateSource="None",coordinateValidated=false,state="Blocked",warnings="",blockingReasons=row.Preview.BlockingReasons});blocked++;completed++;continue;
                    }
                    mappingPassedToPreview=true;
                    IssueCoordinateContext coordinateContext;
                    try{coordinateContext=await client.GetContextAsync(row.Issue,candidates[0],cts.Token);}
                    catch(OperationCanceledException){throw;}
                    catch(Exception ex){coordinateContext=new IssueCoordinateContext{ModelUrn=candidates[0].ItemUrn,VersionUrn=candidates[0].VersionUrn,ViewableGuid=string.IsNullOrWhiteSpace(row.Issue.ViewableGuid)?candidates[0].ViewableGuid:row.Issue.ViewableGuid,IncompleteReason="Model Derivative context unavailable: "+ex.GetType().Name};}
                    var suppliedContext=coordinateContext;
                    row.Preview=await actions.Enqueue("PreviewIssue",row.Issue.Id,_=>service.PreviewAsync(row.Issue,candidates[0],cts.Token,suppliedContext).GetAwaiter().GetResult());
                    if(row.Preview.BlockingReasons.Count==0&&!row.Preview.Coordinates.IsImportable)row.Preview.BlockingReasons.Add(row.Preview.Coordinates.Warning.Length>0?row.Preview.Coordinates.Warning:"Coordinate validation failed.");
                    var versionFallback=candidates[0].Source==AccModelSource.ProjectCatalog&&!string.IsNullOrWhiteSpace(candidates[0].VersionUrn)&&!ApsUrnNormalizer.Same(row.Issue.VersionUrn,candidates[0].VersionUrn);
                    var versionMismatch=!string.IsNullOrWhiteSpace(row.Issue.VersionUrn)&&!string.IsNullOrWhiteSpace(candidates[0].VersionUrn)&&!ApsUrnNormalizer.Same(row.Issue.VersionUrn,candidates[0].VersionUrn);
                    if(versionFallback){row.Preview.Coordinates.Warning=(row.Preview.Coordinates.Warning+" Version mismatch or issue version unavailable; canonical ACC model version was used.").Trim();row.Issue.Warning=row.Preview.Coordinates.Warning;}
                    row.State=row.Preview.Coordinates.IsImportable?(versionFallback||row.Preview.Coordinates.Quality==CoordinateQuality.RecoveredFromElement?ImportState.Warning:ImportState.PreviewReady):ImportState.Blocked;
                    var fingerprint=BuildAiRevitIssuesClient.TryNormalizeIssueId(row.Issue.Id,out var parsedId)?BuildAiRevitIssuesClient.DiagnosticKey(parsedId):("","");
                    AccIssueReturnLog.Event("ACC_PREVIEW_FINAL_DECISION",new{issueIdSuffix=fingerprint.Item1,issueIdHash=fingerprint.Item2,versionResolved=!string.IsNullOrWhiteSpace(candidates[0].VersionUrn),versionMismatch,accExternalIdMatched=row.Preview.AccExternalIdMatched,buildAiExactElementMatched=row.Preview.BuildAiExactElementMatched,externalElementMatched=row.Preview.Coordinates.ExternalIdMatched,externalElementMatchSource=row.Preview.AccExternalIdMatched?(row.Preview.BuildAiExactElementMatched?"Both":"AccObjectSetExternalId"):(row.Preview.BuildAiExactElementMatched?"BuildAiIssueMapping":"None"),coordinateSource=row.Preview.CoordinateSource,coordinateValidated=row.Preview.Coordinates.CoordinateValidated,state=row.State==ImportState.Warning?"ReadyWithWarning":row.State.ToString(),warnings=row.Issue.Warning,blockingReasons=row.Preview.BlockingReasons});
                    AccIssueReturnLog.Event("ACC_PREVIEW_VERSION_RESOLUTION",new{issueIdSuffix=fingerprint.Item1,issueIdHash=fingerprint.Item2,issueVersion=Short(row.Issue.VersionUrn),catalogVersion=lineageModels.Count==1?lineageModels[0].VersionNumber:null,selectedVersion=candidates[0].VersionNumber,selectedVersionUrnPresent=!string.IsNullOrWhiteSpace(candidates[0].VersionUrn),source=candidates[0].Source.ToString(),fallbackUsed=versionFallback,versionMismatch,canonicalItemMatched=lineageModels.Contains(candidates[0]),viewableGuidMatched=ApsUrnNormalizer.Same(row.Issue.ViewableGuid,candidates[0].ViewableGuid)});
                    if(row.Preview.Coordinates.IsImportable)ready++;else blocked++;
                    completed++;
                    AccIssueReturnLog.Event("ACC_PREVIEW_ROW_COMPLETED",new{issueId=Short(row.Issue.Id),state=row.State.ToString(),quality=row.Preview.Coordinates.Quality.ToString(),coordinateValidated=row.Preview.Coordinates.CoordinateValidated,externalIdMatched=row.Preview.Coordinates.ExternalIdMatched,blockingReason=row.Preview.BlockingReasons.Count>0?row.Preview.Coordinates.Quality.ToString():""});
                }
                catch(OperationCanceledException){throw;}
                catch(Exception ex){row.Preview ??=new PreviewRow{Issue=row.Issue,Coordinates=new CoordinateResult{Quality=CoordinateQuality.Rejected,Warning=ex.Message},BlockingReasons=new List<string>{ex.Message}};row.State=ImportState.Failed;row.Issue.Warning=ex.Message;failed++;completed++;AccIssueReturnLog.Event("ACC_ISSUE_PREVIEW_FAILED",new{issueId=Short(row.Issue.Id),error=ex.Message,errorType=ex.GetType().Name});}
                finally{LogPreviewBuildAiMapping(row,mappingPassedToPreview,exactBuildAiElement);}
            }
            foreach(var row in selected)row.NotifyAll();UpdateCounts();
            AccIssueReturnLog.Event("ACC_PREVIEW_WINDOW_OPENING",new{selected=selected.Count,completed,ready,warnings=selected.Count(x=>x.State==ImportState.Warning),blocked,failed});
            var preview=new PreviewWindow(selected.Select(x=>x.Preview!).ToList(),this);preview.ShowDialog();windowOpened=preview.WindowOpenedSuccessfully;AccIssueReturnLog.Event("ACC_PREVIEW_WINDOW_CLOSED",new{selected=selected.Count,windowOpened});
            AccIssueReturnLog.Event("ACC_PREVIEW_FINISHED",new{selected=selected.Count,completed,ready,warnings=selected.Count(x=>x.State==ImportState.Warning),blocked,failed,windowOpened});
        }
        catch(OperationCanceledException){AccIssueReturnLog.Event("ACC_PREVIEW_CANCELLED",new{selected=selected.Count});ModeText.Text="Preview cancelled.";}
        catch(Exception ex){AccIssueReturnLog.Event(windowOpened?"ACC_PREVIEW_FAILED":"ACC_PREVIEW_WINDOW_FAILED",new{error=ex.Message,errorType=ex.GetType().Name,windowOpened});ShowError(ex);}
    }
    private void ExportDiagnosticClicked(object sender,RoutedEventArgs e){try{var row=selectedRows.FirstOrDefault();if(row==null){ShowError(new InvalidOperationException("Select one Issue first."));return;}var dialog=new SaveFileDialog{Title="Export sanitized ACC coordinate diagnostic",FileName="ACC-Issue-"+(row.Issue.DisplayId?.ToString()??row.Issue.Id)+"-coordinate.json",Filter="JSON (*.json)|*.json"};if(dialog.ShowDialog(this)!=true)return;new DiagnosticExportService().Export(dialog.FileName,row.Issue);MessageBox.Show("Sanitized diagnostic exported.","BuildAI ACC Issue Return",MessageBoxButton.OK,MessageBoxImage.Information);}catch(UnauthorizedAccessException ex){ExportFailed(ex,"The selected folder is not writable.");}catch(System.IO.DirectoryNotFoundException ex){ExportFailed(ex,"The selected folder no longer exists.");}catch(System.IO.PathTooLongException ex){ExportFailed(ex,"The selected path is too long.");}catch(NotSupportedException ex){ExportFailed(ex,"The selected path is not supported.");}catch(System.IO.IOException ex){ExportFailed(ex,"The diagnostic file could not be written.");}catch(Exception ex){ExportFailed(ex,"The diagnostic could not be exported.");}}
    private void ExportFailed(Exception ex,string message){AccIssueReturnLog.Event("ACC_DIAGNOSTIC_EXPORT_FAILED",new{error=ex.Message});ShowError(new InvalidOperationException(message));}
    private async void RecoverClicked(object sender,RoutedEventArgs e)
    {
        var rows=selectedRows.ToList();if(rows.Count!=1||rows[0].Preview==null){ShowError(new InvalidOperationException("Select exactly one previewed Issue before recovery."));return;}
        Hide();try{await actions.Enqueue("RecoverElement",rows[0].Issue.Id,app=>{var reference=app.ActiveUIDocument.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.LinkedElement,"Pick the linked Revit element that locates this ACC Issue.");service.RecoverFromLinkedElement(rows[0].Preview!,reference.ElementId,reference.LinkedElementId);return true;});rows[0].State=ImportState.Warning;}
        catch(Autodesk.Revit.Exceptions.OperationCanceledException){}
        catch(Exception ex){AccIssueReturnLog.Event("ACC_ELEMENT_RECOVERY_FAILED",new{issueId=rows[0].Issue.Id,error=ex.Message});ShowError(ex);}
        finally{Show();Activate();rows[0].NotifyAll();UpdateCounts();}
    }
    private async void ImportClicked(object sender,RoutedEventArgs e)
    {
        var selected=selectedRows.ToList();var valid=selected.Where(x=>x.CanImport).ToList();
        if(valid.Count==0){ShowError(new InvalidOperationException("Run Preview first and select valid rows."));return;}
        if(MessageBox.Show($"Import or update {valid.Count} validated Issue marker(s)?","BuildAI ACC Issue Return",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
        try
        {
            var outcomes=await actions.Enqueue("ImportSelected",null,_=>valid.Select(row=>{try{service.Import(row.Preview!);return (Row:row,Success:true,Error:"");}catch(Exception ex){return (Row:row,Success:false,Error:ex.Message);}}).ToArray());
            foreach(var result in outcomes){if(!result.Success){result.Row.State=ImportState.Failed;result.Row.Warning=result.Error;}result.Row.NotifyAll();}
            var imported=outcomes.Count(x=>x.Success);var failed=outcomes.Length-imported;var skipped=selected.Count-valid.Count;
            UpdateCounts();MessageBox.Show($"Imported/updated: {imported}\nSkipped or blocked: {skipped}\nFailed: {failed}","BuildAI ACC Issue Return",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        catch(Exception ex){ShowError(ex);}
    }
    private async void RefreshClicked(object sender,RoutedEventArgs e){if(isRefreshing)return;isRefreshing=true;try{prefetchCts.Cancel();actions.CancelPending();await actions.Enqueue("RefreshRevitContext",null,_=>{fastOpen.Reset();return true;});(client as AccIssueClient)?.ResetContextCache();lastContextScope="";documentContextStale=false;IssueGrid.ItemsSource=null;allRows=new();filteredRows=new();selectedRows=new();reviewQueue=new();currentIssue=null;currentIssueIndex=-1;buildAiByIssueId=new();ambiguousBuildAiIds=new();models=Array.Empty<AccModel>();ModelList.ItemsSource=models;UpdateCounts();UpdateNavigationButtons();ReviewText.Text="Issues cleared. Select a model and press Load Issues.";await LoadHubsAsync();}catch(OperationCanceledException){ModeText.Text="Refresh cancelled.";}catch(Exception ex){AccIssueReturnLog.Event("ACC_WINDOW_REFRESH_FAILED",new{error=ex.Message});ShowError(ex);}finally{isRefreshing=false;}}
    private void CancelClicked(object sender,RoutedEventArgs e){cts.Cancel();prefetchCts.Cancel();}
    private void CloseClicked(object sender,RoutedEventArgs e)=>Close();
    private void LogPreviewBuildAiMapping(IssueRow row,bool passedToPreview,bool exactElementResolved){
        var valid=BuildAiRevitIssuesClient.TryNormalizeIssueId(row.Issue.Id,out var key);
        var fingerprint=valid?BuildAiRevitIssuesClient.DiagnosticKey(key):("","");
        BuildAiRevitIssueRecord? mapping=null;
        var contains=valid&&buildAiByIssueId.TryGetValue(key,out mapping);
        var dto=contains?mapping:null;
        var primary=dto?.PrimaryElement;var secondary=dto?.SecondaryElement;
        var usedExactElement=row.Preview!=null&&new[]{primary,secondary}.Any(reference=>reference!=null
            &&string.Equals(reference.LinkInstanceUid,row.Preview.RevitLinkInstanceUniqueId,StringComparison.OrdinalIgnoreCase)
            &&!string.IsNullOrWhiteSpace(reference.ElementUniqueId)
            &&row.Preview.Elements.Any(elementId=>string.Equals(elementId,reference.ElementUniqueId,StringComparison.OrdinalIgnoreCase)));
        var applied=contains&&!ambiguousBuildAiIds.Contains(key)&&ReferenceEquals(row.Issue.BuildAiRecord,dto)&&passedToPreview&&exactElementResolved&&usedExactElement&&row.State!=ImportState.Failed;
        var reason=string.IsNullOrWhiteSpace(row.Issue.Id)?"AccIdMissing":!valid?"AccIdInvalidGuid":!contains?"KeyNotPresent":ambiguousBuildAiIds.Contains(key)?"ConflictingBuildAiRecords":row.State==ImportState.Failed?"PreviewFailed":!passedToPreview?"PreviewModelUnavailable":!exactElementResolved?"ExactBuildAiElementNotResolved":!usedExactElement?"BuildAiElementNotApplied":"";
        AccIssueReturnLog.Event("ACC_PREVIEW_BUILDAI_MAPPING",new{accIssueIdSuffix=fingerprint.Item1,accIssueIdHash=fingerprint.Item2,accGuidParseSucceeded=valid,buildAiLookupCount=buildAiByIssueId.Count,lookupContainsKey=contains,buildAiDtoIssueIdPresent=!string.IsNullOrWhiteSpace(dto?.IssueId),buildAiRawIssueIdPresent=dto?.RawIssueIdPresent??false,primaryElementPresent=primary!=null,secondaryElementPresent=secondary!=null,primaryLinkInstanceUidPresent=!string.IsNullOrWhiteSpace(primary?.LinkInstanceUid),primaryElementUniqueIdPresent=!string.IsNullOrWhiteSpace(primary?.ElementUniqueId),secondaryLinkInstanceUidPresent=!string.IsNullOrWhiteSpace(secondary?.LinkInstanceUid),secondaryElementUniqueIdPresent=!string.IsNullOrWhiteSpace(secondary?.ElementUniqueId),mappingApplied=applied,mappingFailureReason=reason});
    }
    private static bool SameBuildAiMapping(BuildAiRevitIssueRecord left,BuildAiRevitIssueRecord right)=>SameElement(left.PrimaryElement,right.PrimaryElement)&&SameElement(left.SecondaryElement,right.SecondaryElement);
    private static bool SameElement(BuildAiRevitElementReference? left,BuildAiRevitElementReference? right){
        if(left==null||right==null)return left==right;
        return string.Equals(left.ModelUid,right.ModelUid,StringComparison.OrdinalIgnoreCase)
            &&string.Equals(left.LinkInstanceUid,right.LinkInstanceUid,StringComparison.OrdinalIgnoreCase)
            &&string.Equals(left.ElementUniqueId,right.ElementUniqueId,StringComparison.OrdinalIgnoreCase)
            &&left.ElementId==right.ElementId;
    }
    private static string Short(string value)=>string.IsNullOrWhiteSpace(value)?"":(value.Length<=12?value:"..."+value.Substring(value.Length-8));
    private void ShowError(Exception ex)=>MessageBox.Show(ex.Message,"BuildAI ACC Issue Return",MessageBoxButton.OK,MessageBoxImage.Error);
    private sealed class IssueRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private string openStatus="Not loaded",primaryStatus="Not checked",secondaryStatus="Not checked",sectionStatus="Not cached",warning="";
        private PreviewRow? preview;
        public AccIssue Issue{get;}
        public PreviewRow? Preview{get=>preview;set{preview=value;NotifyAll();}}
        public ImportState State{get=>Preview?.Issue.ImportState??Issue.ImportState;set{Issue.ImportState=value;NotifyAll();}}
        public bool CanOpenInRevit{get;set;}
        public bool CanImport=>Preview?.Coordinates.IsImportable==true&&(State==ImportState.PreviewReady||State==ImportState.Warning);
        public string OpenStatus{get=>openStatus;set{openStatus=value;Notify(nameof(OpenStatus));}}
        public string PrimaryElementStatus{get=>primaryStatus;set{primaryStatus=value;Notify(nameof(PrimaryElementStatus));}}
        public string SecondaryElementStatus{get=>secondaryStatus;set{secondaryStatus=value;Notify(nameof(SecondaryElementStatus));}}
        public string SectionBoxStatus{get=>sectionStatus;set{sectionStatus=value;Notify(nameof(SectionBoxStatus));}}
        public string Warning{get=>warning;set{warning=value;Notify(nameof(Warning));}}
        public string ImportStatus=>State==ImportState.Warning?"ReadyWithWarning":State.ToString();
        public string PinText=>Issue.HasPushpin?"Yes":"No";
        public string QualityText=>Issue.CoordinateQuality.ToString();
        public string ModelVersionSource=>Preview?.ModelVersionSource??"";
        public string ElementMappingSource=>Preview?.ElementMappingSource??"";
        public string CoordinateSource=>Preview?.CoordinateSource??"";
        public string CoordinateValidation=>Preview?.CoordinateValidation??"";
        public string BlockingReason=>Preview?.BlockingReasonText??"";
        public IssueRow(AccIssue issue){Issue=issue;}
        public void NotifyAll(){foreach(var name in new[]{nameof(ImportStatus),nameof(QualityText),nameof(CanImport),nameof(Warning),nameof(PinText)})Notify(name);}
        private void Notify(string name)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
    }
}






