using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BuildAI.AccIssueReturn.Core;

public enum BuildAiMappingStatus { NotRequested, NotFound, Exact, RecoveredByModelUid, Partial, Stale, Conflict, Ambiguous, Invalid }
public sealed class BuildAiRevitElementReference { [JsonProperty("category")] public string Category {get;set;}=""; [JsonProperty("discipline")] public string Discipline {get;set;}=""; [JsonProperty("model_uid")] public string ModelUid {get;set;}=""; [JsonProperty("link_instance_uid")] public string LinkInstanceUid {get;set;}=""; [JsonProperty("element_unique_id")] public string ElementUniqueId {get;set;}=""; [JsonProperty("element_id")] public int? ElementId {get;set;} }
public sealed class BuildAiResultData { [JsonProperty("location_x")] public double? LocationX {get;set;} [JsonProperty("location_y")] public double? LocationY {get;set;} [JsonProperty("location_z")] public double? LocationZ {get;set;} [JsonProperty("coordinate_contract")] public string CoordinateContract {get;set;}=""; [JsonProperty("units")] public string Units {get;set;}=""; [JsonProperty("location_validated")] public bool? LocationValidated {get;set;} [JsonProperty("delta_mm")] public double? DeltaMm {get;set;} [JsonProperty("check_type")] public string CheckType {get;set;}=""; }
public sealed class BuildAiAnalysis { [JsonProperty("reason")] public string Reason {get;set;}=""; [JsonProperty("assessment")] public string Assessment {get;set;}=""; [JsonProperty("is_real_issue")] public bool? IsRealIssue {get;set;} }
public sealed class BuildAiRevitIssueRecord { [JsonProperty("issue_id")] public string IssueId {get;set;}=""; [JsonIgnore] public bool RawIssueIdPresent {get;set;} [JsonProperty("display_id")] public int? DisplayId {get;set;} [JsonProperty("issue_type")] public string IssueType {get;set;}=""; [JsonProperty("issue_status")] public string IssueStatus {get;set;}=""; [JsonProperty("created_at")] public DateTime? CreatedAt {get;set;} [JsonProperty("result_key")] public string ResultKey {get;set;}=""; [JsonProperty("result_data")] public BuildAiResultData ResultData {get;set;}=new(); [JsonProperty("ai_analysis")] public BuildAiAnalysis Analysis {get;set;}=new(); [JsonProperty("primary_element")] public BuildAiRevitElementReference? PrimaryElement {get;set;} [JsonProperty("secondary_element")] public BuildAiRevitElementReference? SecondaryElement {get;set;} }

