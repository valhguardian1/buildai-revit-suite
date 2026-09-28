using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BuildAI.AccIssueReturn.Core;

public interface IAccTokenCache { void InvalidateToken(); }

public sealed class WindowsCredentialTokenProvider : IAccAuthProvider,IAccTokenCache
{
    private const string BuildAiCredential="BuildAI:ApiToken";
    private const string StandAloneCredential="BuildAI.AccIssueReturn:ApiToken";
    private readonly string credentialName;
    public IntegrationMode Mode { get; }
    public string BackendUrl { get; }
    private readonly HttpClient tokenHttp=new(){Timeout=TimeSpan.FromSeconds(60)};
    private readonly SemaphoreSlim tokenGate=new(1,1);
    private string? cachedToken;
    private DateTimeOffset cachedTokenExpires=DateTimeOffset.MinValue;

    public WindowsCredentialTokenProvider(bool integrated)
    {
        Mode=integrated?IntegrationMode.IntegratedWithBuildAI:IntegrationMode.StandAlone;
        credentialName=integrated?BuildAiCredential:StandAloneCredential;
        BackendUrl=LoadBackendUrl();
    }

    public static bool DetectBuildAiInstall()
    {
        return AppDomain.CurrentDomain.GetAssemblies().Any(assembly=>
            string.Equals(assembly.GetName().Name,"BuildAI.Plugin5.ClashFormaIntegration",StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var direct=Environment.GetEnvironmentVariable("BUILDAI_APS_ACCESS_TOKEN");
        if(!string.IsNullOrWhiteSpace(direct))return direct;
        if(!string.IsNullOrWhiteSpace(cachedToken)&&cachedTokenExpires>DateTimeOffset.UtcNow.AddMinutes(2))return cachedToken;
        await tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(!string.IsNullOrWhiteSpace(cachedToken)&&cachedTokenExpires>DateTimeOffset.UtcNow.AddMinutes(2))return cachedToken;
            return await RefreshTokenAsync(ct).ConfigureAwait(false);
        }
        finally{tokenGate.Release();}
    }
    public void InvalidateToken(){cachedToken=null;cachedTokenExpires=DateTimeOffset.MinValue;}
    private async Task<string> RefreshTokenAsync(CancellationToken ct)
    {
        var apiToken=ReadCredential(credentialName);
        if(string.IsNullOrWhiteSpace(apiToken)&&Mode==IntegrationMode.StandAlone)apiToken=ReadCredential(BuildAiCredential);
        if(string.IsNullOrWhiteSpace(apiToken))throw new InvalidOperationException("BuildAI API credential is not configured. Sign in with BuildAI or store BuildAI.AccIssueReturn:ApiToken in Windows Credential Manager.");
        using(var request=new HttpRequestMessage(HttpMethod.Get,BackendUrl.TrimEnd('/')+"/api/revit_aps_key"))
        {
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",apiToken);
            using(var response=await tokenHttp.SendAsync(request,ct).ConfigureAwait(false))
            {
                var body=await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if(!response.IsSuccessStatusCode)throw new InvalidOperationException("BuildAI APS token request failed with HTTP "+(int)response.StatusCode+".");
                var json=JObject.Parse(body);var token=(string?)json["access_token"]??(string?)json["accessToken"]??(string?)json["token"];
                if(string.IsNullOrWhiteSpace(token))throw new InvalidOperationException("BuildAI returned no APS access token.");
                cachedToken=token;cachedTokenExpires=ReadExpiry(json,token);return token;
            }
        }
    }
    private static DateTimeOffset ReadExpiry(JObject json,string token)
    {
        var seconds=(double?)json["expires_in"]??(double?)json["expiresIn"];if(seconds.HasValue)return DateTimeOffset.UtcNow.AddSeconds(Math.Max(60,seconds.Value));
        try{var parts=token.Split('.');if(parts.Length>1){var padded=parts[1].Replace('-','+').Replace('_','/');while(padded.Length%4!=0)padded+="=";var payload=JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));var exp=(long?)payload["exp"];if(exp.HasValue)return DateTimeOffset.FromUnixTimeSeconds(exp.Value);}}catch{}
        return DateTimeOffset.UtcNow.AddMinutes(5);
    }

    private static string LoadBackendUrl()
    {
        var configured=Environment.GetEnvironmentVariable("BUILDAI_BACKEND_URL");if(!string.IsNullOrWhiteSpace(configured))return configured.TrimEnd('/');
        try{var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BuildAI","config.json");if(File.Exists(path)){var json=JObject.Parse(File.ReadAllText(path));var value=(string?)json["BaseUrl"]??(string?)json["baseUrl"];if(!string.IsNullOrWhiteSpace(value))return value!.TrimEnd('/');}}catch(Exception ex){AccIssueReturnLog.Event("ACC_SETTINGS_READ_FAILED",new{error=ex.Message});}
        return "https://app.buildai.me";
    }
    private static string? ReadCredential(string target)
    {
        if(!CredRead(target,1,0,out var handle))return null;
        try{var value=Marshal.PtrToStructure<Credential>(handle);if(value.CredentialBlob==IntPtr.Zero||value.CredentialBlobSize==0)return null;var bytes=new byte[value.CredentialBlobSize];Marshal.Copy(value.CredentialBlob,bytes,0,bytes.Length);return Encoding.Unicode.GetString(bytes).TrimEnd('\0');}finally{CredFree(handle);}
    }
    public static string? ReadBuildAiApiToken(out string source)
    {
        var integrated=DetectBuildAiInstall();
        var primary=ReadCredential(integrated?BuildAiCredential:StandAloneCredential)?.Trim();
        if(!string.IsNullOrWhiteSpace(primary)){source="Canonical";return primary;}
        var fallback=ReadCredential(integrated?StandAloneCredential:BuildAiCredential)?.Trim();
        source="Legacy";return string.IsNullOrWhiteSpace(fallback)?null:fallback;
    }
    public static void SaveApiToken(string token,bool integrated)
    {
        if(string.IsNullOrWhiteSpace(token))throw new ArgumentException("API token is empty.",nameof(token));
        var bytes=Encoding.Unicode.GetBytes(token.Trim());var blob=Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes,0,blob,bytes.Length);
            var credential=new Credential{Type=1,TargetName=integrated?BuildAiCredential:StandAloneCredential,
                CredentialBlob=blob,CredentialBlobSize=(uint)bytes.Length,Persist=2,UserName=Environment.UserName};
            if(!CredWrite(ref credential,0))throw new InvalidOperationException("Windows Credential Manager could not save the token.");
        }
        finally{Marshal.FreeHGlobal(blob);}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct Credential{public uint Flags,Type;public string TargetName,Comment;public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;public uint CredentialBlobSize;public IntPtr CredentialBlob;public uint Persist,AttributeCount;public IntPtr Attributes;public string TargetAlias,UserName;}
    [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode,EntryPoint="CredReadW")]private static extern bool CredRead(string target,int type,int reserved,out IntPtr credential);
    [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode,EntryPoint="CredWriteW")]private static extern bool CredWrite(ref Credential credential,uint flags);
    [DllImport("advapi32.dll")]private static extern void CredFree(IntPtr credential);
}

