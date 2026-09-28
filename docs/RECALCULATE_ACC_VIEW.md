# Recalculate: ACC 3D publication

The manual Volumes > Recalculate command estimates and queues materials as before,
then prepares `BuildAI Publication`, synchronizes the active Revit cloud model,
and publishes it through Autodesk Data Management. It uses the existing BuildAI
`GET /api/revit_aps_key` token provider, including token refresh. Manual Recalculate
does not upload RVT to BuildAI or create an Autodesk Issue.

`BuildAI Publication` is a dedicated website view. Recalculate creates it once
and reuses it on later runs; it neither renames nor edits the `BuildAI Coordination`
and `BuildAI AR-ST` views used by Issues. The log includes its name, UniqueId,
and whether it was created or reused.

## First use

Open a cloud model from Autodesk Docs and connect BuildAI. If the service view does
not exist, Recalculate creates it and reports that setup is required. In Revit
Collaborate > Publish Settings, include `BuildAI Publication` in a selected set,
save those settings, then run Recalculate again. This publish-set selection must be
performed in Revit; creating a view alone does not include it in ACC publication.

Subsequent runs restore full coordination visibility (including loaded links),
disable the section box, synchronize/save the cloud model, publish, and await the
exact 3D view. The service view is intentionally maintained by BuildAI. The active
user view is not changed. Unloaded links must be loaded in Revit if required.

The previous published manifest is not used to reject a newly synchronized set.
The resulting version still undergoes the existing target-viewable readiness
checks. Missing publish-set membership, permission errors, or translation errors
are shown as publication failures; the material calculation remains available.
Local-only RVT documents can still be calculated but cannot be published to ACC.
The command freezes the ACC project ID and `Document.GetCloudModelUrn()` in the
Revit API context. It resolves that exact item directly in Data Management. If
Revit supplies a version URN, its item relationship supplies the lineage URN.
The response type and ID must match; a rejected URN never falls back to a filename.
This avoids the folder GUID search that failed before publication in both supplied
2026-09-16 logs. That search inspected item-level extension data, while Autodesk
also exposes cloud GUIDs on versions. Existing Issue workflows retain their
previous lookup path.

## Website/backend discovery

The website retrieves the model from ACC with its own authorized APS session.
There is no new, assumed BuildAI endpoint or transmission of APS credentials.
The backend must already associate the BuildAI model with its ACC project/model
or item. Revit `ProjectInformation.UniqueId` is not the ACC model GUID.

1. Use the ACC Data Management project ID and the model's item/lineage URN.
2. Fetch the latest published item's version and its derivative relationship.
3. Load that version's derivative manifest, preserving its regional route.
4. Select the 3D geometry node named `BuildAI Publication`; its geometry GUID is
   the Viewer viewable GUID. Do not substitute the model-properties GUID.
5. Use APS Viewer to load that derivative and geometry. Refresh the latest version
   after another Recalculate; geometry identifiers may differ across versions.

The preparation log records `RECALCULATE CLOUD IDENTITY` and
`RECALCULATE PUBLICATION VIEW`. After translation it records `ACC VIEW PUBLICATION COMPLETED` followed
by the frozen Revit model UID, ACC cloud identity, item URN, version URN,
derivative URN, regional manifest URL, and viewable geometry GUID. These are also
available in `EstimationResult.AccView`; they are not posted to a speculative
backend API. If the website lacks the ACC model association, that association
must be implemented in the backend separately.

Only one manual ACC publication runs at a time. Synchronization performed by the
command suppresses its own automatic volume recalculation callback. Independent
user syncs retain the existing auto-calculation/RVT publication behavior.

## Validation limits

Build validation targets Revit 2023–2026. Scripted tests cover the ACC workflow's
success, failure, gate release, callback failures, direct model URNs and version
relationships without network calls.
An actual cloud model, configured publish set, and APS permissions are required
for an end-to-end Revit/ACC acceptance test and are not simulated by a build.

## Issue camera isolation

The auxiliary camera used for surface hit testing now restores the previous
viewport in `finally`, including after a missed hit or canvas failure. Only the
hit point is retained. The final Issue camera derives its eye, target, basis and
local framing from the pushpin, with an eye-to-target distance of two metres.
It retains the Viewer's projection schema and is applied immediately through a
viewport-only restore. A mismatched eye, target, up vector, projection or scale
rejects the capture rather than publishing a known stale camera. AutoCam state
is excluded from the Issue payload. Actual ACC reopening still requires a live
acceptance test, particularly for orthographic views.
