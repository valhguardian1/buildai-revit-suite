using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BuildAI.Core.Issues;
using BuildAI.RevitCompatibility;
using Plugin5.ClashFormaIntegration.Models;

namespace Plugin5.ClashFormaIntegration.Revit
{
    internal enum ModelAction { None, CreateViews, Select, PreparePublishViews, SynchronizeForPublication }
    internal sealed class ModelActionHandler : IExternalEventHandler
    {
        public ModelAction Action;
        public ClashItem Selected;
        private ExternalEventOperation<PublishViewPreparationResult> _publishOperation;
        private ExternalEventOperation<bool> _syncOperation;
        private IProgress<string> _publishProgress;
        public ExternalEventOperation<PublishViewPreparationResult> RequestPreparePublishViews(IProgress<string> progress) { _publishOperation = new ExternalEventOperation<PublishViewPreparationResult>("Prepare publication views", progress); _publishProgress = progress; Action = ModelAction.PreparePublishViews; return _publishOperation; }
        public ExternalEventOperation<bool> RequestSynchronizeForPublication(IProgress<string> progress) { _syncOperation = new ExternalEventOperation<bool>("Synchronize model for publication", progress); _publishProgress = progress; Action = ModelAction.SynchronizeForPublication; return _syncOperation; }
        public void Execute(UIApplication app)
        {
            var action = Action;
            var operationName = action == ModelAction.PreparePublishViews ? _publishOperation?.Name : action == ModelAction.SynchronizeForPublication ? _syncOperation?.Name : null;
            var externalOperation = action == ModelAction.PreparePublishViews || action == ModelAction.SynchronizeForPublication;
            if (action == ModelAction.PreparePublishViews && (_publishOperation == null || !_publishOperation.TryEnter())) { _publishProgress?.Report("EXTERNAL EVENT EXECUTE SKIPPED\nOperation: Prepare publication views\nReason: request was cancelled before Execute() started."); Action = ModelAction.None; return; }
            if (action == ModelAction.SynchronizeForPublication && (_syncOperation == null || !_syncOperation.TryEnter())) { _publishProgress?.Report("EXTERNAL EVENT EXECUTE SKIPPED\nOperation: Synchronize model for publication\nReason: request was cancelled before Execute() started."); Action = ModelAction.None; return; }
            if (externalOperation) _publishProgress?.Report("EXTERNAL EVENT EXECUTE ENTER\nOperation: " + operationName);
            var externalOutcome = "SUCCESS";
            try
            {
                var uidoc=app.ActiveUIDocument; var doc=uidoc?.Document; if(doc==null)throw new InvalidOperationException("No active Revit document.");
                if(action==ModelAction.Select&&Selected!=null) ShowClash(uidoc, doc, Selected);
                else if(action==ModelAction.CreateViews) Create(doc);
                else if(action==ModelAction.PreparePublishViews) { _publishProgress?.Report("Preparing Revit views for Autodesk publication..."); var r=PreparePublishViews(doc,_publishProgress); _publishOperation?.TrySetResult(r); }
                else if(action==ModelAction.SynchronizeForPublication) { SynchronizeForPublication(doc,_publishProgress); _syncOperation?.TrySetResult(true); }
            }
            catch(Exception x){externalOutcome="FAILED: "+x.GetType().FullName+": "+x.Message;_publishOperation?.TrySetException(x);_syncOperation?.TrySetException(x);TaskDialog.Show("BuildAI",x.Message);}
            finally{if(externalOperation)_publishProgress?.Report("EXTERNAL EVENT EXECUTE EXIT\nOperation: "+operationName+"\nResult: "+externalOutcome);Action=ModelAction.None;}
        }

        /// <summary>
        /// Previously this only set a section box: the camera was never moved and the
        /// display style never changed, so the view opened wherever it had last been
        /// left - usually zoomed to the whole model - with the clash cropped out of
        /// sight somewhere inside it. That is the Revit half of "the camera flies
        /// somewhere wrong"; the other half is in viewer-probe.html.
        ///
        /// It also created its own view named "BuildAI Clash Coordination", which is
        /// not the view publication prepares. A view outside the cloud publish set has
        /// no configured link visibility and carries no pushpins, so the two halves of
        /// the product disagreed about which 3D view a clash lives in.
        /// </summary>
        private static void ShowClash(UIDocument uidoc, Document doc, ClashItem item)
        {
            var view = PushpinLocator.EnsureNamed3D(doc, BuildAiViewNames.Coordination);
            if (view == null) return;

            var refs = new List<Reference>(); var hostIds = new List<ElementId>();
            AddReference(doc, item.LinkInstanceAId, item.ElementAId, refs, hostIds);
            AddReference(doc, item.LinkInstanceBId, item.ElementBId, refs, hostIds);

            // The stored coordinates are millimetres in host internal space; the Revit
            // API works in feet throughout.
            var point = new XYZ(item.X / 304.8, item.Y / 304.8, item.Z / 304.8);
            PushpinLocator.FocusOn(uidoc, view, point, refs, hostIds.Distinct().ToList());
        }