public interface IBuildAiTokenProvider { (string? Token,string Source) GetToken(); }
public sealed class WindowsBuildAiTokenProvider : IBuildAiTokenProvider { public (string? Token,string Source) GetToken(){ string source; var token=WindowsCredentialTokenProvider.ReadBuildAiApiToken(out source); return (token,source); } }
public sealed class BuildAiAuthorizationException : InvalidOperationException { public BuildAiAuthorizationException():base("BuildAI authorization failed. Update the BuildAI API token."){} }
public interface IBuildAiRevitIssuesClient { Task<IReadOnlyList<BuildAiRevitIssueRecord>> GetAsync(string modelUid,string source,CancellationToken ct); }
public sealed class BuildAiRevitIssuesClient : IBuildAiRevitIssuesClient {
    private readonly IAccAuthProvider auth; private readonly HttpClient http; private readonly IBuildAiTokenProvider tokens;
    public BuildAiRevitIssuesClient(IAccAuthProvider auth,HttpClient? http=null,IBuildAiTokenProvider? tokens=null){this.auth=auth;this.http=http??new HttpClient{Timeout=TimeSpan.FromSeconds(60)};this.tokens=tokens??new WindowsBuildAiTokenProvider();}
    public Task<IReadOnlyList<BuildAiRevitIssueRecord>> GetAsync(string modelUid,CancellationToken ct)=>GetAsync(modelUid,"ar-st",ct);
    public async Task<IReadOnlyList<BuildAiRevitIssueRecord>> GetAsync(string modelUid,string source,CancellationToken ct){
        var url=auth.BackendUrl.TrimEnd('/')+"/api/revit_issues?revit_model_uid="+Uri.EscapeDataString(modelUid??"")+"&source="+Uri.EscapeDataString(source??"");
        var (token,credentialSource)=tokens.GetToken(); if(string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("BuildAI API token is not configured. Update the BuildAI API token.");
        var retriedCanonical=false; for(var attempt=1;;attempt++){
            AccIssueReturnLog.Event("BUILDAI_REVIT_ISSUES_REQUEST",new{endpointPath="/api/revit_issues",modelUidHash=Short(modelUid),source,credentialSource,tokenPresent=true});
            var sw=Stopwatch.StartNew(); using var request=new HttpRequestMessage(HttpMethod.Get,url); request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token); using var response=await http.SendAsync(request,ct).ConfigureAwait(false); var body=await response.Content.ReadAsStringAsync().ConfigureAwait(false); sw.Stop();
            if(!response.IsSuccessStatusCode)AccIssueReturnLog.Event("BUILDAI_MAPPING_RESPONSE_SHAPE",new{source,rootTokenType="unknown",recordsContainer="unknown",recordsCount=0,firstRecordPropertyNames=Array.Empty<string>(),has_issue_id=false,has_issueId=false,has_id=false,has_acc_issue_id=false,has_accIssueId=false});
            if(response.StatusCode==HttpStatusCode.Unauthorized && credentialSource=="Legacy" && !retriedCanonical){ var canonical=WindowsCredentialTokenProvider.ReadBuildAiApiToken(out var nextSource); if(!string.IsNullOrWhiteSpace(canonical)&&nextSource=="Canonical"){credentialSource="Canonical"; token=canonical; retriedCanonical=true; AccIssueReturnLog.Event("BUILDAI_REVIT_ISSUES_AUTH_RETRY",new{fromCredentialSource="Legacy",toCredentialSource="Canonical"}); continue;} }
            if((int)response.StatusCode==429 || ((int)response.StatusCode>=500 && attempt<3)){await Task.Delay(TimeSpan.FromMilliseconds(250*attempt),ct).ConfigureAwait(false);continue;}
            var count=0; IReadOnlyList<BuildAiRevitIssueRecord> rows=Array.Empty<BuildAiRevitIssueRecord>(); if(response.IsSuccessStatusCode){rows=ParseResponse(body);count=rows.Count;LogResponseDiagnostics(body,source,rows);} AccIssueReturnLog.Event("BUILDAI_REVIT_ISSUES_RESPONSE",new{statusCode=(int)response.StatusCode,requestedSource=source,returnedCheckTypes=rows.Select(r=>r.ResultData?.CheckType??"").Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray(),receivedCount=count,elapsedMs=sw.ElapsedMilliseconds});
            if(response.StatusCode==HttpStatusCode.Unauthorized||response.StatusCode==HttpStatusCode.Forbidden)throw new BuildAiAuthorizationException();
            if(response.StatusCode==HttpStatusCode.NotFound)return rows;
            response.EnsureSuccessStatusCode(); return rows;
        }
    }
    public static IReadOnlyList<BuildAiRevitIssueRecord> ParseResponse(string body){
        if(string.IsNullOrWhiteSpace(body))return Array.Empty<BuildAiRevitIssueRecord>();
        var json=JToken.Parse(body);
        var tokens=json.Type==JTokenType.Array?json as JArray:json["issues"] as JArray;
        var rows=tokens?.ToObject<List<BuildAiRevitIssueRecord>>()??new List<BuildAiRevitIssueRecord>();
        for(var i=0;i<rows.Count;i++)rows[i].RawIssueIdPresent=tokens?[i] is JObject raw&&!string.IsNullOrWhiteSpace((string?)raw["issue_id"]);
        return rows;
    }
    public static string NormalizeIssueId(string value)=>string.IsNullOrWhiteSpace(value)?"":value.Trim().ToUpperInvariant();
    public static bool TryNormalizeIssueId(string? value,out Guid issueId){
        issueId=Guid.Empty; if(string.IsNullOrWhiteSpace(value))return false;
        var trimmed=value!.Trim();
        if(trimmed.Length>=2&&trimmed[0]=='{'&&trimmed[trimmed.Length-1]=='}')trimmed=trimmed.Substring(1,trimmed.Length-2);
        return Guid.TryParse(trimmed,out issueId);
    }
    public static bool TryDiagnosticId(string? raw,out string normalized){
        var valid=TryNormalizeIssueId(raw,out var guid);
        normalized=valid?guid.ToString("D").ToLowerInvariant():"";
        return valid;
    }
    public static (string Suffix,string Hash) DiagnosticKey(Guid guid)=>DiagnosticKey(guid.ToString("D").ToLowerInvariant());
    public static (string Suffix,string Hash) DiagnosticKey(string normalized){
        if(string.IsNullOrEmpty(normalized))return ("","");
        using var sha=SHA256.Create(); var bytes=sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        return (normalized.Substring(Math.Max(0,normalized.Length-8)),BitConverter.ToString(bytes).Replace("-","").Substring(0,12).ToLowerInvariant());
    }
    private static void LogResponseDiagnostics(string body,string? source,IReadOnlyList<BuildAiRevitIssueRecord> rows){
        var root=string.IsNullOrWhiteSpace(body)?JValue.CreateNull():JToken.Parse(body);
        var container="unknown"; JArray? records=null;
        if(root is JArray array){container="root";records=array;}
        else if(root is JObject obj){
            if(obj["issues"] is JArray issues){container="root";records=issues;}
            else foreach(var name in new[]{"data","results","items"})if(obj[name] is JArray found){container=name;records=found;break;}
        }
        var first=records?.FirstOrDefault() as JObject;
        AccIssueReturnLog.Event("BUILDAI_MAPPING_RESPONSE_SHAPE",new{source,rootTokenType=root.Type.ToString(),recordsContainer=container,recordsCount=records?.Count??0,firstRecordPropertyNames=first?.Properties().Select(p=>p.Name).ToArray()??Array.Empty<string>(),has_issue_id=first?.Property("issue_id")!=null,has_issueId=first?.Property("issueId")!=null,has_id=first?.Property("id")!=null,has_acc_issue_id=first?.Property("acc_issue_id")!=null,has_accIssueId=first?.Property("accIssueId")!=null});
        if(records==null)return;
        for(var i=0;i<Math.Min(5,records.Count);i++){
            var record=records[i] as JObject; var rawProperty=new[]{"issue_id","issueId","id","acc_issue_id","accIssueId"}.FirstOrDefault(name=>record?.Property(name)!=null);
            var raw=rawProperty==null?null:record![rawProperty]; var rawText=raw?.Type==JTokenType.Null?null:raw?.ToString();
            var dto=i<rows.Count?rows[i]:null;
            var selected=rawProperty!=null&&dto!=null&&string.Equals(dto.IssueId,rawText,StringComparison.Ordinal)?rawProperty:"none";
            var valid=TryDiagnosticId(rawText,out var normalized); var key=DiagnosticKey(normalized);
            AccIssueReturnLog.Event("BUILDAI_MAPPING_KEY_PARSED",new{source,recordIndex=i,selectedJsonProperty=selected,rawValuePresent=!string.IsNullOrWhiteSpace(rawText),rawValueType=raw?.Type.ToString()??"none",rawLength=rawText?.Length??0,guidParseSucceeded=valid,normalizedIdSuffix=key.Suffix,normalizedIdHash=key.Hash,resultKeyPresent=!string.IsNullOrWhiteSpace(dto?.ResultKey),primaryElementPresent=dto?.PrimaryElement!=null,secondaryElementPresent=dto?.SecondaryElement!=null});
        }
    }
    private static string Short(string value){if(string.IsNullOrWhiteSpace(value))return ""; return value.Length<=10?value:"..."+value.Substring(value.Length-10);}
}
