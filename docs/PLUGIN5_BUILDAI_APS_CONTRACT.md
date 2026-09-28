# Plugin 5 — BuildAI / APS publication contract

## Important APS constraint
SVF2 is a streamed APS derivative. Autodesk does not expose a downloadable standalone 3D SVF2 package. Therefore the artifact sent to BuildAI is a **viewer publication descriptor**, not an SVF2 file.

## Preferred production flow
1. Revit plugin uploads the saved RVT to `POST /api/revit-publications/upload` as multipart form-data.
2. BuildAI backend stores APS Client Secret, uploads RVT to APS OSS, requests SVF2 translation and polls the manifest.
3. BuildAI stores `ApsUrn` and returns `ViewerPublication`.
4. Browser Viewer asks BuildAI `GET /api/aps/viewer-token`; BuildAI obtains a short-lived 2-legged APS token and returns it.
5. Autodesk Viewer loads `urn:<ApsUrn>` from APS.

## Direct-plugin test flow
For development only, setting `UseDirectAps=true` lets the plugin upload and translate itself, then POST the descriptor to BuildAI. Do not distribute APS Client Secret in a production desktop plugin.

## Upload request
`POST /api/revit-publications/upload`
- model: RVT binary
- projectId: string
- clashes: JSON

## Expected response
```json
{
  "publicationId": "...",
  "projectId": "...",
  "sourceFileName": "model.rvt",
  "apsUrn": "base64url-urn",
  "status": "ready",
  "viewerTokenEndpoint": "https://app.buildai.me/api/aps/viewer-token",
  "viewerEnvironment": "AutodeskProduction2"
}
```

## Viewer usage
Load document id as `urn:` + `apsUrn`. The token callback must call the BuildAI viewer-token endpoint. Keep Client Secret only on BuildAI backend.