        private static void AddReference(Document host,int?linkId,int elementId,List<Reference>refs,List<ElementId>hostIds)
        {
            if(!linkId.HasValue){var e=host.GetElement(new ElementId(elementId));if(e!=null)hostIds.Add(e.Id);return;}
            var link=host.GetElement(new ElementId(linkId.Value))as RevitLinkInstance;var linked=link?.GetLinkDocument()?.GetElement(new ElementId(elementId));
            if(link!=null&&linked!=null)refs.Add(new Reference(linked).CreateLinkReference(link));
        }

        private static void SynchronizeForPublication(Document doc, IProgress<string> progress)
        {
            progress?.Report("Synchronizing the active Revit model before native APS publication...");
            if (doc.IsWorkshared)
            {
                var transact = new TransactWithCentralOptions();
                var sync = new SynchronizeWithCentralOptions();
                sync.SetRelinquishOptions(new RelinquishOptions(true));
                sync.Comment = "BuildAI native APS publication";
                doc.SynchronizeWithCentral(transact, sync);
                progress?.Report("[OK] Synchronize with Central completed.");
            }
            else
            {
                if (doc.IsModified) doc.Save();
                progress?.Report("[OK] Revit document saved. The model is not workshared.");
            }
        }
        private static PublishViewPreparationResult PreparePublishViews(Document doc, IProgress<string> progress)
        {
            var wasModified = doc.IsModified;
            var existingNames = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .Where(x => !x.IsTemplate).Select(x => x.Name).ToList();
            // Clash Issues are attached exclusively to BuildAI Coordination. The
            // AR-ST view is owned by Plugin4 and must remain byte-for-byte untouched:
            // earlier builds reset its template, section box, categories and links
            // even for an HVAC/Plumbing clash publication.
            var viewsCreated = !existingNames.Contains(BuildAiViewNames.Coordination);
            var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(x=>x.ViewFamily==ViewFamily.ThreeDimensional);
            if(type==null)throw new InvalidOperationException("No 3D view type was found.");
            using(var tx=new Transaction(doc,"BuildAI: prepare publication views"))
            {
                tx.Start();
                var coordination=EnsureNamed3D(doc,type,BuildAiViewNames.Coordination);
                ResetView(coordination);
                var coordinationVisibility = MepComparisonViewComposer.Compose(doc, coordination, PluginContext.Report);
                tx.Commit();
                progress?.Report(
                    coordinationVisibility.Details+"\n"+
                    "AR-ST VIEW PRESERVED\n"+
                    "Clash publication does not create, reset, filter or publish-control BuildAI AR-ST.");
            }
            progress?.Report("[OK] BuildAI Coordination prepared from the selected MEP categories and actual Clash participants.\n[OK] BuildAI AR-ST was not modified by the Clash workflow.");
            var cloud = TryGetCloudIdentity(doc);
            var shared = GetSharedCoordinateOffset(doc);
            return new PublishViewPreparationResult
            {
                DocumentTitle = doc.Title, HostModelUid = doc.ProjectInformation?.UniqueId, HubId = cloud.HubId, ProjectId = cloud.ProjectId,
                ProjectGuid = cloud.ProjectGuid, ModelGuid = cloud.ModelGuid,
                SharedOffsetX = shared.X, SharedOffsetY = shared.Y, SharedOffsetZ = shared.Z,
                SharedAngleRadians = TryGetSharedAngle(doc), ViewsCreated = viewsCreated,
                DocumentWasModifiedBeforePreparation = wasModified,
                DocumentIsModifiedAfterPreparation = doc.IsModified
            };
        }
        private static XYZ GetSharedCoordinateOffset(Document doc)
        {
            try
            {
                var position = doc.ActiveProjectLocation?.GetProjectPosition(XYZ.Zero);
                return position == null ? XYZ.Zero : new XYZ(position.EastWest, position.NorthSouth, position.Elevation);
            }
            catch
            {
                return XYZ.Zero;
            }
        }
        private static View3D EnsureNamed3D(Document doc,ViewFamilyType type,string name){var v=new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(x=>!x.IsTemplate&&x.Name==name);if(v==null){v=View3D.CreateIsometric(doc,type.Id);v.Name=name;}return v;}
        private static void ResetView(View3D view){try{view.IsSectionBoxActive=false;}catch{}try{view.DetailLevel=ViewDetailLevel.Fine;}catch{}try{view.DisplayStyle=DisplayStyle.FlatColors;}catch{}}
        private sealed class CloudIdentity { public string HubId; public string ProjectId; public string ProjectGuid; public string ModelGuid; }
        private static CloudIdentity TryGetCloudIdentity(Document doc)
        {
            var result = new CloudIdentity { ProjectId = TryGetProjectId(doc) };
            try
            {
                var hub = doc.GetType().GetMethod("GetHubId", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                result.HubId = hub?.Invoke(doc, null) as string;
            }
            catch { }
            try
            {
                var cloudPathMethod = doc.GetType().GetMethod("GetCloudModelPath", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                var path = cloudPathMethod?.Invoke(doc, null);
                if (path != null)
                {
                    var t = path.GetType();
                    var pg = t.GetMethod("GetProjectGUID", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null) ??
                             t.GetMethod("GetProjectGuid", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                    var mg = t.GetMethod("GetModelGUID", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null) ??
                             t.GetMethod("GetModelGuid", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                    var pv = pg?.Invoke(path, null); var mv = mg?.Invoke(path, null);
                    result.ProjectGuid = pv?.ToString(); result.ModelGuid = mv?.ToString();
                }
            }
            catch { }
            return result;
        }
        private static string TryGetProjectId(Document doc){try{var m=doc.GetType().GetMethod("GetProjectId",BindingFlags.Instance|BindingFlags.Public,null,Type.EmptyTypes,null);return m?.Invoke(doc,null)as string;}catch{return null;}}

        private void Create(Document d)
        {
            var r=PluginContext.Report;if(r==null||r.Items.Count==0){TaskDialog.Show("BuildAI","No clashes were found.");return;}
            var vt=new FilteredElementCollector(d).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(x=>x.ViewFamily==ViewFamily.Section);if(vt==null)throw new InvalidOperationException("No section view type was found.");
            FamilySymbol tb=null;if(PluginContext.Settings.CreateSheets){tb=new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().Cast<FamilySymbol>().FirstOrDefault();if(tb==null)throw new InvalidOperationException("No title block was found.");}
            using(var t=new Transaction(d,"BuildAI: clash views")){t.Start();int n=1;foreach(var x in r.Items){var p=new XYZ(x.X/304.8,x.Y/304.8,x.Z/304.8);var box=new BoundingBoxXYZ{Transform=Transform.Identity,Min=new XYZ(p.X-5,p.Y-5,p.Z-5),Max=new XYZ(p.X+5,p.Y+5,p.Z+5)};var v=ViewSection.CreateSection(d,vt.Id,box);v.Scale=PluginContext.Settings.SectionScale;v.Name=PluginContext.Settings.SectionNameTemplate.Replace("{id}",x.Id.Substring(0,8)).Replace("{level}",San(x.Level));x.SectionViewId=ElementIdCompatibility.ToInt32(v.Id);if(tb!=null){var sh=ViewSheet.Create(d,tb.Id);sh.SheetNumber=PluginContext.Settings.SheetNumberPrefix+n.ToString("000");sh.Name="Clash "+x.Id.Substring(0,8);if(Viewport.CanAddViewToSheet(d,sh.Id,v.Id))Viewport.Create(d,sh.Id,v.Id,new XYZ(1,1,0));x.SheetId=ElementIdCompatibility.ToInt32(sh.Id);}n++;}t.Commit();}
            PluginContext.SaveReport(); TaskDialog.Show("BuildAI",$"Section views created: {r.Items.Count}");
        }
        static string San(string x){foreach(var c in "\\:{}[]|;<>?`~")x=(x??"").Replace(c,'_');return x;}
        private static double TryGetSharedAngle(Document doc) { try { return doc?.ActiveProjectLocation?.GetProjectPosition(XYZ.Zero)?.Angle ?? 0.0; } catch { return 0.0; } }
        public string GetName()=>"BuildAI Plugin5 actions";
    }
}
