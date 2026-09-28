using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BuildAI.RevitCompatibility;
using BuildAI.Core.Localization;
using BuildAI.Core.Logging;
using Plugin4.LinkComparatorAI.Models;
using Plugin4.LinkComparatorAI.Comparison;
using Plugin4.LinkComparatorAI.UI;

namespace Plugin4.LinkComparatorAI.Revit
{
    public sealed class ComparatorActionHandler : IExternalEventHandler
    {
        private readonly object _gate = new object();
        private Request _pending;
        public void RequestHighlight(ComparisonIssue issue) { lock (_gate) _pending = new Request { Issue = issue }; }
        public void RequestReset() { lock (_gate) _pending = new Request { Reset = true }; }
        public ExternalEventOperation<PublishViewPreparationResult> RequestPreparePublishViews(IProgress<string> progress)
        {
            var operation = new ExternalEventOperation<PublishViewPreparationResult>("Prepare publication views", progress);
            lock (_gate) _pending = new Request { PreparePublishViews = true, PublishOperation = operation, Progress = progress };
            return operation;
        }
        public Task<ComparisonReport> RequestRecalculate(IProgress<string> progress)
        {
            var tcs = new TaskCompletionSource<ComparisonReport>();
            lock (_gate) _pending = new Request { Recalculate = true, ReportCompletion = tcs, Progress = progress };
            return tcs.Task;
        }
        public ExternalEventOperation<bool> RequestSynchronizeForPublication(IProgress<string> progress)
        {
            var operation = new ExternalEventOperation<bool>("Synchronize model for publication", progress);
            lock (_gate) _pending = new Request { SynchronizeForPublication = true, SyncOperation = operation, Progress = progress };
            return operation;
        }