public sealed class AccIssueClient : IAccIssueClient,IDisposable
{
    private readonly IAccAuthProvider auth;private readonly HttpClient http;private readonly string apsBase;private string selectedHubId="";private string selectedProjectRegion="US";
    private readonly Dictionary<CoordinateContextCacheKey,IssueCoordinateContext> contextCache=new();private readonly Dictionary<string,JObject> derivativeResponseCache=new(StringComparer.OrdinalIgnoreCase);private readonly Dictionary<string,string> projectRegions=new(StringComparer.OrdinalIgnoreCase);
    public void ResetContextCache(){contextCache.Clear();derivativeResponseCache.Clear();}
    public AccIssueClient(IAccAuthProvider provider,HttpClient? client=null){auth=provider??throw new ArgumentNullException(nameof(provider));http=client??new HttpClient{Timeout=TimeSpan.FromSeconds(90)};apsBase=(Environment.GetEnvironmentVariable("BUILDAI_APS_BASE_URL")??"https://developer.api.autodesk.com").TrimEnd('/');}
    public async Task<IReadOnlyList<AccHub>> GetHubsAsync(CancellationToken ct){var root=await GetAsync(apsBase+"/project/v1/hubs",ct);return Data(root).Select(x=>new AccHub{Id=S(x,"id"),Name=S(x,"attributes","name")}).Where(x=>x.Id.Length>0).ToList();}
    public async Task<IReadOnlyList<AccProject>> GetProjectsAsync(string hubId,CancellationToken ct){selectedHubId=hubId??"";var root=await GetAsync(apsBase+"/project/v1/hubs/"+E(selectedHubId)+"/projects",ct);var result=Data(root).Select(x=>new AccProject{HubId=selectedHubId,Id=S(x,"id"),Name=S(x,"attributes","name"),Region=ApsRegionResolver.Normalize(S(x,"attributes","extension","data","region"))}).Where(x=>x.Id.Length>0).ToList();foreach(var project in result)projectRegions[project.Id]=project.Region;if(result.Count>0)selectedProjectRegion=result[0].Region;return result;}
    public async Task<IReadOnlyList<AccModel>> GetModelsAsync(string projectId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(selectedHubId))throw new InvalidOperationException("Load ACC projects for the selected hub before loading models.");
        if(projectRegions.TryGetValue(projectId,out var projectRegion))selectedProjectRegion=ApsRegionResolver.Normalize(projectRegion);var models=new List<AccModel>();var queue=new Queue<(string Id,string Path)>();var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var top=await GetPagedObjectsAsync(apsBase+"/project/v1/hubs/"+E(selectedHubId)+"/projects/"+E(projectId)+"/topFolders",ct,"topFolders");
        foreach(var folder in top){var id=S(folder,"id");if(id.Length>0)queue.Enqueue((id,S(folder,"attributes","displayName")));}
        while(queue.Count>0){ct.ThrowIfCancellationRequested();var current=queue.Dequeue();if(!visited.Add(current.Id))continue;IReadOnlyList<JObject> rows;try{rows=await GetPagedObjectsAsync(apsBase+"/data/v1/projects/"+E(projectId)+"/folders/"+E(current.Id)+"/contents",ct,"folderContents");}catch(Exception ex){AccIssueReturnLog.Event("ACC_MODEL_DISCOVERY_FOLDER_WARNING",new{projectId,folderId=current.Id,error=ex.Message});continue;}foreach(var x in rows){var type=S(x,"type");if(type=="folders"){var id=S(x,"id");if(id.Length>0)queue.Enqueue((id,current.Path+"/"+S(x,"attributes","displayName")));continue;}if(type!="items"||!IsSupportedModelItem(x))continue;var itemId=S(x,"id");var name=S(x,"attributes","displayName");var tip=S(x,"relationships","tip","data","id");var lineage=First(S(x,"attributes","extension","data","lineageUrn"),itemId);var resolved=await ResolveItemVersionAsync(projectId,itemId,tip,ct);models.Add(new AccModel{ProjectId=projectId,Id=itemId,Name=name,ItemUrn=itemId,LineageUrn=lineage,FolderId=current.Id,VersionId=resolved.VersionId,VersionUrn=resolved.VersionId,Region=selectedProjectRegion,FolderPath=current.Path,VersionNumber=resolved.VersionNumber,VersionStatus=resolved.Status,IsLatest=resolved.IsLatest,VersionNumberSource=resolved.NumberSource,LatestStatusSource=resolved.LatestSource,VersionUrnSource=resolved.UrnSource,Source=AccModelSource.ProjectCatalog,ResolutionStatus=AccModelResolutionStatus.Resolved});}}
        var refs=await GetIssueModelReferencesAsync(projectId,ct);
        foreach(var reference in refs){var match=models.FirstOrDefault(m=>ApsUrnNormalizer.Same(m.VersionUrn,reference.VersionUrn)||ApsUrnNormalizer.Same(m.LineageUrn,reference.LineageUrn)||ApsUrnNormalizer.Same(m.ItemUrn,reference.LineageUrn));if(match!=null){match.RelatedIssueCount+=reference.Count;if(!string.IsNullOrWhiteSpace(reference.ViewableGuid)&&string.IsNullOrWhiteSpace(match.ViewableGuid))match.ViewableGuid=reference.ViewableGuid;if(!string.IsNullOrWhiteSpace(reference.VersionUrn)&&!ApsUrnNormalizer.Same(match.VersionUrn,reference.VersionUrn)){models.Add(new AccModel{ProjectId=projectId,Id=match.Id,Name=match.Name,ItemUrn=match.ItemUrn,LineageUrn=match.LineageUrn,VersionId=reference.VersionUrn,VersionUrn=reference.VersionUrn,DerivativeUrn=reference.DerivativeUrn,SeedUrn=reference.SeedUrn,ViewableGuid=reference.ViewableGuid,Region=selectedProjectRegion,FolderPath=match.FolderPath,IsLatest=false,VersionStatus=string.IsNullOrWhiteSpace(reference.VersionUrn)?VersionResolutionStatus.Unknown:VersionResolutionStatus.InvalidCandidate,ResolutionEvidence=string.IsNullOrWhiteSpace(reference.VersionUrn)?"MissingIssueVersion":"CandidateVersionNotVerifiedForItem",RelatedIssueCount=reference.Count,Source=AccModelSource.IssueLinkedDocument,ResolutionStatus=AccModelResolutionStatus.PartiallyResolved});}continue;}models.Add(new AccModel{ProjectId=projectId,Id=reference.LineageUrn,Name=string.IsNullOrWhiteSpace(reference.Name)?"Unresolved Issue model":reference.Name,ItemUrn=reference.LineageUrn,LineageUrn=reference.LineageUrn,VersionId=reference.VersionUrn,VersionUrn=reference.VersionUrn,DerivativeUrn=reference.DerivativeUrn,SeedUrn=reference.SeedUrn,ViewableGuid=reference.ViewableGuid,Region=selectedProjectRegion,IsLatest=false,VersionStatus=string.IsNullOrWhiteSpace(reference.VersionUrn)?VersionResolutionStatus.Unknown:VersionResolutionStatus.InvalidCandidate,ResolutionEvidence=string.IsNullOrWhiteSpace(reference.VersionUrn)?"MissingIssueVersion":"CandidateVersionNotVerifiedForItem",RelatedIssueCount=reference.Count,Source=AccModelSource.IssueLinkedDocument,ResolutionStatus=reference.Name.Length>0?AccModelResolutionStatus.PartiallyResolved:AccModelResolutionStatus.Unresolved});}
        var result=models.GroupBy(x=>string.Join("|",x.ItemUrn,x.VersionUrn,x.ViewableGuid),StringComparer.OrdinalIgnoreCase).Select(x=>x.First()).ToList();AccIssueReturnLog.Event("ACC_MODELS_LOADED",new{projectId,count=result.Count,issueReferenced=result.Count(x=>x.Source==AccModelSource.IssueLinkedDocument)});return result;
    }
    public async Task<IReadOnlyList<AccIssue>> GetIssuesAsync(AccProject project,IReadOnlyCollection<AccModel> models,CancellationToken ct,IProgress<string>? progress=null)
    {
        if(project==null)throw new ArgumentNullException(nameof(project));
        var result=new List<AccIssue>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var offset=0;const int limit=100;var pages=0;var apiReceived=0;var duplicates=0;var complete=false;var id=StripProjectPrefix(project.Id);var endpoint=apsBase+"/construction/issues/v1/projects/"+E(id)+"/issues?limit="+limit+"&offset=";
        while(true){ct.ThrowIfCancellationRequested();JObject page;var url=endpoint+offset;try{page=await GetAsync(url,ct);}catch(ApsHttpException ex) when(ex.StatusCode==System.Net.HttpStatusCode.NotFound){endpoint=apsBase+"/issues/v2/containers/"+E(id)+"/issues?limit="+limit+"&offset=";page=await GetAsync(endpoint+offset,ct);}var rows=(page["results"]??page["issues"]??page["data"]??new JArray()).OfType<JObject>().ToList();pages++;apiReceived+=rows.Count;var newUnique=0;foreach(var raw in rows){var issue=ParseIssue(raw,project,new HashSet<string>(StringComparer.OrdinalIgnoreCase));if(string.IsNullOrWhiteSpace(issue.Id)||seen.Add(issue.Id)){if(!string.IsNullOrWhiteSpace(issue.Id)){result.Add(issue);newUnique++;}}else{duplicates++;}}AccIssueReturnLog.Event("ACC_ISSUES_PAGE_RECEIVED",new{projectId=SanitizeUrn(project.Id),offset,received=rows.Count,newUnique,duplicates,totalUnique=result.Count});progress?.Report("ACC_ISSUES_PAGE_RECEIVED | offset="+offset+" | received="+rows.Count+" | newUnique="+newUnique+" | totalUnique="+result.Count);if(rows.Count==0){complete=true;break;}var next=S(page,"links","next","href");if(string.IsNullOrWhiteSpace(next)&&rows.Count<limit){complete=true;break;}offset+=rows.Count;}
        var resolved=result.Count(x=>!string.IsNullOrWhiteSpace(x.ModelUrn)||!string.IsNullOrWhiteSpace(x.ViewableGuid));var noReference=result.Count-resolved;AccIssueReturnLog.Event("ACC_ISSUES_CLASSIFIED",new{total=result.Count,resolvedModel=resolved,partialModel=0,unresolvedModel=noReference,ambiguousModel=0,noModelReference=noReference});AccIssueReturnLog.Event("ACC_LOAD_ISSUES_FINISHED",new{projectId=SanitizeUrn(project.Id),apiReceived,uniqueLoaded=result.Count,visibleAfterFilters=result.Count,withObjectSet=result.Count(x=>x.HasObjectSet),withoutObjectSet=result.Count(x=>!x.HasObjectSet),pages,complete,duplicateIssuesRemoved=duplicates,loadFailed=false});return result;
    }
    private async Task<(string VersionId,int? VersionNumber,VersionResolutionStatus Status,bool IsLatest,string NumberSource,string LatestSource,string UrnSource)> ResolveItemVersionAsync(string projectId,string itemId,string tipId,CancellationToken ct)
    {
        AccIssueReturnLog.Event("ACC_ITEM_VERSION_RESOLUTION_STARTED",new{projectId=SanitizeUrn(projectId),itemId=SanitizeUrn(itemId)});
        try
        {
            var rows=await GetPagedObjectsAsync(apsBase+"/data/v1/projects/"+E(projectId)+"/items/"+E(itemId)+"/versions?limit=100",ct,"itemVersions");
            JObject? selected=null;
            if(!string.IsNullOrWhiteSpace(tipId)) selected=rows.FirstOrDefault(v=>string.Equals(S(v,"id"),tipId,StringComparison.OrdinalIgnoreCase));
            selected ??=rows.FirstOrDefault(v=>B(v,"attributes","extension","data","isTip")||B(v,"attributes","isTip")||B(v,"isTip"));
            if(selected==null && rows.Count==1) selected=rows[0];
            if(selected==null){AccIssueReturnLog.Event("ACC_ITEM_VERSION_UNRESOLVED",new{itemId=SanitizeUrn(itemId),reason="NoTipOrVersion"});return(tipId??"",null,VersionResolutionStatus.Unknown,false,"","","item/versions");}
            var id=S(selected,"id");var number=ParseVersionNumber(selected);var isLatest=(!string.IsNullOrWhiteSpace(tipId)&&string.Equals(id,tipId,StringComparison.OrdinalIgnoreCase))||B(selected,"attributes","extension","data","isTip")||B(selected,"attributes","isTip")||B(selected,"isTip");var status=number.HasValue?(isLatest?VersionResolutionStatus.LatestResolved:VersionResolutionStatus.Historical):VersionResolutionStatus.Unknown;
            AccIssueReturnLog.Event("ACC_ITEM_VERSION_RESOLVED",new{itemId=SanitizeUrn(itemId),versionId=SanitizeUrn(id),versionNumber=number,isLatest,source="versionsEndpoint"});return(id,number,status,isLatest,"versionsEndpoint",isLatest?"tip/versionsEndpoint":"versionsEndpoint","versionsEndpoint");
        }
        catch(ApsHttpException ex) when((int)ex.StatusCode==401||(int)ex.StatusCode==403){AccIssueReturnLog.Event("ACC_ITEM_VERSION_UNRESOLVED",new{itemId=SanitizeUrn(itemId),reason="NoAccess",status=(int)ex.StatusCode});return(tipId??"",null,VersionResolutionStatus.NoAccess,false,"","","versionsEndpoint");}
        catch(Exception ex){AccIssueReturnLog.Event("ACC_ITEM_VERSION_UNRESOLVED",new{itemId=SanitizeUrn(itemId),reason=ex.GetType().Name});return(tipId??"",null,VersionResolutionStatus.Unknown,false,"","","versionsEndpoint");}
    }
    public static int? ParseVersionNumber(JObject x)
    {
        foreach(var token in new[]{x["attributes"]?["versionNumber"],x["attributes"]?["version_number"],x["attributes"]?["extension"]?["data"]?["versionNumber"],x["attributes"]?["extension"]?["data"]?["version_number"],x["versionNumber"]}) if(token!=null && int.TryParse(token.ToString(),out var n) && n>0) return n;
        return null;
    }
    private static bool B(JObject x,params string[] path){JToken? t=x;foreach(var p in path){t=t?[p];if(t==null)return false;}return bool.TryParse(t.ToString(),out var b)&&b;}    private static bool IsSupportedModelItem(JObject x){var name=S(x,"attributes","displayName");var ext=First(S(x,"attributes","extension","data","fileType"),S(x,"attributes","extension","type"),System.IO.Path.GetExtension(name));var blocked=new[]{"pdf","doc","docx","xls","xlsx","txt","jpg","jpeg","png","zip"};return !string.IsNullOrWhiteSpace(name)&&!blocked.Contains(ext.TrimStart('.'),StringComparer.OrdinalIgnoreCase);}
    private async Task<IReadOnlyList<JObject>> GetPagedObjectsAsync(string url,CancellationToken ct,string operation){var result=new List<JObject>();var next=url;while(!string.IsNullOrWhiteSpace(next)){ct.ThrowIfCancellationRequested();var page=await GetAsync(next,ct).ConfigureAwait(false);result.AddRange(Data(page));next=S(page,"links","next","href");AccIssueReturnLog.Event("ACC_MODEL_DISCOVERY_PAGE",new{operation,endpoint=operation,count=Data(page).Count()});}return result;}
    private sealed class IssueModelReference{public string Name="";public string LineageUrn="";public string VersionUrn="";public string DerivativeUrn="";public string SeedUrn="";public string ViewableGuid="";public int Count;}
    private async Task<IReadOnlyList<IssueModelReference>> GetIssueModelReferencesAsync(string projectId,CancellationToken ct){var result=new Dictionary<string,IssueModelReference>(StringComparer.OrdinalIgnoreCase);var id=StripProjectPrefix(projectId);var next=apsBase+"/construction/issues/v1/projects/"+E(id)+"/issues?limit=100&offset=0";while(!string.IsNullOrWhiteSpace(next)){JObject page;try{page=await GetAsync(next,ct);}catch(ApsHttpException ex) when(ex.StatusCode==System.Net.HttpStatusCode.NotFound){page=await GetAsync(apsBase+"/issues/v2/containers/"+E(id)+"/issues?limit=100&offset=0",ct);}foreach(var issue in (page["results"]??page["issues"]??page["data"]??new JArray()).OfType<JObject>()){foreach(var linked in (issue["linkedDocuments"] as JArray)?.OfType<JObject>()??Enumerable.Empty<JObject>()){var details=linked["details"] as JObject;var view=details?["viewable"] as JObject;var version=First(S(details,"versionUrn"),S(linked,"versionUrn"),S(view,"versionUrn"));var lineage=First(S(details,"modelUrn"),S(linked,"urn"),S(view,"urn"));var seed=First(S(details,"seedUrn"),S(view,"urn"));var viewable=S(view,"guid");var key=string.Join("|",lineage,version,viewable);if(!result.TryGetValue(key,out var r)){r=new IssueModelReference{Name=First(S(details,"modelName"),S(linked,"name")),LineageUrn=lineage,VersionUrn=version,SeedUrn=seed,ViewableGuid=viewable,Count=0};result[key]=r;}r.Count++;}}next=S(page,"links","next","href");}return result.Values.ToList();}    public async Task<IssueCoordinateContext> GetContextAsync(AccIssue issue,AccModel model,CancellationToken ct)
    {
        if(issue==null)throw new ArgumentNullException(nameof(issue));if(model==null)throw new ArgumentNullException(nameof(model));
        var context=new IssueCoordinateContext{ModelUrn=First(model.ItemUrn,issue.ModelUrn),VersionUrn=First(model.VersionUrn,issue.VersionUrn),ViewableGuid=First(issue.ViewableGuid,model.ViewableGuid),ViewableUrn=First(issue.SeedUrn,model.SeedUrn),Region=ApsRegionResolver.Normalize(model.Region)};
        var raw=string.IsNullOrWhiteSpace(issue.RawJson)?new JObject():JObject.Parse(issue.RawJson);
        var details=SelectLinkedDetails(raw,context.ViewableGuid);
        var viewerState=details?["viewerState"] as JObject??details?["viewState"] as JObject;
        var rawUnits=First(S(viewerState,"units"),S(details,"units"));
        ApplyUnit(context,rawUnits,string.IsNullOrWhiteSpace(rawUnits)?"":"Issue/viewerState");
        var viewerOffset=Vector(viewerState?["globalOffset"]);var detailsOffset=Vector(details?["globalOffset"]);var rawOffset=viewerOffset??detailsOffset;
        if(rawOffset!=null&&MatrixMath.IsFinite(rawOffset))
        {
            context.GlobalOffset=rawOffset;context.GlobalOffsetSource=viewerOffset!=null?"Issue.linkedDocuments.details.viewerState.globalOffset":"Issue.linkedDocuments.details.globalOffset";context.ContextSources.Add(context.GlobalOffsetSource);
            TrySetViewerFeetContract(context,details,viewerState,rawOffset);
        }
        var rawPlacement=ReadMatrix(viewerState?["placementTransform"]??details?["placementTransform"],MatrixSourceFormat.ViewerColumnMajor4x4);
        if(rawPlacement!=null){context.PlacementTransform=rawPlacement;context.PlacementDirection=CoordinateTransformDirection.Unverified;context.MatrixFormat=MatrixSourceFormat.ViewerColumnMajor4x4;context.PlacementTransformSource="Issue.viewerState.placementTransform (direction unverified)";context.ContextSources.Add(context.PlacementTransformSource);AccIssueReturnLog.Event("ACC_MATRIX_CONTEXT",new{issueId=issue.Id,sourceFormat="Viewer 16 column-major",direction="Unverified",determinant=MatrixMath.LinearDeterminant(rawPlacement)});}

        var preliminaryKey=BuildCacheKey(issue.ProjectId,context.Region,context.VersionUrn,context.ViewableGuid);
        IssueCoordinateContext? cached=null;
        var contextHit=preliminaryKey.ViewableGuid.Length>0&&contextCache.TryGetValue(preliminaryKey,out cached);
        AccIssueReturnLog.Event("ACC_CONTEXT_CACHE",new{source="CoordinateContext",cacheHit=contextHit,projectId=SanitizeUrn(issue.ProjectId),versionUrn=SanitizeUrn(context.VersionUrn),viewableGuidPresent=context.ViewableGuid.Length>0});
        if(contextHit)MergeMissing(context,cached!);
        var derivative=NormalizeDerivativeUrn(First(model.DerivativeUrn,context.VersionUrn,context.ViewableUrn));
        if(!string.IsNullOrWhiteSpace(derivative))
        {
            var metadata=await TryGetAsync(DerivativeUrl(context.Region,derivative,"metadata"),ct,"metadata").ConfigureAwait(false);
            var manifest=await TryGetAsync(DerivativeUrl(context.Region,derivative,"manifest"),ct,"manifest").ConfigureAwait(false);
            var manifestViewable=FindManifestViewable(manifest,context.ViewableGuid);
            if(context.ViewableGuid.Length==0&&manifestViewable!=null)context.ViewableGuid=First(S(manifestViewable,"guid"),S(manifestViewable,"viewableID"));
            var selected=SelectMetadataViewable(metadata,manifestViewable,context.ViewableGuid);
            if(selected!=null)
            {
                context.ModelPropertiesGuid=S(selected,"guid");context.ViewableSource="Issue viewable mapped through manifest to Model Derivative metadata";context.ContextSources.Add(context.ViewableSource);
                if(context.UnitStatus!=UnitParseStatus.Known)ApplyUnit(context,First(S(selected,"units"),S(selected,"unit")),"Model Derivative metadata");
            }
            var aecResource=FindAecModelDataResource(manifest,context.ViewableGuid);
            var resourceUrn=S(aecResource,"urn");
            if(resourceUrn.Length>0)
            {
                var aec=await TryGetAsync(DerivativeUrl(context.Region,derivative,"manifest/"+resourceUrn),ct,"AECModelData").ConfigureAwait(false);
                var values=ReadNumbers(aec?["refPointTransformation"]);
                if(values?.Length==12)
                {
                    try
                    {
                        var aecMatrix=MatrixFormats.Parse(values,MatrixSourceFormat.AecRefPointColumnMajor4x3);
                        if(string.IsNullOrWhiteSpace(context.PlacementTransformSource)){context.PlacementTransform=aecMatrix;context.PlacementDirection=CoordinateTransformDirection.Unverified;context.MatrixFormat=MatrixSourceFormat.AecRefPointColumnMajor4x3;context.PlacementTransformSource="Autodesk.AEC.ModelData.refPointTransformation (direction unverified)";}
                        context.ContextSources.Add("Autodesk.AEC.ModelData");AccIssueReturnLog.Event("ACC_MATRIX_CONTEXT",new{issueId=issue.Id,sourceFormat="AEC 12 column-major",direction="Unverified",determinant=MatrixMath.LinearDeterminant(aecMatrix)});
                    }catch(FormatException ex){context.IncompleteReason=ex.Message;}
                }
            }
            if(context.ModelPropertiesGuid.Length>0&&issue.ObjectSet.FirstOrDefault(x=>x.DbId.HasValue)?.DbId is int objectId)
            {
                var properties=await TryGetAsync(DerivativeUrl(context.Region,derivative,"metadata/"+context.ModelPropertiesGuid+"/properties?objectid="+objectId),ct,"properties").ConfigureAwait(false);
                if(context.UnitStatus!=UnitParseStatus.Known)ApplyUnit(context,DeepString(properties??new JObject(),"units"),"Model properties");
            }
        }
        if(context.Contract==CoordinateContract.Unverified&&rawOffset!=null)TrySetViewerFeetContract(context,details,viewerState,rawOffset);
        if(context.Contract==CoordinateContract.Unverified)context.IncompleteReason=First(context.IncompleteReason,"Issue viewer coordinate contract is unverified. Export a sanitized diagnostic or recover from a matched Revit element.");
        else if(context.UnitStatus==UnitParseStatus.Unknown)context.IncompleteReason="Unknown unit '"+context.SourceUnits+"'; recover from a matched Revit element or export a sanitized diagnostic.";
        else if(context.UnitStatus==UnitParseStatus.Missing)context.IncompleteReason="Coordinate unit is missing; recover from a matched Revit element or export a sanitized diagnostic.";
        var key=BuildCacheKey(issue.ProjectId,context.Region,context.VersionUrn,context.ViewableGuid);if(key.ViewableGuid.Length>0)contextCache[key]=CloneContext(context);
        AccIssueReturnLog.Event("ACC_COORDINATE_CONTEXT",new{issueId=issue.Id,versionUrn=SanitizeUrn(context.VersionUrn),context.ViewableGuid,rawUnits,normalizedUnits=context.Unit?.ToString()??context.UnitStatus.ToString(),pushpinSpace=context.PushpinSpace.ToString(),globalOffsetSpace=context.GlobalOffsetSpace.ToString(),placementDirection=context.PlacementDirection.ToString(),matrixFormat=context.MatrixFormat.ToString(),contextSources=context.ContextSources,linkTransformApplied=false});
        return context;
    }
    public static CoordinateContextCacheKey BuildCacheKey(string projectId,string region,string versionUrn,string viewableGuid)=>new(projectId,region,versionUrn,viewableGuid);
    private string DerivativeUrl(string region,string derivative,string path){var url=ApsRegionResolver.BuildModelDerivativeUrl(apsBase,region,derivative,path);AccIssueReturnLog.Event("ACC_HTTP",new{operation=path,region=ApsRegionResolver.Normalize(region),endpoint=path});return url;}
    private async Task<JObject> GetAsync(string url,CancellationToken ct)
    {
        var refreshed=false;
        for(var attempt=1;attempt<=3;attempt++)
        {
            var token=await auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
            using var request=new HttpRequestMessage(HttpMethod.Get,url);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
            using var response=await http.SendAsync(request,ct).ConfigureAwait(false);var body=await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if(response.StatusCode==System.Net.HttpStatusCode.Unauthorized&&!refreshed){(auth as IAccTokenCache)?.InvalidateToken();refreshed=true;continue;}
            if(response.IsSuccessStatusCode)return JObject.Parse(string.IsNullOrWhiteSpace(body)?"{}":body);
            var retryable=(int)response.StatusCode==429||(int)response.StatusCode>=500;
            if(retryable&&attempt<3){var delay=response.Headers.RetryAfter?.Delta??TimeSpan.FromMilliseconds(250*Math.Pow(2,attempt-1));await Task.Delay(delay>TimeSpan.FromSeconds(10)?TimeSpan.FromSeconds(10):delay,ct);continue;}
            var corr=response.Headers.TryGetValues("x-request-id",out var ids)?ids.FirstOrDefault():null;throw new ApsHttpException(url,response.StatusCode,SafeExcerpt(body),corr??Guid.NewGuid().ToString("N").Substring(0,12),retryable,response.Headers.RetryAfter?.Delta);
        }
        throw new ApsHttpException(url,System.Net.HttpStatusCode.ServiceUnavailable,"retry limit","retry-limit",true,null);
    }
    private static string SafeExcerpt(string body){if(string.IsNullOrWhiteSpace(body))return "";var s=body.Replace("access_token","redacted").Replace("refresh_token","redacted");return s.Length>240?s.Substring(0,240):s;}
    private async Task<JObject?> TryGetAsync(string url,CancellationToken ct,string source){
        if(derivativeResponseCache.TryGetValue(url,out var cached)){AccIssueReturnLog.Event("ACC_CONTEXT_CACHE",new{source,cacheHit=true});return (JObject)cached.DeepClone();}
        AccIssueReturnLog.Event("ACC_CONTEXT_CACHE",new{source,cacheHit=false});
        try{var result=await GetAsync(url,ct).ConfigureAwait(false);derivativeResponseCache[url]=(JObject)result.DeepClone();return result;}
        catch(OperationCanceledException){throw;}catch(Exception ex){AccIssueReturnLog.Event("ACC_CONTEXT_SOURCE_UNAVAILABLE",new{source,error=ex.Message});return null;}}
    private static AccIssue ParseIssue(JObject x,AccProject p,ISet<string> selected){var docs=(x["linkedDocuments"] as JArray)?.OfType<JObject>().ToList()??new List<JObject>();var matches=docs.Where(d=>LinkedDocumentIdentifiers(d).Any(id=>selected.Contains(id)||selected.Any(s=>ApsUrnNormalizer.Same(s,id)))).ToList();var linked=selected.Count>0?(matches.Count==1?matches[0]:null):(docs.Count==1?docs[0]:null);var details=linked?["details"] as JObject;var viewable=details?["viewable"] as JObject;var pos=Vector(details?["position"]);var objects=ParseObjectRefs(details?["objectSet"],S(details,"modelUrn"));if(objects.Count==0&&I(details,"objectId").HasValue)objects.Add(new AccObjectRef{ModelUrn=S(details,"modelUrn"),DbId=I(details,"objectId"),DbIds=new List<int>{I(details,"objectId")!.Value}});var version=First(S(details,"versionUrn"),S(linked,"versionUrn"),S(viewable,"versionUrn"));var lineage=First(S(details,"lineageUrn"),S(details,"modelUrn"),S(linked,"lineageUrn"));var model=First(S(linked,"urn"),S(details,"modelUrn"),S(viewable,"urn"));return new AccIssue{HubId=p.HubId,ProjectId=p.Id,ContainerId=StripProjectPrefix(p.Id),Id=S(x,"id"),DisplayId=I(x,"displayId"),Title=S(x,"title"),Description=S(x,"description"),Status=S(x,"status"),Type=First(S(x,"issueTypeId"),S(x,"type")),AssignedUserId=S(x,"assignedTo"),AssignedUserName=S(x,"assignedToName"),Author=S(x,"createdBy"),DueDateUtc=D(x,"dueDate"),CreatedAtUtc=D(x,"createdAt"),Url=S(x,"webUrl"),ScreenshotUrl=S(x,"snapshotUrn"),ModelUrn=model,LineageUrn=lineage,DerivativeUrn=First(S(details,"derivativeUrn"),S(linked,"derivativeUrn"),S(viewable,"derivativeUrn")),VersionUrn=version,SeedUrn=First(S(details,"seedUrn"),S(viewable,"seedUrn"),S(viewable,"urn")),ViewableGuid=S(viewable,"guid"),PushpinPosition=pos,Camera=ParseCamera(details),ObjectSet=objects,RawJson=x.ToString(Newtonsoft.Json.Formatting.None)};}
    internal static List<AccObjectRef> ParseObjectRefs(JToken? token,string modelUrn)
    {
        var result=new List<AccObjectRef>();foreach(var o in (token as JArray)?.OfType<JObject>()??Enumerable.Empty<JObject>())
        {
            var externalIds=ReadStrings(o["externalId"]);if(externalIds.Count==0)externalIds=ReadStrings(o["externalIds"]);var dbIds=ReadInts(o["id"]);dbIds.AddRange(ReadInts(o["dbId"]));dbIds.AddRange(ReadInts(o["objectId"]));dbIds.AddRange(ReadInts(o["details"]?["objectId"]));dbIds=dbIds.Distinct().ToList();
            var rawExternal=externalIds.FirstOrDefault()??"";var composite=CompositeExternalId.Parse(rawExternal);result.Add(new AccObjectRef{ModelUrn=First(S(o,"modelUrn"),modelUrn),ExternalId=rawExternal,ExternalIds=externalIds,DbId=dbIds.FirstOrDefault(),DbIds=dbIds,Composite=composite,RevitUniqueId=composite.IsComposite?composite.ElementUniqueId:rawExternal,LinkDocumentUniqueId=composite.IsComposite?composite.LinkDocumentUniqueId:""});
        }return result;
    }
    private static List<string> ReadStrings(JToken? token){if(token==null)return new List<string>();if(token is JArray a)return a.Values<string>().Where(x=>!string.IsNullOrWhiteSpace(x)).ToList();var value=(string?)token;return string.IsNullOrWhiteSpace(value)?new List<string>():new List<string>{value};}
    private static List<int> ReadInts(JToken? token){if(token==null)return new List<int>();if(token is JArray a)return a.SelectMany(ReadInts).Distinct().ToList();if(token.Type==JTokenType.Integer)return new List<int>{(int)token};return int.TryParse((string?)token,out var n)?new List<int>{n}:new List<int>();}
    private static IEnumerable<string> LinkedDocumentIdentifiers(JObject linked){var details=linked["details"] as JObject;var viewable=details?["viewable"] as JObject;return new[]{S(linked,"urn"),S(linked,"versionUrn"),S(linked,"lineageUrn"),S(linked,"derivativeUrn"),S(details,"modelUrn"),S(details,"lineageUrn"),S(details,"versionUrn"),S(details,"seedUrn"),S(details,"derivativeUrn"),S(viewable,"urn"),S(viewable,"versionUrn"),S(viewable,"derivativeUrn"),S(viewable,"guid")}.Where(x=>x.Length>0);}
    private static AccCamera? ParseCamera(JObject? d){var state=d?["viewerState"] as JObject??d?["viewState"] as JObject;var c=state?["viewport"] as JObject??d?["camera"] as JObject;if(c==null)return null;var eye=DeepVector(c,"eye");var target=DeepVector(c,"target");var up=DeepVector(c,"up");return eye==null||target==null||up==null?null:new AccCamera{Eye=eye,Target=target,Up=up,FieldOfView=DeepDouble(c,"fieldOfView"),IsPerspective=!((bool?)c["isOrthographic"]??string.Equals(DeepString(c,"projection"),"orthographic",StringComparison.OrdinalIgnoreCase))};}
    private static IEnumerable<JObject> Data(JObject x)=>(x["data"] as JArray)?.OfType<JObject>()??Enumerable.Empty<JObject>();private static string E(string x)=>Uri.EscapeDataString(x??"");private static string StripProjectPrefix(string x)=>x!=null&&x.StartsWith("b.",StringComparison.OrdinalIgnoreCase)?x.Substring(2):x??"";
    private static string S(JToken? x,params string[] path){foreach(var p in path)x=x?[p];return (string?)x??"";}private static int? I(JToken? x,string name){return x?[name]?.Type==JTokenType.Integer?(int?)x[name]:int.TryParse((string?)x?[name],out var n)?n:null;}private static DateTime? D(JToken? x,string name)=>DateTime.TryParse((string?)x?[name],out var d)?d.ToUniversalTime():(DateTime?)null;private static string First(params string[] xs)=>xs.FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x))??"";
    private static double[]? Vector(JToken? x){try{if(x is JArray a&&a.Count>=3)return new[]{(double)a[0],(double)a[1],(double)a[2]};if(x is JObject o)return new[]{(double?)o["x"]??double.NaN,(double?)o["y"]??double.NaN,(double?)o["z"]??double.NaN};}catch{}return null;}
    private static IEnumerable<JProperty> Properties(JToken x)=>x is JContainer c?c.DescendantsAndSelf().OfType<JProperty>():Enumerable.Empty<JProperty>();
    private static double[]? DeepVector(JToken x,string name)=>Vector(Properties(x).FirstOrDefault(p=>string.Equals(p.Name,name,StringComparison.OrdinalIgnoreCase))?.Value);
    private static string DeepString(JToken x,string name)=>(string?)Properties(x).FirstOrDefault(p=>string.Equals(p.Name,name,StringComparison.OrdinalIgnoreCase))?.Value??"";
    private static double DeepDouble(JToken x,string name)=>(double?)Properties(x).FirstOrDefault(p=>string.Equals(p.Name,name,StringComparison.OrdinalIgnoreCase))?.Value??0;
    internal static JObject? SelectLinkedDetails(JObject raw,string viewableGuid){var details=((raw["linkedDocuments"] as JArray)?.OfType<JObject>()??Enumerable.Empty<JObject>()).Select(x=>x["details"] as JObject).Where(x=>x!=null).Cast<JObject>().ToList();if(viewableGuid.Length>0)return details.FirstOrDefault(x=>string.Equals(S(x["viewable"],"guid"),viewableGuid,StringComparison.OrdinalIgnoreCase));return details.Count==1?details[0]:null;}
    internal static bool TrySetViewerFeetContract(IssueCoordinateContext context,JObject? details,JObject? viewerState,double[] offset)
    {
        if(!MatrixMath.IsFinite(offset)||string.IsNullOrWhiteSpace(context.ViewableGuid))return false;
        context.ViewerCoordinateUnit=LengthUnit.Foot;context.Contract=CoordinateContract.ViewerLocalFeetPlusGlobalOffset;context.PushpinSpace=CoordinateSpace.ViewerLocal;context.GlobalOffsetSpace=CoordinateSpace.RevitLinkInternalFeet;context.CameraContract=CameraPointContract.RevitLinkInternalFeet;context.ContractDecision=new CoordinateContractDecision{Contract=context.Contract,Confidence=details!=null&&viewerState!=null?ContractConfidence.Confirmed:ContractConfidence.Strong,Source="BuildAI viewerState contract",IsAutomaticImportAllowed=true};context.ContractDecision.Evidence.Add("finite globalOffset");context.ContractDecision.Evidence.Add("same viewableGuid");
        context.ContextSources.Add("Viewer contract: position/globalOffset are Revit feet; camera target is diagnostic only");return true;
    }
    private static double[]? ReadNumbers(JToken? token){if(token is not JArray a)return null;try{return a.Select(x=>(double)x).ToArray();}catch{return null;}}
    private static Matrix4? ReadMatrix(JToken? token,MatrixSourceFormat format){var values=ReadNumbers(token);if(values==null)return null;try{return MatrixFormats.Parse(values,format);}catch(FormatException){return null;}}
    internal static JObject? FindAecModelDataResource(JObject? manifest,string viewableGuid)
    {
        if(manifest==null)return null;var nodes=manifest.DescendantsAndSelf().OfType<JObject>().ToList();
        var selected=nodes.FirstOrDefault(x=>viewableGuid.Length>0&&(string.Equals(S(x,"guid"),viewableGuid,StringComparison.OrdinalIgnoreCase)||string.Equals(S(x,"viewableID"),viewableGuid,StringComparison.OrdinalIgnoreCase)));
        var scoped=selected?.DescendantsAndSelf().OfType<JObject>().FirstOrDefault(IsAecResource);if(scoped!=null)return scoped;
        if(viewableGuid.Length>0)return null;var all=nodes.Where(IsAecResource).ToList();return all.Count==1?all[0]:null;
    }
    private static JObject? FindManifestViewable(JObject? manifest,string viewableGuid)
    {
        if(manifest==null)return null;var geometries=manifest.DescendantsAndSelf().OfType<JObject>().Where(x=>string.Equals(S(x,"type"),"geometry",StringComparison.OrdinalIgnoreCase)).ToList();
        if(viewableGuid.Length>0)return geometries.FirstOrDefault(x=>string.Equals(S(x,"guid"),viewableGuid,StringComparison.OrdinalIgnoreCase)||string.Equals(S(x,"viewableID"),viewableGuid,StringComparison.OrdinalIgnoreCase));
        return geometries.Count==1?geometries[0]:null;
    }
    internal static JObject? SelectMetadataViewable(JObject? metadata,JObject? manifestViewable,string viewableGuid)
    {
        var rows=(metadata?["data"]?["metadata"] as JArray)?.OfType<JObject>().ToList()??new List<JObject>();if(rows.Count==0)return null;
        var exact=rows.FirstOrDefault(x=>string.Equals(S(x,"guid"),viewableGuid,StringComparison.OrdinalIgnoreCase));if(exact!=null)return exact;
        if(manifestViewable!=null){var graphics=new HashSet<string>(manifestViewable.DescendantsAndSelf().OfType<JObject>().Where(x=>string.Equals(S(x,"role"),"graphics",StringComparison.OrdinalIgnoreCase)).Select(x=>S(x,"guid")).Where(x=>x.Length>0),StringComparer.OrdinalIgnoreCase);var mapped=rows.FirstOrDefault(x=>graphics.Contains(S(x,"guid")));if(mapped!=null)return mapped;}
        return viewableGuid.Length==0&&rows.Count==1?rows[0]:null;
    }
    private static bool IsAecResource(JObject x)=>string.Equals(S(x,"role"),"Autodesk.AEC.ModelData",StringComparison.OrdinalIgnoreCase);
    private static void ApplyUnit(IssueCoordinateContext context,string raw,string source)
    {
        var parsed=LengthUnits.Parse(raw);if(context.UnitStatus!=UnitParseStatus.Missing)return;
        context.SourceUnits=parsed.OriginalValue;context.UnitStatus=parsed.Status;context.Unit=parsed.Unit;context.ModelDisplayUnit=parsed.Unit;context.MetresPerUnit=parsed.MetresPerUnit;context.UnitsSource=source;
        if(source.Length>0&&!context.ContextSources.Contains(source))context.ContextSources.Add(source);
    }
    internal static void MergeMissing(IssueCoordinateContext target,IssueCoordinateContext source)
    {
        if(target.UnitStatus==UnitParseStatus.Missing&&source.UnitStatus==UnitParseStatus.Known){target.SourceUnits=source.SourceUnits;target.UnitStatus=source.UnitStatus;target.Unit=source.Unit;target.MetresPerUnit=source.MetresPerUnit;target.UnitsSource=source.UnitsSource;}
        if(!MatrixMath.IsFinite(target.GlobalOffset)&&MatrixMath.IsFinite(source.GlobalOffset)){target.GlobalOffset=(double[])source.GlobalOffset.Clone();target.GlobalOffsetSource=source.GlobalOffsetSource;}
        if(string.IsNullOrWhiteSpace(target.PlacementTransformSource)){target.PlacementTransform=new Matrix4{M=(double[])source.PlacementTransform.M.Clone()};target.PlacementDirection=source.PlacementDirection;target.MatrixFormat=source.MatrixFormat;target.PlacementTransformSource=source.PlacementTransformSource;}
        if(target.Contract==CoordinateContract.Unverified){target.Contract=source.Contract;target.PushpinSpace=source.PushpinSpace;target.GlobalOffsetSpace=source.GlobalOffsetSpace;target.CameraContract=source.CameraContract;}
        if(string.IsNullOrWhiteSpace(target.ModelPropertiesGuid))target.ModelPropertiesGuid=source.ModelPropertiesGuid;
        if(string.IsNullOrWhiteSpace(target.ViewableSource))target.ViewableSource=source.ViewableSource;
        foreach(var item in source.ContextSources)if(!target.ContextSources.Contains(item))target.ContextSources.Add(item);
    }
    internal static IssueCoordinateContext CloneContext(IssueCoordinateContext x)
    {
        var issueUnit=x.UnitsSource.StartsWith("Issue/",StringComparison.OrdinalIgnoreCase);var issueOffset=x.GlobalOffsetSource.StartsWith("Issue.",StringComparison.OrdinalIgnoreCase);var issuePlacement=x.PlacementTransformSource.StartsWith("Issue.",StringComparison.OrdinalIgnoreCase);
        return new IssueCoordinateContext{ModelUrn=x.ModelUrn,VersionUrn=x.VersionUrn,ViewableGuid=x.ViewableGuid,ViewableUrn=x.ViewableUrn,ModelPropertiesGuid=x.ModelPropertiesGuid,SourceUnits=issueUnit?"":x.SourceUnits,UnitStatus=issueUnit?UnitParseStatus.Missing:x.UnitStatus,Unit=issueUnit?null:x.Unit,MetresPerUnit=issueUnit?null:x.MetresPerUnit,GlobalOffset=issueOffset?new[]{double.NaN,double.NaN,double.NaN}:(double[])x.GlobalOffset.Clone(),PlacementTransform=issuePlacement?Matrix4.Identity():new Matrix4{M=(double[])x.PlacementTransform.M.Clone()},LinkToHost=Matrix4.Identity(),Contract=CoordinateContract.Unverified,PushpinSpace=CoordinateSpace.Unverified,GlobalOffsetSpace=CoordinateSpace.Unverified,CameraContract=CameraPointContract.Unverified,PlacementDirection=issuePlacement?CoordinateTransformDirection.Unverified:x.PlacementDirection,MatrixFormat=issuePlacement?MatrixSourceFormat.None:x.MatrixFormat,UnitsSource=issueUnit?"":x.UnitsSource,GlobalOffsetSource=issueOffset?"":x.GlobalOffsetSource,PlacementTransformSource=issuePlacement?"":x.PlacementTransformSource,ViewableSource=x.ViewableSource,IncompleteReason="",ContextSources=x.ContextSources.Where(s=>!s.StartsWith("Issue",StringComparison.OrdinalIgnoreCase)).ToList()};
    }
    private static string SanitizeUrn(string urn){if(string.IsNullOrWhiteSpace(urn))return "";var tail=urn.Length<=12?urn:urn.Substring(urn.Length-12);return "..."+tail.Replace("?","_").Replace("&","_");}
    private static string NormalizeDerivativeUrn(string urn){if(!urn.StartsWith("urn:",StringComparison.OrdinalIgnoreCase))return urn.TrimEnd('=');return Convert.ToBase64String(Encoding.UTF8.GetBytes(urn)).TrimEnd('=').Replace('+','-').Replace('/','_');}
    public void Dispose()=>http.Dispose();
}





