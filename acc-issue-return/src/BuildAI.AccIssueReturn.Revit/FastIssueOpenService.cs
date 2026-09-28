using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BuildAI.AccIssueReturn.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BuildAI.AccIssueReturn.Revit;

// Revit API methods on this service are called by the modeless window's ExternalEvent.
internal sealed class FastIssueOpenService : IDisposable
{
    internal const string ReviewViewName = "BuildAI Issue Review";
    private readonly UIDocument uidoc;
    private readonly Dictionary<string, LinkMatch> links = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Document, Dictionary<string, ElementId>> documentIndexes = new();
    private readonly Dictionary<string, ResolvedElement?> elements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResolvedIssueContext> contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Transform> transforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Document> indexedLinkDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BoundingBoxXYZ?> modelBounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<int> temporaryControls = new();
    private List<AccIssue> loadedIssues = new();
    private bool indexesBuilt;
    private View3D? cachedView;
    private string? redIcon, blueIcon;
    private double paddingMetres = 1;
    internal long LastResolveElementsMs { get; private set; }
    internal long LastCalculateSectionBoxMs { get; private set; }

    internal FastIssueOpenService(UIDocument document) { uidoc = document; }
    internal double PaddingMetres { get => paddingMetres; set { paddingMetres = Math.Max(0.05, Math.Min(20, value)); contexts.Clear(); } }
    internal bool HasCached(string issueId) => contexts.ContainsKey(issueId);

    internal void Reset()
    {
        ClearHighlight(); Invalidate();
    }

    // Safe to call when another document is active: this only releases cached references.
    internal void Invalidate()
    {
        links.Clear(); documentIndexes.Clear(); elements.Clear(); contexts.Clear(); transforms.Clear(); indexedLinkDocuments.Clear(); modelBounds.Clear(); loadedIssues.Clear(); indexesBuilt = false; cachedView = null;
    }

    internal void BuildIndexes(IEnumerable<AccIssue> issues)
    {
        Reset();
        loadedIssues = issues.ToList();
        var needed = new HashSet<string>(loadedIssues.SelectMany(i => new[] { i.BuildAiRecord?.PrimaryElement?.LinkInstanceUid, i.BuildAiRecord?.SecondaryElement?.LinkInstanceUid })
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!), StringComparer.OrdinalIgnoreCase);
        foreach (var link in new FilteredElementCollector(uidoc.Document).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            if (!needed.Contains(link.UniqueId)) continue;
            var doc = link.GetLinkDocument(); if (doc == null) continue;
            links[link.UniqueId] = new LinkMatch { Link = link, Reason = doc.Title };
            transforms[link.UniqueId] = link.GetTotalTransform();
            indexedLinkDocuments[link.UniqueId] = doc;
            if (!documentIndexes.ContainsKey(doc))
                documentIndexes[doc] = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                    .ToElements().GroupBy(e => e.UniqueId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        }
        indexesBuilt = true;
    }

    internal ResolvedIssueContext Resolve(AccIssue issue, bool precise, XYZ? validatedCoordinate = null)
    {
        LastResolveElementsMs = 0; LastCalculateSectionBoxMs = 0;
        if (!indexesBuilt && loadedIssues.Count > 0) BuildIndexes(loadedIssues);
        if (contexts.TryGetValue(issue.Id, out var cached))
        {
            if (Valid(cached) && !(validatedCoordinate != null && !cached.CanOpenInRevit))
            {
                if (precise && !cached.Precise) Refine(cached);
                if (cached.SectionBox != null) LogSection(cached, true, 0);
                return cached;
            }
            BuildIndexes(loadedIssues);
        }
        var context = new ResolvedIssueContext { Issue = issue };
        contexts[issue.Id] = context;
        if (issue.BuildAiMappingStatus != BuildAiMappingStatus.Exact || issue.BuildAiRecord == null)
        { context.BlockingReason = "No BuildAI mapping"; return context; }
        var resolveTimer = Stopwatch.StartNew();
        context.Primary = ResolveElement(issue, "Primary", issue.BuildAiRecord.PrimaryElement);
        context.Secondary = ResolveElement(issue, "Secondary", issue.BuildAiRecord.SecondaryElement);
        LastResolveElementsMs = resolveTimer.ElapsedMilliseconds;
        if (context.Primary?.HostBounds == null && context.Secondary?.HostBounds == null)
            validatedCoordinate ??= ValidatedBuildAiPoint(issue);
        if (context.Primary == null && context.Secondary == null)
        {
            if (validatedCoordinate == null) { context.BlockingReason = "No Revit elements or validated coordinate"; return context; }
            context.Focus = validatedCoordinate; context.FocusMethod = "ValidatedPreviewCoordinate";
            context.CanOpenInRevit = true; context.Warning = "BuildAI elements were not resolved";
            CalculateSection(context, false); return context;
        }
        if (context.Primary?.HostBounds == null && context.Secondary?.HostBounds == null)
        {
            if (validatedCoordinate == null) { context.BlockingReason = "Resolved Revit elements have no usable bounds or validated coordinate"; return context; }
            context.Focus = validatedCoordinate; context.FocusMethod = "ValidatedPreviewCoordinate";
        }
        context.CanOpenInRevit = true;
        if (context.Primary == null) context.Warning = "Primary element was not resolved";
        if (context.Secondary == null) context.Warning = Join(context.Warning, "Secondary element was not resolved");
        CalculateSection(context, false);
        if (precise) Refine(context);
        return context;
    }