        public void Execute(UIApplication app)
        {
            Request request; lock (_gate) { request = _pending; _pending = null; }
            if (request == null) return;
            var operationName = request.PreparePublishViews ? request.PublishOperation?.Name :
                request.SynchronizeForPublication ? request.SyncOperation?.Name : null;
            var externalOperation = request.PreparePublishViews || request.SynchronizeForPublication;
            if (request.PreparePublishViews && (request.PublishOperation == null || !request.PublishOperation.TryEnter()))
            {
                request.Progress?.Report("EXTERNAL EVENT EXECUTE SKIPPED\nOperation: Prepare publication views\nReason: request was cancelled before Execute() started.");
                return;
            }
            if (request.SynchronizeForPublication && (request.SyncOperation == null || !request.SyncOperation.TryEnter()))
            {
                request.Progress?.Report("EXTERNAL EVENT EXECUTE SKIPPED\nOperation: Synchronize model for publication\nReason: request was cancelled before Execute() started.");
                return;
            }
            if (externalOperation) request.Progress?.Report("EXTERNAL EVENT EXECUTE ENTER\nOperation: " + operationName);
            var externalOutcome = "SUCCESS";
            try
            {
                var uidoc = app.ActiveUIDocument; var doc = uidoc?.Document; if (doc == null) throw new InvalidOperationException("No active Revit document.");
                if (request.Recalculate)
                {
                    var settings = PluginContext.Settings ?? ComparatorSettings.Load();
                    LinkRefreshService.RefreshSelectedLinks(doc, settings, true, x => request.Progress?.Report(x));
                    request.Progress?.Report("Recalculating AR-ST from refreshed linked documents...");
                    var report = PluginContext.Engine.Run(doc, settings, true, false);
                    PluginContext.LastReport = report;
                    request.Progress?.Report("[OK] AR-ST recalculated. Results: " + report.Issues.Count);
                    request.ReportCompletion?.TrySetResult(report);
                    return;
                }
                if (request.PreparePublishViews)
                {
                    request.Progress?.Report("Preparing Revit views for Autodesk publication...");
                    var result = PreparePublishViews(doc, request.Progress);
                    request.PublishOperation?.TrySetResult(result);
                    return;
                }
                if (request.SynchronizeForPublication)
                {
                    SynchronizeForPublication(doc, request.Progress);
                    request.SyncOperation?.TrySetResult(true);
                    return;
                }
                if (request.Reset)
                {
                    uidoc.Selection.SetElementIds(new List<ElementId>());

                    var active3D = uidoc.ActiveView as View3D;
                    if (active3D != null && !active3D.IsTemplate)
                    {
                        try
                        {
                            using (var tx = new Transaction(doc, "BuildAI: clear comparison focus"))
                            {
                                tx.Start();
                                if (active3D.IsSectionBoxActive) active3D.IsSectionBoxActive = false;
                                tx.Commit();
                            }
                        }
                        catch (Exception ex)
                        {
                            PluginLog.Warn("Could not deactivate the comparison section box: " + ex.Message);
                        }
                    }

                    uidoc.RefreshActiveView();
                    ResultsPane.SetGlobalStatus("Selection and focused view cleared.");
                    return;
                }

                var refs = new List<Reference>();
                var hostIds = new List<ElementId>();
                var boxes = new List<BoundingBoxXYZ>();
                AddElement(doc, request.Issue.ArchitecturalLinkUniqueId, request.Issue.ArchitecturalElementUniqueId, request.Issue.ArchitecturalElementId, refs, hostIds, boxes);
                AddElement(doc, request.Issue.StructuralLinkUniqueId, request.Issue.StructuralElementUniqueId, request.Issue.StructuralElementId, refs, hostIds, boxes);
                if (refs.Count == 0 && hostIds.Count == 0)
                {
                    ResultsPane.SetGlobalStatus(Loc.T("P4_NotFound"));
                    TaskDialog.Show("BuildAI", Loc.T("P4_NotFound")); return;
                }

                // Same defect as Plugin5: a section box was applied but the camera was
                // never oriented and the display style never changed, so the view kept
                // whatever orientation and zoom it was last left with.
                var view = EnsureCoordination3D(doc);
                if (view != null)
                {
                    var section = Union(boxes, 3.0);
                    var target = section == null
                        ? null
                        : (section.Min + section.Max) * 0.5;
                    var radius = section == null
                        ? 4.0
                        : Math.Max(3.0, (section.Max - section.Min).GetLength() * 0.5);
                    if (target != null)
                        PushpinLocator.FocusOn(uidoc, view, target, refs, hostIds, radius);
                }
                else
                {
                    if (refs.Count > 0) uidoc.Selection.SetReferences(refs);
                    else uidoc.Selection.SetElementIds(hostIds);
                    uidoc.RefreshActiveView();
                }
                ResultsPane.SetGlobalStatus("Element shown in 3D coordination view.");
                PluginLog.Info($"Displayed comparison result. Linked references: {refs.Count}; host elements: {hostIds.Count}.");
            }
            catch(Exception ex)
            {
                externalOutcome = "FAILED: " + ex.GetType().FullName + ": " + ex.Message;
                request?.ReportCompletion?.TrySetException(ex);
                request?.PublishOperation?.TrySetException(ex);
                request?.SyncOperation?.TrySetException(ex);
                ResultsPane.SetGlobalStatus(ex.Message);
                PluginLog.Error("Plugin4 highlight failed", ex);
                TaskDialog.Show("BuildAI", ex.Message);
            }
            finally
            {
                if (externalOperation)
                    request.Progress?.Report("EXTERNAL EVENT EXECUTE EXIT\nOperation: " + operationName + "\nResult: " + externalOutcome);
            }
        }

        private static void AddElement(Document host, string linkUid, string elementUid, int elementId, List<Reference> refs, List<ElementId> hostIds, List<BoundingBoxXYZ> boxes)
        {
            if (string.IsNullOrWhiteSpace(linkUid))
            {
                Element element = null;
                if (!string.IsNullOrWhiteSpace(elementUid)) element = host.GetElement(elementUid);
                if (element == null && elementId > 0) element = host.GetElement(new ElementId(elementId));
                if (element == null) return;
                hostIds.Add(element.Id);
                var box = ToHostBox(element.get_BoundingBox(null), Transform.Identity); if (box != null) boxes.Add(box);
                return;
            }

            var link = host.GetElement(linkUid) as RevitLinkInstance;
            var linkedDoc = link?.GetLinkDocument();
            var linked = linkedDoc?.GetElement(elementUid);
            if (link == null || linked == null) return;
            refs.Add(new Reference(linked).CreateLinkReference(link));
            var linkedBox = ToHostBox(linked.get_BoundingBox(null), link.GetTotalTransform() ?? Transform.Identity); if (linkedBox != null) boxes.Add(linkedBox);
        }

