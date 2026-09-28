using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using BuildAI.AccIssueReturn.Core;
using Newtonsoft.Json;
using System;

namespace BuildAI.AccIssueReturn.Revit;

internal static class IssueStorage
{
    private static readonly Guid LegacySchemaId=new("3B3D0A9A-07A3-4E95-9C5E-6EE7BEA11001");
    private static readonly Guid SchemaId=new("3B3D0A9A-07A3-4E95-9C5E-6EE7BEA11002");
    public static void Write(Element element,AccIssue issue,XYZ point,CoordinateResult coordinates)
    {
        var schema=Schema.Lookup(SchemaId)??Create();var entity=new Entity(schema);var context=issue.Context;var legacy=Schema.Lookup(LegacySchemaId);var migrated=legacy!=null&&element.GetEntity(legacy).IsValid()?1:0;
        entity.Set("SchemaVersion",2);entity.Set("MigratedFromSchemaVersion",migrated);entity.Set("HubId",issue.HubId??"");entity.Set("ProjectId",issue.ProjectId??"");entity.Set("ContainerId",issue.ContainerId??"");entity.Set("IssueId",issue.Id??"");entity.Set("DisplayId",issue.DisplayId??0);entity.Set("ModelUrn",issue.ModelUrn??"");entity.Set("VersionUrn",issue.VersionUrn??"");entity.Set("ViewableGuid",issue.ViewableGuid??"");entity.Set("ViewableUrn",context?.ViewableUrn??issue.SeedUrn??"");entity.Set("SourcePushpin",JsonConvert.SerializeObject(issue.PushpinPosition));entity.Set("SourceGlobalOffset",JsonConvert.SerializeObject(context?.GlobalOffset));entity.Set("SourcePlacementTransform",JsonConvert.SerializeObject(context?.PlacementTransform?.M));entity.Set("AppliedLinkTransform",JsonConvert.SerializeObject(context?.LinkToHost?.M));entity.Set("CalculatedRevitPoint",JsonConvert.SerializeObject(new[]{point.X,point.Y,point.Z}));entity.Set("RoundTripPoint",JsonConvert.SerializeObject(coordinates.RoundTripPoint));entity.Set("CoordinateQuality",coordinates.Quality.ToString());entity.Set("RoundTripErrorMm",coordinates.RoundTripErrorMm);entity.Set("SourceUnits",context?.SourceUnits??"");entity.Set("LastSyncUtc",DateTime.UtcNow.ToString("O"));entity.Set("PayloadHash",Hash(issue.RawJson));entity.Set("ObjectSet",JsonConvert.SerializeObject(issue.ObjectSet));entity.Set("ExternalIds",JsonConvert.SerializeObject(issue.ObjectSet.ConvertAll(x=>x.ExternalId)));entity.Set("RevitUniqueIds",JsonConvert.SerializeObject(issue.ObjectSet.ConvertAll(x=>x.RevitUniqueId)));entity.Set("TransformSource",context?.TransformSource??"");entity.Set("Mode",WindowsCredentialTokenProvider.DetectBuildAiInstall()?IntegrationMode.IntegratedWithBuildAI.ToString():IntegrationMode.StandAlone.ToString());element.SetEntity(entity);if(migrated==1){var written=element.GetEntity(schema);if(written.IsValid()&&written.Get<int>("SchemaVersion")==2)element.DeleteEntity(legacy!);}
    }
    private static Schema Create(){var b=new SchemaBuilder(SchemaId);b.SetSchemaName("BuildAI ACC Issue Return v2");b.SetDocumentation("https://app.buildai.me");b.SetVendorId("BUILDAI");b.SetReadAccessLevel(AccessLevel.Public);b.SetWriteAccessLevel(AccessLevel.Vendor);b.AddSimpleField("SchemaVersion",typeof(int));b.AddSimpleField("MigratedFromSchemaVersion",typeof(int));foreach(var n in new[]{"HubId","ProjectId","ContainerId","IssueId","ModelUrn","VersionUrn","ViewableGuid","ViewableUrn","SourcePushpin","SourceGlobalOffset","SourcePlacementTransform","AppliedLinkTransform","CalculatedRevitPoint","RoundTripPoint","CoordinateQuality","SourceUnits","LastSyncUtc","PayloadHash","ObjectSet","ExternalIds","RevitUniqueIds","TransformSource","Mode"})b.AddSimpleField(n,typeof(string));b.AddSimpleField("DisplayId",typeof(int));b.AddSimpleField("RoundTripErrorMm",typeof(double));return b.Finish();}
    private static string Hash(string value){using var sha=System.Security.Cryptography.SHA256.Create();return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value??""))).Replace("-","");}
}