    private bool Valid(ResolvedIssueContext c)
    {
        if (c.Issue.BuildAiRecord != null)
            foreach (var reference in new[] { c.Issue.BuildAiRecord.PrimaryElement, c.Issue.BuildAiRecord.SecondaryElement })
                if (reference != null && !links.ContainsKey(reference.LinkInstanceUid) && RevitMapping.FindByUniqueId(uidoc.Document, reference.LinkInstanceUid) != null) return false;
        foreach (var pair in links)
        {
            var link = pair.Value.Link;
            if (!link.IsValidObject || link.GetLinkDocument() == null || !link.GetLinkDocument()!.Equals(indexedLinkDocuments[pair.Key])) return false;
            if (!transforms[pair.Key].AlmostEqual(link.GetTotalTransform())) return false;
        }
        foreach (var e in new[] { c.Primary, c.Secondary }.Where(e => e != null))
        {
            var link = e!.Match.Link;
            if (!link.IsValidObject || link.GetLinkDocument() == null || !link.GetLinkDocument()!.Equals(e.Element.Document)) return false;
            var old = transforms[link.UniqueId]; var now = link.GetTotalTransform();
            if (!old.AlmostEqual(now)) return false;
            if (e.Element == null || !e.Element.IsValidObject) return false;
        }
        return true;
    }

    private ResolvedElement? ResolveElement(AccIssue issue, string role, BuildAiRevitElementReference? reference)
    {
        var sw = Stopwatch.StartNew();
        var key = reference == null ? "" : reference.LinkInstanceUid + "|" + reference.ElementUniqueId + "|" + reference.ElementId;
        LinkMatch? match = null;
        var linkResolved = reference != null && links.TryGetValue(reference.LinkInstanceUid, out match);
        ResolvedElement? answer = null; var method = "None";
        bool? modelUidMatch = null;
        if (reference != null && linkResolved)
        {
            if (!string.IsNullOrWhiteSpace(reference.ModelUid))
                modelUidMatch = string.Equals(match!.Document.ProjectInformation?.UniqueId, reference.ModelUid, StringComparison.OrdinalIgnoreCase);
            if (elements.TryGetValue(key, out answer) && answer != null && answer.Element.IsValidObject) method = answer.Method;
            else
            {
                // RevitMapping is also the exact resolver used by Import preview.
                documentIndexes.TryGetValue(match!.Document, out var index);
                var element = RevitMapping.ResolveBuildAiElement(match, reference, out method, index);
                if (element != null)
                {
                    var bounds = RevitMapping.HostBounds(match!, new[] { element }, transforms[match!.Link.UniqueId]) ?? LocationBounds(match!, element);
                    answer = new ResolvedElement(match!, element, bounds, method);
                    elements[key] = answer;
                    if (!string.IsNullOrWhiteSpace(reference.ElementUniqueId) && index != null)
                        index[reference.ElementUniqueId] = element.Id;
                }
            }
        }
        sw.Stop();
        AccIssueReturnLog.Event("ACC_FAST_ELEMENT_RESOLUTION", new { issueIdSuffix = Short(issue.Id), role,
            linkInstanceUidHash = Hash(reference?.LinkInstanceUid), elementUniqueIdHash = Hash(reference?.ElementUniqueId),
            linkResolved, elementResolved = answer != null, modelUidMatch, resolutionMethod = method, elapsedMs = sw.ElapsedMilliseconds });
        return answer;
    }