        private static View3D EnsureCoordination3D(Document doc)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(x => !x.IsTemplate && x.Name == "BuildAI Coordination");
            if (existing != null) return existing;
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);
            if (type == null) return null;
            using (var t = new Transaction(doc, "BuildAI: create coordination view"))
            {
                t.Start(); var view = View3D.CreateIsometric(doc, type.Id); view.Name = "BuildAI Coordination"; t.Commit(); return view;
            }
        }

        private static BoundingBoxXYZ ToHostBox(BoundingBoxXYZ box, Transform sourceTransform)
        {
            if (box == null) return null;
            var local = box.Transform ?? Transform.Identity;
            var pts = new List<XYZ>();
            for (var x=0;x<2;x++) for (var y=0;y<2;y++) for (var z=0;z<2;z++)
                pts.Add(sourceTransform.OfPoint(local.OfPoint(new XYZ(x==0?box.Min.X:box.Max.X,y==0?box.Min.Y:box.Max.Y,z==0?box.Min.Z:box.Max.Z))));
            return new BoundingBoxXYZ { Transform=Transform.Identity, Min=new XYZ(pts.Min(p=>p.X),pts.Min(p=>p.Y),pts.Min(p=>p.Z)), Max=new XYZ(pts.Max(p=>p.X),pts.Max(p=>p.Y),pts.Max(p=>p.Z)) };
        }
        private static BoundingBoxXYZ Union(IList<BoundingBoxXYZ> boxes, double pad)
        {
            if (boxes == null || boxes.Count == 0) return null;
            return new BoundingBoxXYZ { Transform=Transform.Identity,
                Min=new XYZ(boxes.Min(x=>x.Min.X)-pad,boxes.Min(x=>x.Min.Y)-pad,boxes.Min(x=>x.Min.Z)-pad),
                Max=new XYZ(boxes.Max(x=>x.Max.X)+pad,boxes.Max(x=>x.Max.Y)+pad,boxes.Max(x=>x.Max.Z)+pad) };
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
            var viewsCreated = !existingNames.Contains("BuildAI Coordination") || !existingNames.Contains("BuildAI AR-ST");
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);
            if (type == null) throw new InvalidOperationException("No 3D view type was found.");
            using (var tx = new Transaction(doc, "BuildAI: prepare publication views"))
            {
                tx.Start();
                var coordination = EnsureNamed3D(doc, type, "BuildAI Coordination");
                var arSt = EnsureNamed3D(doc, type, "BuildAI AR-ST");
                ResetView(coordination);
                ResetView(arSt);
                var coordinationVisibility = CoordinationViewVisibility.EnsureFull(doc, coordination);
                // AR-ST is reused between runs. Repair every inherited visibility
                // mechanism before applying the deliberate AR/ST-only link filter;
                // otherwise a template, category, filter or workset hidden by an
                // older run can leave the published service view incomplete.
                var arStVisibility = CoordinationViewVisibility.EnsureFull(doc, arSt);
                SetSelectedLinkVisibility(doc, arSt, PluginContext.Settings ?? ComparatorSettings.Load());
                HideMepFromArSt(doc, arSt);
                tx.Commit();
                progress?.Report(
                    "COORDINATION VISIBILITY VERIFIED\n" +
                    "Template detached: " + coordinationVisibility.TemplateDetached + "\n" +
                    "Model categories unhidden: " + coordinationVisibility.CategoriesUnhidden + "\n" +
                    "Revit link instances: " + coordinationVisibility.LinkInstanceCount + "\n" +
                    "Link instances unhidden: " + coordinationVisibility.LinkInstancesUnhidden + "\n" +
                    coordinationVisibility.Details + "\n" +
                    "AR-ST VISIBILITY BASELINE RESTORED\n" +
                    "Template detached: " + arStVisibility.TemplateDetached + "\n" +
                    "Model categories unhidden: " + arStVisibility.CategoriesUnhidden + "\n" +
                    "Link instances unhidden before AR/ST filtering: " + arStVisibility.LinkInstancesUnhidden + "\n" +
                    arStVisibility.Details);
            }
            progress?.Report("[OK] BuildAI Coordination prepared (all coordination sources).\n[OK] BuildAI AR-ST prepared with only the selected Architecture and Structure sources; unrelated links hidden.");
            var cloud = TryGetCloudIdentity(doc);
            var shared = TryGetSharedCoordinateTransform(doc);
            return new PublishViewPreparationResult
            {
                DocumentTitle = doc.Title, HostModelUid = doc.ProjectInformation?.UniqueId, HubId = cloud.HubId, ProjectId = cloud.ProjectId, ProjectGuid = cloud.ProjectGuid, ModelGuid = cloud.ModelGuid,
                SharedOffsetX = shared.Offset.X, SharedOffsetY = shared.Offset.Y, SharedOffsetZ = shared.Offset.Z, SharedAngleRadians = shared.Angle,
                ViewsCreated = viewsCreated, DocumentWasModifiedBeforePreparation = wasModified, DocumentIsModifiedAfterPreparation = doc.IsModified
            };
        }
        private static View3D EnsureNamed3D(Document doc, ViewFamilyType type, string name)
        {
            var view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(x => !x.IsTemplate && x.Name == name);
            if (view == null) { view = View3D.CreateIsometric(doc, type.Id); view.Name = name; }
            return view;
        }
        private static void ResetView(View3D view)
        {
            try { view.IsSectionBoxActive = false; } catch { }
            try { view.DetailLevel = ViewDetailLevel.Fine; } catch { }
            try { view.DisplayStyle = DisplayStyle.FlatColors; } catch { }
        }
        private static bool IsElementHiddenInView(Document doc, View view, ElementId id)
        {
            if (doc == null || view == null || id == null) return false;
            try
            {
                var element = doc.GetElement(id);
                return element != null && element.IsHidden(view);
            }
            catch
            {
                return false;
            }
        }
        private static void SetSelectedLinkVisibility(Document doc, View3D view, ComparatorSettings settings)
        {
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (settings != null)
            {
                if (!string.IsNullOrWhiteSpace(settings.ArchitecturalLinkUniqueId) && settings.ArchitecturalLinkUniqueId != ComparatorSettings.HostSourceId)
                    selected.Add(settings.ArchitecturalLinkUniqueId);
                if (!string.IsNullOrWhiteSpace(settings.StructuralLinkUniqueId) && settings.StructuralLinkUniqueId != ComparatorSettings.HostSourceId)
                    selected.Add(settings.StructuralLinkUniqueId);
            }
            var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            var show = links.Where(x => selected.Contains(x.UniqueId)).Select(x => x.Id).ToList();
            var hide = links.Where(x => !selected.Contains(x.UniqueId)).Select(x => x.Id).ToList();
            try
            {
                var hiddenSelected = show.Where(id => IsElementHiddenInView(doc, view, id)).ToList();
                if (hiddenSelected.Count > 0) view.UnhideElements(hiddenSelected);
            }
            catch { }
            try
            {
                var visibleUnselected = hide.Where(id => !IsElementHiddenInView(doc, view, id)).ToList();
                if (visibleUnselected.Count > 0) view.HideElements(visibleUnselected);
            }
            catch { }
        }
        private static void HideMepFromArSt(Document doc, View3D view)
        {
            var mepCategories = new[] { BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting, BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Sprinklers };
            foreach (var bic in mepCategories) try { var c = Category.GetCategory(doc, bic); if (c != null && view.CanCategoryBeHidden(c.Id)) view.SetCategoryHidden(c.Id, true); } catch { }
            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var n = (link.Name ?? "").ToUpperInvariant();
                if (n.Contains("MEP") || n.Contains("HVAC") || n.Contains("PLUMB") || n.Contains("ELECT") || n.Contains("SYSTEM") || n.Contains("MECH"))
                    try { view.HideElements(new[] { link.Id }); } catch { }
            }
        }

        private sealed class SharedCoordinateTransformInfo { public XYZ Offset = XYZ.Zero; public double Angle; }
        private static SharedCoordinateTransformInfo TryGetSharedCoordinateTransform(Document doc)
        {
            try
            {
                var location = doc?.ActiveProjectLocation;
                if (location == null) return new SharedCoordinateTransformInfo();
                var position = location.GetProjectPosition(XYZ.Zero);
                if (position == null) return new SharedCoordinateTransformInfo();
                return new SharedCoordinateTransformInfo
                {
                    Offset = new XYZ(position.EastWest, position.NorthSouth, position.Elevation),
                    Angle = position.Angle
                };
            }
            catch { return new SharedCoordinateTransformInfo(); }
        }
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
        private static string TryGetProjectId(Document doc)
        {
            try { var m = doc.GetType().GetMethod("GetProjectId", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null); return m?.Invoke(doc, null) as string; } catch { return null; }
        }
        public string GetName() => "BuildAI Link Comparator actions";
        private sealed class Request { public ComparisonIssue Issue; public bool Reset; public bool Recalculate; public bool PreparePublishViews; public bool SynchronizeForPublication; public TaskCompletionSource<ComparisonReport> ReportCompletion; public ExternalEventOperation<PublishViewPreparationResult> PublishOperation; public ExternalEventOperation<bool> SyncOperation; public IProgress<string> Progress; }
    }
}
