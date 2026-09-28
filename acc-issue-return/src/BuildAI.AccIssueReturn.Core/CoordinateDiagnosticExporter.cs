using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BuildAI.AccIssueReturn.Core;

public static class CoordinateDiagnosticExporter
{
    public static string CreateSanitizedJson(AccIssue issue)
    {
        var context=issue.Context;
        var root=new JObject
        {
            ["schema"]="BuildAI.AccIssueReturn.CoordinateFixture.v1",
            ["issueId"]=issue.Id,
            ["versionUrn"]=issue.VersionUrn,
            ["viewableGuid"]=issue.ViewableGuid,
            ["rawUnits"]=context?.SourceUnits??"",
            ["normalizedUnits"]=context?.Unit?.ToString()??context?.UnitStatus.ToString()??UnitParseStatus.Missing.ToString(),
            ["pushpin"]=issue.PushpinPosition==null?null:new JArray(issue.PushpinPosition),
            ["pushpinSpace"]=context?.PushpinSpace.ToString()??CoordinateSpace.Unverified.ToString(),
            ["globalOffset"]=context==null?null:new JArray(context.GlobalOffset),
            ["globalOffsetSpace"]=context?.GlobalOffsetSpace.ToString()??CoordinateSpace.Unverified.ToString(),
            ["placementOrRefPointTransform"]=context==null?null:new JArray(context.PlacementTransform.M),
            ["placementDirection"]=context?.PlacementDirection.ToString()??CoordinateTransformDirection.Unverified.ToString(),
            ["matrixFormat"]=context?.MatrixFormat.ToString()??MatrixSourceFormat.None.ToString(),
            ["camera"]=issue.Camera==null?null:JObject.FromObject(issue.Camera),
            ["objectSet"]=JArray.FromObject(issue.ObjectSet),
            ["provenance"]=context==null?null:new JObject{{"unitsSource",context.UnitsSource},{"globalOffsetSource",context.GlobalOffsetSource},{"placementTransformSource",context.PlacementTransformSource},{"viewableSource",context.ViewableSource},{"contextSources",JArray.FromObject(context.ContextSources)}}
        };
        return root.ToString(Formatting.Indented);
    }
}
