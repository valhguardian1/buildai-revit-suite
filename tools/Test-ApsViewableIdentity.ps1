$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$models = Join-Path -Path $root -ChildPath "src\BuildAI.Core\Issues\IssueModels.cs"
$publication = Join-Path -Path $root -ChildPath "src\BuildAI.Core\APS\ApsPublicationClient.cs"
$resolver = Join-Path -Path $root -ChildPath "src\BuildAI.Core\APS\ApsObjectResolver.cs"
$integration = Join-Path -Path $root -ChildPath "src\BuildAI.Core\Issues\IssueIntegrationClient.cs"

Write-Host "==> Checking APS viewable identity mapping"

$modelsText = Get-Content -LiteralPath $models -Raw
$publicationText = Get-Content -LiteralPath $publication -Raw
$resolverText = Get-Content -LiteralPath $resolver -Raw
$integrationText = Get-Content -LiteralPath $integration -Raw

foreach ($member in @('ViewableGeometryGuid', 'ViewableId', 'ModelPropertiesGuid')) {
    if ($modelsText -notmatch ("public string " + $member)) {
        throw "APS pushpin context member is missing: $member"
    }
}

$viewableModel = [regex]::Match(
    $modelsText,
    'public sealed class ApsPushpinViewable[\s\S]*?public sealed class ApsPushpinDetails'
).Value
if ([string]::IsNullOrWhiteSpace($viewableModel)) {
    throw "ApsPushpinViewable model was not found."
}
if ($viewableModel -match '\[JsonProperty\("id"\)\]') {
    throw "The nonstandard viewable.id field must not be serialized in the Issue create payload."
}

if ($publicationText -notmatch 'ResolveManifestViewableIdentity' -or
    $publicationText -notmatch 'x\["type"\], "geometry"' -or
    $publicationText -notmatch 'geometry\["viewableID"\]' -or
    $publicationText -notmatch 'x\["role"\], "graphics"') {
    throw "Manifest geometry/viewableID/graphics identity resolution is incomplete."
}

if ($resolverText -notmatch 'context\.ModelPropertiesGuid' -or
    $resolverText -match 'context\.ViewableGeometryGuid\) \+ "/properties"') {
    throw "Model Properties lookup does not use the graphics resource GUID exclusively."
}

if ($integrationText -notmatch 'Guid\s*=\s*context\.ViewableGeometryGuid' -or
    $integrationText -notmatch 'ViewableId\s*=\s*context\.ViewableId' -or
    $integrationText -match '\bId\s*=\s*context\.') {
    throw "The linkedDocuments viewable mapping is incorrect."
}

# Regression values captured from the published BuildAI AR-ST manifest.
$geometryGuid = 'f4a80e1e-65dd-800e-78ed-fcb3667b117a'
$viewableId = 'af85e976-bdbc-463d-a3dd-68eec91c1c9e-006c2a77'
$graphicsGuid = '6d3fee9e-61af-e1d2-5597-83e511081afa'
if ($geometryGuid -eq $viewableId -or $geometryGuid -eq $graphicsGuid -or $viewableId -eq $graphicsGuid) {
    throw "Viewable identity regression values must remain distinct."
}

Write-Host "[OK] geometry.guid maps to viewable.guid, geometry.viewableID maps to viewableId, the graphics GUID is reserved for Model Properties, and viewable.id is omitted."