    private void Refine(ResolvedIssueContext c)
    {
        c.Precise = true;
        if (c.Primary == null || c.Secondary == null) return;
        var sw = Stopwatch.StartNew();
        try
        {
            if (ImportService.TrySolidPoint(c.Primary.Match, c.Primary.Element, c.Secondary.Match, c.Secondary.Element,
                out var point, out _, out var method, 250) && sw.ElapsedMilliseconds < 350)
            {
                c.Focus = new XYZ(point[0], point[1], point[2]); c.FocusMethod = method;
                c.SolidIntersectionUsed = method == "SolidIntersection";
                c.Warning = c.Warning.Replace("Section Box calculated from bounding boxes", "").Trim(' ', ';');
                CalculateSection(c, true);
            }
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
    }

    private static BoundingBoxXYZ? LocationBounds(LinkMatch match, Element element)
    {
        var tr = match.Link.GetTotalTransform();
        XYZ? point = null;
        if (element.Location is LocationPoint locationPoint) point = tr.OfPoint(locationPoint.Point);
        else if (element.Location is LocationCurve locationCurve) point = tr.OfPoint(locationCurve.Curve.Evaluate(0.5, true));
        if (point == null) return null;
        const double half = 0.05 / 0.3048;
        return new BoundingBoxXYZ { Transform = Transform.Identity,
            Min = point - new XYZ(half, half, half), Max = point + new XYZ(half, half, half) };
    }

    private XYZ? ValidatedBuildAiPoint(AccIssue issue)
    {
        var data = issue.BuildAiRecord?.ResultData;
        if (data?.LocationValidated != true || !data.LocationX.HasValue || !data.LocationY.HasValue || !data.LocationZ.HasValue) return null;
        if (!string.Equals(data.CoordinateContract, "RevitHostInternalFeet", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(data.CoordinateContract, "revit_host_internal_feet", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(data.Units, "ft", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(data.Units, "feet", StringComparison.OrdinalIgnoreCase)) return null;
        var p = new XYZ(data.LocationX.Value, data.LocationY.Value, data.LocationZ.Value);
        if (new[] { p.X, p.Y, p.Z }.Any(x => double.IsNaN(x) || double.IsInfinity(x)) || p.IsZeroLength()) return null;
        var references = new[] { issue.BuildAiRecord?.PrimaryElement, issue.BuildAiRecord?.SecondaryElement };
        foreach (var reference in references.Where(r => r != null))
        {
            if (!links.TryGetValue(reference!.LinkInstanceUid, out var match)) continue;
            if (!modelBounds.TryGetValue(reference.LinkInstanceUid, out var bounds))
            { bounds = RevitMapping.ModelHostBounds(match); modelBounds[reference.LinkInstanceUid] = bounds; }
            if (bounds == null) continue;
            const double margin = 1 / 0.3048;
            if (p.X >= bounds.Min.X - margin && p.X <= bounds.Max.X + margin
                && p.Y >= bounds.Min.Y - margin && p.Y <= bounds.Max.Y + margin
                && p.Z >= bounds.Min.Z - margin && p.Z <= bounds.Max.Z + margin) return p;
        }
        return null;
    }

    private void CalculateSection(ResolvedIssueContext c, bool precise)
    {
        var sw = Stopwatch.StartNew();
        var a = c.Primary?.HostBounds; var b = c.Secondary?.HostBounds;
        if (c.Focus == null)
        {
            if (a != null && b != null)
            {
                var nearA = new double[3]; var nearB = new double[3];
                var amin = Values(a.Min); var amax = Values(a.Max); var bmin = Values(b.Min); var bmax = Values(b.Max);
                var overlap = true;
                for (var i = 0; i < 3; i++)
                {
                    if (amax[i] < bmin[i]) { nearA[i] = amax[i]; nearB[i] = bmin[i]; overlap = false; }
                    else if (bmax[i] < amin[i]) { nearA[i] = amin[i]; nearB[i] = bmax[i]; overlap = false; }
                    else nearA[i] = nearB[i] = (Math.Max(amin[i], bmin[i]) + Math.Min(amax[i], bmax[i])) / 2;
                }
                c.Focus = new XYZ((nearA[0] + nearB[0]) / 2, (nearA[1] + nearB[1]) / 2, (nearA[2] + nearB[2]) / 2);
                c.FocusMethod = overlap ? "HostBoundsIntersection" : "HostBoundsClosestPoints";
            }
            else
            {
                var box = a ?? b!;
                c.Focus = (box.Min + box.Max) / 2;
                c.FocusMethod = a != null ? "PrimaryBoundsCenter" : "SecondaryBoundsCenter";
            }
        }
        var p = paddingMetres / 0.3048;
        c.SectionBox = new BoundingBoxXYZ { Transform = Transform.Identity,
            Min = c.Focus - new XYZ(p, p, p), Max = c.Focus + new XYZ(p, p, p) };
        if (!precise && a != null && b != null) c.Warning = Join(c.Warning, "Section Box calculated from bounding boxes");
        sw.Stop();
        LastCalculateSectionBoxMs = sw.ElapsedMilliseconds;
        LogSection(c, false, sw.ElapsedMilliseconds);
    }

    private void LogSection(ResolvedIssueContext c, bool cacheHit, long elapsedMs)
    {
        var a = c.Primary?.HostBounds; var b = c.Secondary?.HostBounds;
        AccIssueReturnLog.Event("ACC_FAST_SECTIONBOX_CALCULATED", new { issueIdSuffix = Short(c.Issue.Id), focusMethod = c.FocusMethod,
            primaryBoundsAvailable = a != null, secondaryBoundsAvailable = b != null, solidIntersectionUsed = c.SolidIntersectionUsed,
            sectionBoxMin = Values(c.SectionBox!.Min), sectionBoxMax = Values(c.SectionBox.Max), paddingMm = paddingMetres * 1000,
            cacheHit, elapsedMs });
    }

    internal void Open(ResolvedIssueContext c, long preparationElapsedMs)
    {
        var sw = Stopwatch.StartNew(); var sectionApplied = false; var highlightApplied = false;
        long activateViewMs = 0, applySectionBoxMs = 0, cameraMs = 0, highlightMs = 0, zoomMs = 0;
        var suffix = Short(c.Issue.Id);
        try
        {
            if (!c.CanOpenInRevit || c.SectionBox == null) throw new InvalidOperationException(c.BlockingReason);
            ClearHighlight();
            var stage = Stopwatch.StartNew();
            var view = GetOrCreateView();
            if (view.ViewTemplateId != ElementId.InvalidElementId)
                throw new InvalidOperationException("The BuildAI Issue Review view has a view template. Remove its template to allow Section Box and graphics changes.");
            uidoc.ActiveView = view;
            activateViewMs = stage.ElapsedMilliseconds;
            stage.Restart();
            using (var tx = new Transaction(uidoc.Document, "BuildAI Issue Review: section box"))
            {
                tx.Start();
                try
                {
                    if (view.IsLocked) view.Unlock();
                    view.IsSectionBoxActive = true; view.SetSectionBox(c.SectionBox);
                    tx.Commit(); sectionApplied = true;
                }
                catch { tx.RollBack(); throw; }
            }
            applySectionBoxMs = stage.ElapsedMilliseconds;
            // The review view keeps its orthographic orientation. Fast Open does not need ACC camera data.
            stage.Restart();
            try
            {
                var highlight = Highlight(c, view);
                highlightApplied = highlight.Applied;
                if (highlight.Fallback) c.Warning = Join(c.Warning, "Native linked selection unavailable; visual fallback used");
                AccIssueReturnLog.Event("ACC_FAST_HIGHLIGHT_APPLIED", new { issueIdSuffix = suffix,
                    primaryResolved = c.Primary != null, secondaryResolved = c.Secondary != null,
                    primaryHighlighted = highlight.Primary, secondaryHighlighted = highlight.Secondary,
                    strategy = highlight.Method, highlightMethod = highlight.Method, linkedReferenceSupported = highlight.Native,
                    fallbackUsed = highlight.Fallback, elapsedMs = highlight.ElapsedMs, warning = c.Warning });
            }
            catch (Exception ex)
            {
                c.Warning = Join(c.Warning, "Element highlight unavailable: " + ex.GetType().Name);
                AccIssueReturnLog.Event("ACC_FAST_HIGHLIGHT_APPLIED", new { issueIdSuffix = suffix,
                    primaryResolved = c.Primary != null, secondaryResolved = c.Secondary != null,
                    primaryHighlighted = false, secondaryHighlighted = false,
                    strategy = "Unavailable", highlightMethod = "Unavailable", linkedReferenceSupported = false,
                    fallbackUsed = false, elapsedMs = stage.ElapsedMilliseconds, warning = c.Warning });
            }
            highlightMs = stage.ElapsedMilliseconds;
            stage.Restart();
            try
            {
                var uiView = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                if (uiView != null) uiView.ZoomToFit();
                else c.Warning = Join(c.Warning, "ZoomToFit skipped: review UIView is not ready");
            }
            catch (Exception ex) { c.Warning = Join(c.Warning, "ZoomToFit skipped: " + ex.GetType().Name); }
            zoomMs = stage.ElapsedMilliseconds;
        }
        catch (Exception ex)
        {
            c.BlockingReason = ex.Message;
            throw;
        }
        finally
        {
            sw.Stop();
            AccIssueReturnLog.Event("ACC_FAST_OPEN_FINISHED", new { issueIdSuffix = suffix,
                success = sectionApplied && highlightApplied, canOpenInRevit = c.CanOpenInRevit,
                primaryResolved = c.Primary != null, secondaryResolved = c.Secondary != null,
                sectionBoxApplied = sectionApplied, highlightApplied, warning = c.Warning,
                blockingReason = c.BlockingReason, resolveElementsMs = LastResolveElementsMs,
                calculateSectionBoxMs = LastCalculateSectionBoxMs, activateViewMs, applySectionBoxMs,
                cameraMs, highlightMs, zoomMs, totalMs = preparationElapsedMs + sw.ElapsedMilliseconds,
                totalElapsedMs = preparationElapsedMs + sw.ElapsedMilliseconds });
        }
    }

    private View3D GetOrCreateView()
    {
        if (cachedView != null && cachedView.IsValidObject) return cachedView;
        var doc = uidoc.Document;
        var view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
            .FirstOrDefault(v => !v.IsTemplate && !v.IsPerspective && (v.Name == ReviewViewName || v.Name == ReviewViewName + " (orthographic)"));
        if (view != null) return cachedView = view;
        var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional)
            ?? throw new InvalidOperationException("No 3D view family is available.");
        using var tx = new Transaction(doc, "BuildAI Issue Review: create view");
        tx.Start(); view = View3D.CreateIsometric(doc, type.Id);
        view.Name = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Any(v => v.Name == ReviewViewName)
            ? ReviewViewName + " (orthographic)" : ReviewViewName;
        tx.Commit();
        return cachedView = view;
    }

    private (bool Applied, bool Primary, bool Secondary, bool Native, bool Fallback, string Method, long ElapsedMs) Highlight(ResolvedIssueContext c, View3D view)
    {
        var sw = Stopwatch.StartNew();
        var found = new[] { c.Primary, c.Secondary }.Where(e => e != null).ToList();
        uidoc.Selection.SetElementIds(new List<ElementId>());
        try
        {
            var refs = found.Select(e => new Reference(e!.Element).CreateLinkReference(e.Match.Link)).ToList();
            uidoc.Selection.SetReferences(refs);
            var selected = uidoc.Selection.GetReferences();
            if (selected.Count != refs.Count || found.Any(e => !selected.Any(r => r.ElementId == e!.Match.Link.Id && r.LinkedElementId == e.Element.Id)))
                throw new InvalidOperationException("Linked reference selection was not retained by Revit.");
            sw.Stop(); return (true, c.Primary != null, c.Secondary != null, true, false, "SetReferences", sw.ElapsedMilliseconds);
        }
        catch (Exception)
        {
            uidoc.Selection.SetElementIds(new List<ElementId>());
            var manager = TemporaryGraphicsManager.GetTemporaryGraphicsManager(uidoc.Document);
            if (c.Primary?.HostBounds != null)
                temporaryControls.Add(manager.AddControl(new InCanvasControlData(Icon(true), Near(c.Primary.HostBounds, c.Focus!) + new XYZ(-0.08, 0, 0)), view.Id));
            if (c.Secondary?.HostBounds != null)
                temporaryControls.Add(manager.AddControl(new InCanvasControlData(Icon(false), Near(c.Secondary.HostBounds, c.Focus!) + new XYZ(0.08, 0, 0)), view.Id));
            sw.Stop(); return (temporaryControls.Count > 0, c.Primary != null, c.Secondary != null, false, true, "TemporaryGraphicsManager", sw.ElapsedMilliseconds);
        }
    }

    internal void ClearHighlight()
    {
        try { uidoc.Selection.SetElementIds(new List<ElementId>()); } catch { }
        try
        {
            var manager = TemporaryGraphicsManager.GetTemporaryGraphicsManager(uidoc.Document);
            foreach (var index in temporaryControls) { try { manager.RemoveControl(index); } catch (ArgumentException) { } }
        }
        catch { }
        temporaryControls.Clear();
    }

    private string Icon(bool primary)
    {
        var current = primary ? redIcon : blueIcon; if (current != null) return current;
        var path = Path.Combine(Path.GetTempPath(), "BuildAI-Issue-Review-" + Guid.NewGuid().ToString("N") + ".bmp");
        const int size = 24, stride = size * 3, offset = 54;
        var bytes = new byte[offset + stride * size];
        bytes[0] = 66; bytes[1] = 77; BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 2);
        BitConverter.GetBytes(offset).CopyTo(bytes, 10); BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(size).CopyTo(bytes, 18); BitConverter.GetBytes(size).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26); BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
        {
            var i = offset + y * stride + x * 3;
            var mark = Math.Abs(x - 12) < 3 || Math.Abs(y - 12) < 3;
            bytes[i] = mark ? (byte)(primary ? 20 : 245) : (byte)0;
            bytes[i + 1] = mark ? (byte)(primary ? 20 : 105) : (byte)128;
            bytes[i + 2] = mark ? (byte)(primary ? 245 : 25) : (byte)128;
        }
        File.WriteAllBytes(path, bytes);
        if (primary) redIcon = path; else blueIcon = path;
        return path;
    }

    public void Dispose()
    {
        Reset(); foreach (var path in new[] { redIcon, blueIcon }) if (path != null) try { File.Delete(path); } catch { }
    }

    private static XYZ Near(BoundingBoxXYZ b, XYZ p) => new XYZ(
        Math.Max(b.Min.X, Math.Min(b.Max.X, p.X)),
        Math.Max(b.Min.Y, Math.Min(b.Max.Y, p.Y)),
        Math.Max(b.Min.Z, Math.Min(b.Max.Z, p.Z)));
    private static double[] Values(XYZ p) => new[] { p.X, p.Y, p.Z };
    private static string Short(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value!.Substring(Math.Max(0, value.Length - 8));
    private static string Hash(string? value) => string.IsNullOrWhiteSpace(value) ? "" : BuildAiRevitIssuesClient.DiagnosticKey(value!).Hash;
    private static string Join(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a + "; " + b;
}

internal sealed class ResolvedElement
{
    internal ResolvedElement(LinkMatch match, Element element, BoundingBoxXYZ? bounds, string method)
    { Match = match; Element = element; HostBounds = bounds; Method = method; }
    internal LinkMatch Match { get; }
    internal Element Element { get; }
    internal BoundingBoxXYZ? HostBounds { get; }
    internal string Method { get; }
}

internal sealed class ResolvedIssueContext
{
    internal AccIssue Issue { get; set; } = null!;
    internal ResolvedElement? Primary { get; set; }
    internal ResolvedElement? Secondary { get; set; }
    internal XYZ? Focus { get; set; }
    internal BoundingBoxXYZ? SectionBox { get; set; }
    internal string FocusMethod { get; set; } = "None";
    internal string Warning { get; set; } = "";
    internal string BlockingReason { get; set; } = "";
    internal bool CanOpenInRevit { get; set; }
    internal bool SolidIntersectionUsed { get; set; }
    internal bool Precise { get; set; }
}
