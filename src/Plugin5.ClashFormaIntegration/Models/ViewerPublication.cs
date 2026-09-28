using System;
namespace Plugin5.ClashFormaIntegration.Models {
 public sealed class ViewerPublication {
  public string PublicationId {get;set;}=""; public string ProjectId {get;set;}=""; public string SourceFileName {get;set;}="";
  public string ApsUrn {get;set;}=""; public string Status {get;set;}=""; public string ViewerTokenEndpoint {get;set;}="";
  public string ViewerEnvironment {get;set;}="AutodeskProduction2"; public DateTime CreatedUtc {get;set;}=DateTime.UtcNow;
  public string Note {get;set;}="SVF2 is streamed from APS; this descriptor is the ready-to-view artifact.";
 }
}
