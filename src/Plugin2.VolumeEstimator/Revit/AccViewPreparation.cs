using System;
using System.Linq;
using Autodesk.Revit.DB;
using BuildAI.Core.Issues;
using BuildAI.RevitCompatibility;

namespace Plugin2.VolumeEstimator.Revit
{
    internal static class AccViewPreparation
    {
        public const string ViewName = "BuildAI Publication";
        public static bool Synchronizing { get; private set; }

        // Called only in the manual command's Revit API context. No Document
        // or Element is retained by the asynchronous publication operation.
        public static ApsCloudModelIdentity PrepareAndSynchronize(Document doc, Action<string> report = null)
        {
            if (!doc.IsModelInCloud)
                throw new InvalidOperationException("ACC publication requires a Revit cloud model. Open the model from Autodesk Docs and run Recalculate again.");
            var path = doc.GetCloudModelPath();
            var identity = new ApsCloudModelIdentity
            {
                HubId = doc.GetHubId(),
                ProjectGuid = path.GetProjectGUID().ToString(),
                ProjectId = doc.GetProjectId(),
                ModelGuid = path.GetModelGUID().ToString(),
                CloudModelUrn = doc.GetCloudModelUrn(),
                RequireExactModelMatch = true,
                DocumentTitle = doc.Title
            };
            if (string.IsNullOrWhiteSpace(identity.HubId) || !identity.IsUsable)
                throw new InvalidOperationException("The active model does not expose its ACC hub/project identity. Reopen the cloud model from Autodesk Docs.");
            if (string.IsNullOrWhiteSpace(identity.CloudModelUrn))
                throw new InvalidOperationException("Revit did not return the ACC model URN. Reopen the cloud model from Autodesk Docs and retry.");
            report?.Invoke("RECALCULATE CLOUD IDENTITY\nProject ID: " + identity.ProjectId + "\nRevit model URN: " + identity.CloudModelUrn);
            var view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(x => !x.IsTemplate && x.Name == ViewName);
            var created = view == null;
            using (var tx = new Transaction(doc, "BuildAI: prepare dedicated publication view"))
            {
                tx.Start();
                if (view == null)
                {
                    var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                        .First(x => x.ViewFamily == ViewFamily.ThreeDimensional);
                    view = View3D.CreateIsometric(doc, type.Id);
                    view.Name = ViewName;
                }
                CoordinationViewVisibility.EnsureFull(doc, view);
                view.IsSectionBoxActive = false;
                view.DetailLevel = ViewDetailLevel.Fine;
                view.DisplayStyle = DisplayStyle.FlatColors;
                if (tx.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("The BuildAI publication view could not be prepared.");
            }
            report?.Invoke("RECALCULATE PUBLICATION VIEW\nName: " + ViewName + "\nUniqueId: " + view.UniqueId +
                "\nAction: " + (created ? "created" : "reused") + "\nIssue views were not modified.");
            if (created)
                throw new InvalidOperationException(ViewName + " was created for website publication. In Collaborate > Publish Settings, add this 3D view to a selected publish set and save the settings, then run Recalculate again.");
            Synchronizing = true;
            try
            {
                if (doc.IsWorkshared)
                {
                    using (var central = new TransactWithCentralOptions())
                    using (var sync = new SynchronizeWithCentralOptions())
                    {
                        sync.Comment = "BuildAI Recalculate: publish dedicated website view";
                        doc.SynchronizeWithCentral(central, sync);
                    }
                }
                else doc.SaveCloudModel();
            }
            finally { Synchronizing = false; }
            return identity;
        }
    }
}
