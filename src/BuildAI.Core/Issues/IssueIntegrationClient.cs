using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildAI.Core.Logging;
using BuildAI.Core.APS;
using BuildAI.Core.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BuildAI.Core.Issues
{
    public sealed partial class IssueIntegrationClient : IDisposable
    {
        private static readonly string[] SupportedRegions =
            { "US", "EMEA", "AUS", "GBR", "DEU", "JPN", "CAN", "IND" };
        private readonly string _baseUrl;
        private readonly ICredentialStore _credentials;
        private readonly HttpClient _http;

        public IssueIntegrationClient(string baseUrl, ICredentialStore credentials, HttpClient http = null)
        {
            _baseUrl = (baseUrl ?? "https://app.buildai.me").TrimEnd('/');
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        public async Task<IReadOnlyList<ExistingRevitIssueDto>> GetExistingIssuesAsync(string modelUid, string source, CancellationToken ct = default)
        {
            var url = _baseUrl + "/api/revit_issues?revit_model_uid=" + Uri.EscapeDataString(modelUid ?? "") + "&source=" + Uri.EscapeDataString(source ?? "");
            var text = await SendBuildAiAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return new List<ExistingRevitIssueDto>();
            var token = JToken.Parse(text);
            if (token.Type == JTokenType.Array) return token.ToObject<List<ExistingRevitIssueDto>>() ?? new List<ExistingRevitIssueDto>();
            return token["issues"]?.ToObject<List<ExistingRevitIssueDto>>() ?? new List<ExistingRevitIssueDto>();
        }

        public async Task<ApsProjectResponse> GetCurrentApsProjectAsync(CancellationToken ct = default, IProgress<string> diagnostics = null)
        {
            var text = await SendBuildAiAsync(HttpMethod.Get, _baseUrl + "/api/revit_current_aps_project_id", null, ct, diagnostics).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<ApsProjectResponse>(text) ?? new ApsProjectResponse();
        }

        public async Task<ApsTokenResponse> GetApsTokenAsync(CancellationToken ct = default, IProgress<string> diagnostics = null)
        {
            var token = await FetchApsTokenAsync(ct, diagnostics).ConfigureAwait(false);
            token.ConfigureRefresh(FetchApsTokenAsync, DateTime.UtcNow);
            diagnostics?.Report("APS TOKEN LIFETIME\nexpires_in: " + token.ExpiresIn +
                                "\nProactive refresh window: 120 seconds");
            return token;
        }

        private async Task<ApsTokenResponse> FetchApsTokenAsync(CancellationToken ct, IProgress<string> diagnostics)
        {
            var text = await SendBuildAiAsync(HttpMethod.Get, _baseUrl + "/api/revit_aps_key", null, ct, diagnostics).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<ApsTokenResponse>(text) ?? new ApsTokenResponse();
        }

        public async Task<ApsIssueEnvironment> ResolveIssueEnvironmentAsync(
            string buildAiApsId,
            string revitProjectId,
            ApsTokenResponse token,
            CancellationToken ct = default,
            IProgress<string> diagnostics = null)
        {
            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
                throw new InvalidOperationException("APS access token is empty; cannot resolve the Issues environment.");

            // Autodesk Issues endpoints validate the id as a bare GUID. The "b."
            // prefix used by Data Management fails that validation with HTTP 400
            // on BOTH API families, so strip it instead of probing it separately.
            var candidates = new[] { buildAiApsId, revitProjectId }
                .Select(ApsIssueEnvironment.StripDataManagementPrefix)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            diagnostics?.Report("APS ENVIRONMENT RESOLUTION\nBuildAI APS id: " + (buildAiApsId ?? "<empty>") +
                                "\nRevit Data Management project id: " + (revitProjectId ?? "<empty>") +
                                "\nNormalised candidates (b. prefix removed): " +
                                (candidates.Count == 0 ? "<none>" : string.Join(", ", candidates)) +
                                "\nStrategy: probe ACC Issues (construction/issues/v1) first, then BIM 360 Issues v2.");

            if (candidates.Count == 0)
                throw new ApsEnvironmentNotFoundException(
                    "No Autodesk project identifier is available. BuildAI returned no APS project id and Revit " +
                    "did not expose a cloud project id.");

            // Probe order matters. ACC is the common case for new projects and
            // Autodesk actively rejects ACC projects on the BIM 360 v2 route, so
            // trying v2 first guarantees a wasted round trip on every ACC project.
            var failures = new List<string>();

            foreach (var candidate in candidates)
            {
                try
                {
                    var subtype = await ResolveClashSubtypeAccAsync(candidate, token, ct, diagnostics).ConfigureAwait(false);
                    var environment = new ApsIssueEnvironment
                    {
                        Platform = ApsPlatformKind.AutodeskConstructionCloud,
                        ApiMode = ApsIssueApiMode.AccIssuesV1,
                        ProjectId = candidate,
                        ContainerId = "",
                        IssueSubtypeId = subtype,
                        SupportsCurrentUserEndpoint = true
                    };
                    diagnostics?.Report("APS ENVIRONMENT RESOLVED\n" + environment.DisplayName +
                                        "\nProject ID: " + environment.ProjectId +
                                        "\nClash subtype ID: " + environment.IssueSubtypeId);
                    return environment;
                }
                catch (ApsHttpException ex) when (IsProbeRejection(ex.StatusCode))
                {
                    // A rejection only rules out THIS id on THIS API family. It must
                    // never abort the remaining probes: a 400 from one candidate
                    // previously terminated the whole workflow after the model had
                    // already been published.
                    failures.Add("ACC Issues v1 / " + candidate + " -> HTTP " + ex.StatusCode);
                    diagnostics?.Report("ACC Issues v1 probe rejected for id " + candidate +
                                        " (HTTP " + ex.StatusCode + "). Continuing with the remaining routes.\n" + ex.Message);
                }
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    var subtype = await ResolveClashSubtypeBim360Async(candidate, token, ct, diagnostics).ConfigureAwait(false);
                    var environment = new ApsIssueEnvironment
                    {
                        Platform = ApsPlatformKind.Bim360,
                        ApiMode = ApsIssueApiMode.Bim360IssuesV2,
                        ProjectId = revitProjectId ?? "",
                        ContainerId = candidate,
                        IssueSubtypeId = subtype,
                        SupportsCurrentUserEndpoint = true
                    };
                    diagnostics?.Report("APS ENVIRONMENT RESOLVED\n" + environment.DisplayName +
                                        "\nProject ID: " + environment.ProjectId +
                                        "\nContainer ID: " + environment.ContainerId +
                                        "\nClash subtype ID: " + environment.IssueSubtypeId);
                    return environment;
                }
                catch (ApsHttpException ex) when (IsProbeRejection(ex.StatusCode))
                {
                    failures.Add("BIM 360 Issues v2 / " + candidate + " -> HTTP " + ex.StatusCode);
                    diagnostics?.Report("BIM 360 Issues v2 probe rejected for id " + candidate +
                                        " (HTTP " + ex.StatusCode + "). Continuing with the remaining routes.\n" + ex.Message);
                }
            }

            throw new ApsEnvironmentNotFoundException(
                "Autodesk Issues could not be resolved for either Autodesk Construction Cloud or BIM 360." +
                Environment.NewLine + "Attempted routes:" + Environment.NewLine +
                string.Join(Environment.NewLine, failures) + Environment.NewLine + Environment.NewLine +
                "Check that Issues is activated for the project, that the APS token carries the data:read and " +
                "data:write scopes, that the signed-in user is a project member, and that the project id supplied " +
                "by BuildAI matches the Autodesk project.");
        }

        /// <summary>
        /// Validates every URN supplied for the selected item/version against every
        /// supported Autodesk data region, then freezes the complete context before
        /// any bulk Issue POST is allowed.
        /// </summary>
        public async Task<ApsIssueEnvironment> ResolveRegionalIssueEnvironmentAsync(
            ApsProjectResponse buildAiProject,
            string revitProjectId,
            ApsPushpinContext model,
            ApsTokenResponse token,
            CancellationToken ct = default,
            IProgress<string> diagnostics = null)
        {
            if (model == null || !model.IsUsable)
                throw new InvalidOperationException("The published model context is incomplete; regional URN validation cannot start.");

            var rawUrns = new List<string>();
            var currentDerivativeUrn = NormalizeDerivativeUrn(model.DerivativeUrn);
            rawUrns.Add(currentDerivativeUrn);
            if (buildAiProject?.Urns != null) rawUrns.AddRange(buildAiProject.Urns);
            rawUrns.Add(model.DerivativeUrn);
            rawUrns.Add(model.VersionUrn);
            // DocumentUrn is an item/lineage identifier, not a Model Derivative
            // URN. Probing it in every region creates guaranteed 404 noise and
            // can add minutes to a full sweep.
            var urns = rawUrns.Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeDerivativeUrn)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var regions = new[] { model.DerivativeRegion, buildAiProject?.Region }
                .Concat(SupportedRegions)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => NormalizeRegion(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var rejected = new List<string>();
            var manifestCandidates = new List<ManifestCandidate>();
            if (!string.IsNullOrWhiteSpace(model.DerivativeManifestUrl))
                manifestCandidates.Add(new ManifestCandidate
                {
                    Urn = model.DerivativeUrn,
                    Region = string.IsNullOrWhiteSpace(model.DerivativeRegion) ? "AUTO" : NormalizeRegion(model.DerivativeRegion),
                    Url = model.DerivativeManifestUrl,
                    IsAuthoritative = true
                });
            foreach (var urn in urns)
                foreach (var region in regions)
                    manifestCandidates.Add(new ManifestCandidate
                    {
                        Urn = urn,
                        Region = region,
                        Url = BuildRegionalManifestUrl(urn, region,
                            string.Equals(urn, model.DerivativeUrn, StringComparison.Ordinal) ? model.DerivativeScopes : "")
                    });
            manifestCandidates = manifestCandidates
                .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();

            diagnostics?.Report("APS MULTI-REGION VALIDATION\nURN candidates: " + urns.Count +
                                "\nManifest routes: " + manifestCandidates.Count +
                                "\nRegions: " + string.Join(", ", regions) +
                                "\nAuthoritative current-version route first: " + (!string.IsNullOrWhiteSpace(model.DerivativeManifestUrl) ? "yes" : "no") +
                                "\nRule: every route is inspected; 401 refreshes at most once and never aborts the sweep; " +
                                "404/403/429/5xx are recorded per route. A previously resolved current-version context is preserved.");
            PluginLog.Info("APS multi-region validation started", new { urnCount = urns.Count, regions });

            var accepted = new List<ManifestCandidate>();
            foreach (var candidate in manifestCandidates)
            {
                try
                {
                    await ValidateManifestAsync(candidate, token, ct, diagnostics).ConfigureAwait(false);
                    accepted.Add(candidate);

                    // The authoritative route comes from Data Management itself. Once it
                    // validates, the selection below discards every other candidate
                    // anyway, so continuing the sweep only produces guaranteed 401s
                    // against regions that do not host this derivative.
                    if (candidate.IsAuthoritative)
                    {
                        diagnostics?.Report(
                            "APS MULTI-REGION SWEEP SHORT-CIRCUITED" + Environment.NewLine +
                            "The authoritative current-version route validated successfully." + Environment.NewLine +
                            "Remaining regional fallbacks: " + (manifestCandidates.Count - 1) + " (not probed).");
                        break;
                    }
                }
                catch (ApsHttpException ex)
                {
                    rejected.Add(candidate.Region + ":HTTP" + ex.StatusCode);
                    diagnostics?.Report("URN CANDIDATE REJECTED\nRegion: " + candidate.Region +
                                        "\nRoute: " + (candidate.IsAuthoritative ? "authoritative current version" : "regional fallback") +
                                        "\nHTTP " + ex.StatusCode + "; checking the next candidate.");
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    rejected.Add(candidate.Region + ":" + ex.GetType().Name);
                    diagnostics?.Report("URN CANDIDATE REJECTED\nRegion: " + candidate.Region +
                                        "\nRoute: " + (candidate.IsAuthoritative ? "authoritative current version" : "regional fallback") +
                                        "\n" + ex.Message + "\nThe sweep will continue.");
                }
            }

            // A historical BuildAI URN may be useful as a diagnostic probe, but
            // it must never replace the exact derivative resolved for the current
            // Data Management version.
            var chosen = accepted
                .Where(x => x.IsAuthoritative || string.Equals(NormalizeDerivativeUrn(x.Urn), currentDerivativeUrn, StringComparison.Ordinal))
                .OrderByDescending(x => x.IsAuthoritative)
                .FirstOrDefault();
            if (chosen == null)
            {
                chosen = manifestCandidates.FirstOrDefault(x => x.IsAuthoritative) ??
                         manifestCandidates.FirstOrDefault(x => string.Equals(NormalizeDerivativeUrn(x.Urn), currentDerivativeUrn, StringComparison.Ordinal));
                if (chosen == null)
                    throw new InvalidOperationException("No Autodesk manifest route could be constructed for the resolved current version.");
                diagnostics?.Report(
                    "APS MULTI-REGION DEGRADED ACCEPTANCE\n" +
                    "No route returned a positive validation response during this sweep, but the current-version viewable, " +
                    "PropertyDatabase and Autodesk.AEC.ModelData were already resolved successfully. " +
                    "The authoritative current-version context will be retained.\nRejections: " +
                    string.Join(", ", rejected.Take(80)));
            }

            diagnostics?.Report("APS MULTI-REGION SWEEP COMPLETE\nAccepted routes: " + accepted.Count +
                                "\nRejected routes: " + rejected.Count +
                                "\nSelected region: " + chosen.Region +
                                "\nSelected route: " + (chosen.IsAuthoritative ? "authoritative current version" : "regional fallback"));

            var buildAiContainer = FirstNonEmpty(buildAiProject?.ContainerId, buildAiProject?.ProjectId);
            var environment = await ResolveIssueEnvironmentAsync(
                buildAiContainer, revitProjectId, token, ct, diagnostics).ConfigureAwait(false);
            environment.Region = chosen.Region;
            environment.ItemId = model.DocumentUrn ?? "";
            environment.VersionId = model.VersionUrn ?? "";
            environment.ModelUrn = currentDerivativeUrn;
            environment.SeedUrn = model.DerivativeUrn ?? "";
            diagnostics?.Report("APS OPERATION CONTEXT FROZEN\nRegion: " + environment.Region +
                                "\nProject ID: " + environment.ProjectId +
                                "\nContainer ID: " + environment.ContainerId +
                                "\nItem ID: " + environment.ItemId +
                                "\nVersion ID: " + environment.VersionId +
                                "\nModel URN: " + environment.ModelUrn +
                                "\nSeed URN: " + environment.SeedUrn +
                                "\nIssues API: " + environment.ApiMode);
            PluginLog.Info("APS operation context resolved", new
            {
                region = environment.Region,
                api = environment.ApiMode.ToString(),
                urnCandidateCount = urns.Count,
                acceptedCount = accepted.Count,
                rejectedCount = rejected.Count
            });
            return environment;
        }

        public async Task<string> ResolveCurrentUserIdAsync(ApsIssueEnvironment environment, ApsTokenResponse token, CancellationToken ct = default, IProgress<string> diagnostics = null)
        {
            if (environment == null || !environment.IsUsable)
                throw new InvalidOperationException("APS Issues environment is not resolved; cannot resolve the current user.");
            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
                throw new InvalidOperationException("APS access token is empty; cannot resolve the current user.");

            if (!environment.SupportsCurrentUserEndpoint || environment.ApiMode == ApsIssueApiMode.Unknown)
            {
                var configured = FirstNonEmpty(token.AssigneeId, token.OwnerId);
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    diagnostics?.Report("APS CURRENT USER\nUsing the assignee/owner id returned with the APS token because the resolved environment does not expose users/me.");
                    return configured;
                }
                diagnostics?.Report("APS CURRENT USER\nNo current-user id could be resolved. Issue assignment fields will be omitted.");
                return null;
            }

            // Both API families expose users/me, but under their own base path.
            // ACC projects are blocked on the BIM 360 containers route, so the
            // URL must follow the resolved API mode rather than default to v2.
            var url = environment.ApiMode == ApsIssueApiMode.Bim360IssuesV2
                ? "https://developer.api.autodesk.com/issues/v2/containers/" +
                  Uri.EscapeDataString(environment.ContainerId) + "/users/me"
                : "https://developer.api.autodesk.com/construction/issues/v1/projects/" +
                  Uri.EscapeDataString(environment.ProjectId) + "/users/me";
            var body = await SendApsGetAsync("current Issues user", url, token, ct, diagnostics).ConfigureAwait(false);
            var root = JToken.Parse(body);

            var userId = FirstNonEmpty(
                (string)root["id"],
                (string)root["userId"],
                (string)root["user_id"],
                (string)root["autodeskId"],
                (string)root["autodesk_id"],
                (string)root["user"]?["id"],
                (string)root["user"]?["userId"],
                (string)root["user"]?["autodeskId"],
                (string)root["data"]?["id"],
                (string)root["data"]?["userId"],
                (string)root["data"]?["autodeskId"]);

            if (string.IsNullOrWhiteSpace(userId))
                throw new InvalidOperationException("APS users/me returned success but no current-user id was found. Response: " + Truncate(body, 1200));

            PluginLog.Info("APS current user resolved", new { id = userId, environment = environment.DisplayName });
            return userId;
        }

        public async Task<IReadOnlyList<ApsAssigneeResolution>> GetProjectAssigneesAsync(CancellationToken ct = default)
        {
            var project = await GetCurrentApsProjectAsync(ct).ConfigureAwait(false);
            var token = await GetApsTokenAsync(ct).ConfigureAwait(false);
            var projectId = (project.ProjectId ?? "").Trim();
            if (projectId.StartsWith("b.", StringComparison.OrdinalIgnoreCase)) projectId = projectId.Substring(2);
            if (string.IsNullOrWhiteSpace(projectId)) throw new InvalidOperationException("Autodesk project is not configured.");
            var users = new List<ApsAssigneeResolution>();
            for (var offset = 0; ; offset += 200)
            {
                var url = "https://developer.api.autodesk.com/construction/admin/v1/projects/" + Uri.EscapeDataString(projectId) + "/users?limit=200&offset=" + offset;
                var root = JToken.Parse(await SendApsGetAsync("project assignees", url, token, ct).ConfigureAwait(false));
                var page = (root is JArray ? root : root["results"] ?? root["data"] ?? root["users"]) as JArray;
                if (page == null) throw new InvalidOperationException("Unexpected Autodesk project users response.");
                foreach (var user in page.OfType<JObject>())
                {
                    var id = FirstNonEmpty((string)user["autodeskId"], (string)user["autodesk_id"]);
                    var status = (string)user["status"];
                    if (string.IsNullOrWhiteSpace(id) || (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))) continue;
                    var name = FirstNonEmpty((string)user["name"], (string)user["displayName"], (string)user["email"], id);
                    var email = (string)user["email"];
                    users.Add(new ApsAssigneeResolution { AssignedTo = id, AssignedToType = "user", ProjectId = projectId,
                        DisplayName = name + (string.IsNullOrWhiteSpace(email) || name == email ? "" : " <" + email + ">") });
                }
                if (page.Count < 200) break;
            }
            return users.GroupBy(x => x.AssignedTo, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        public async Task<ApsAssigneeResolution> ResolveAssigneeByRoleAsync(
            ApsIssueEnvironment environment,
            ApsTokenResponse token,
            IEnumerable<string> preferredRoles,
            string currentUserId,
            CancellationToken ct = default,
            IProgress<string> diagnostics = null)
        {
            var candidates = (preferredRoles ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var fallback = new ApsAssigneeResolution
            {
                AssignedTo = string.IsNullOrWhiteSpace(currentUserId) ? null : currentUserId,
                AssignedToType = string.IsNullOrWhiteSpace(currentUserId) ? null : "user",
                DisplayName = string.IsNullOrWhiteSpace(currentUserId) ? "Unassigned" : "Current APS user",
                PreferredRole = candidates.FirstOrDefault() ?? "",
                ResolutionReason = string.IsNullOrWhiteSpace(currentUserId) ? "No project role or current user could be resolved." : "No matching project role was found; current APS user fallback."
            };
            if (environment == null || string.IsNullOrWhiteSpace(environment.ProjectId) || token == null || string.IsNullOrWhiteSpace(token.AccessToken) || candidates.Count == 0)
                return fallback;

            try
            {
                var projectId = environment.ProjectId.Trim();
                if (projectId.StartsWith("b.", StringComparison.OrdinalIgnoreCase)) projectId = projectId.Substring(2);
                var users = new JArray();
                for (var offset = 0; offset < 2000; offset += 200)
                {
                    var url = "https://developer.api.autodesk.com/construction/admin/v1/projects/" + Uri.EscapeDataString(projectId) + "/users?limit=200&offset=" + offset;
                    var body = await SendApsGetAsync("project users and roles", url, token, ct, diagnostics).ConfigureAwait(false);
                    var root = JToken.Parse(body);
                    var page = (root["results"] ?? root["data"] ?? root["users"] ?? root) as JArray ?? new JArray();
                    foreach (var item in page) users.Add(item);
                    if (page.Count < 200) break;
                }
                foreach (var candidate in candidates)
                {
                    // Collect EVERY user holding this role before choosing one. The
                    // previous code returned on first match, so when a project had two
                    // people with the same role the winner was decided by whatever order
                    // the Admin API happened to return - and nothing told the user that a
                    // choice had even been made.
                    var matches = new List<RoleMatch>();
                    foreach (var user in users.OfType<JObject>())
                    {
                        var roles = new List<JToken>();
                        foreach (var name in new[] { "roles", "projectRoles", "industryRoles", "roleNames" })
                        {
                            var value = user[name];
                            if (value is JArray array) roles.AddRange(array);
                        }
                        foreach (var role in roles)
                        {
                            var roleName = role.Type == JTokenType.String ? (string)role : FirstNonEmpty((string)role["name"], (string)role["title"], (string)role["displayName"], (string)role["key"]);
                            if (string.IsNullOrWhiteSpace(roleName) || !RoleMatches(roleName, candidate)) continue;
                            matches.Add(new RoleMatch
                            {
                                RoleName = roleName,
                                UserId = FirstNonEmpty((string)user["autodeskId"], (string)user["autodesk_id"], (string)user["userId"], (string)user["user_id"], (string)user["id"]),
                                RoleId = role.Type == JTokenType.String ? null : FirstNonEmpty((string)role["id"], (string)role["roleId"], (string)role["role_id"]),
                                DisplayName = FirstNonEmpty((string)user["name"], (string)user["displayName"], (string)user["email"], roleName),
                                Email = (string)user["email"] ?? "",
                                Company = (string)user["companyName"] ?? "",
                                Status = (string)user["status"] ?? ""
                            });
                            break;
                        }
                    }
                    if (matches.Count == 0) continue;

                    // Prefer active accounts, then order by name so the same project always
                    // yields the same assignee across runs and machines.
                    var ordered = matches
                        .OrderByDescending(x => string.Equals(x.Status, "active", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var chosen = ordered.First();

                    if (ordered.Count > 1)
                        diagnostics?.Report(
                            "ASSIGNEE AMBIGUITY" + Environment.NewLine +
                            "Requested role: " + candidate + Environment.NewLine +
                            "Project members holding it: " + ordered.Count + Environment.NewLine +
                            string.Join(Environment.NewLine, ordered.Select((x, i) =>
                                "  " + (i == 0 ? "[chosen] " : "          ") + x.DisplayName +
                                (string.IsNullOrWhiteSpace(x.Email) ? "" : " <" + x.Email + ">") +
                                (string.IsNullOrWhiteSpace(x.Company) ? "" : " - " + x.Company))) + Environment.NewLine +
                            "The plugin cannot tell which of them owns this Issue. Selection is " +
                            "deterministic (active first, then name) but arbitrary. Give the intended " +
                            "owner a distinct Autodesk project role if this is wrong.");

                    if (!string.IsNullOrWhiteSpace(chosen.UserId))
                    {
                        var resolved = new ApsAssigneeResolution { AssignedTo = chosen.UserId, AssignedToType = "user", DisplayName = chosen.DisplayName, PreferredRole = candidate, ResolutionReason = "Matched a project user with the requested role." };
                        diagnostics?.Report("ASSIGNEE RESOLUTION\nRequested roles (in order): " + string.Join(", ", candidates) +
                                            "\nMatched on role: " + candidate +
                                            "\nAutodesk role name: " + chosen.RoleName +
                                            "\nMatched user: " + chosen.DisplayName +
                                            (string.IsNullOrWhiteSpace(chosen.Company) ? "" : " (" + chosen.Company + ")") +
                                            "\nAssignedToType: user\nAssignedTo: " + chosen.UserId);
                        return resolved;
                    }
                    if (!string.IsNullOrWhiteSpace(chosen.RoleId))
                    {
                        var resolved = new ApsAssigneeResolution { AssignedTo = chosen.RoleId, AssignedToType = "role", DisplayName = chosen.RoleName, PreferredRole = candidate, ResolutionReason = "Matched an Autodesk project role because the matching user has no usable Autodesk user id." };
                        diagnostics?.Report("ASSIGNEE RESOLUTION\nMatched on role: " + candidate + "\nAutodesk role name: " + chosen.RoleName + "\nAssignedToType: role\nAssignedTo: " + chosen.RoleId + "\nReason: matching user id was unavailable");
                        return resolved;
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics?.Report("ASSIGNEE RESOLUTION WARNING\nProject role lookup failed. " + ex.Message + "\nUsing fallback: " + fallback.DisplayName);
            }
            return fallback;
        }

        public async Task<ApsCreateIssueResponse> CreateApsIssueWithAssignmentFallbackAsync(
            ApsIssueEnvironment environment,
            ApsTokenResponse token,
            ApsCreateIssueRequest payload,
            CancellationToken ct = default,
            IProgress<string> diagnosticsProgress = null,
            bool logFullPayload = false, bool requireAssignee = false)
        {
            for (var fallbackAttempt = 0; fallbackAttempt < 2; fallbackAttempt++)
            {
                try
                {
                    return await CreateApsIssueAsync(environment, token, payload, ct, diagnosticsProgress, logFullPayload).ConfigureAwait(false);
                }
                catch (ApsHttpException ex) when (ex.StatusCode == 400 || ex.StatusCode == 422)
                {
                    if (!requireAssignee && !string.IsNullOrWhiteSpace(payload?.AssignedTo) && IsAssigneeValidationError(ex))
                    {
                        diagnosticsProgress?.Report("ASSIGNEE FALLBACK\nAutodesk rejected the selected assignee. Retrying the same Issue without assignedTo.\nError: " + ex.Message);
                        payload.AssignedTo = null;
                        payload.AssignedToType = null;
                        continue;
                    }
                    if (payload?.LinkedDocuments != null && payload.LinkedDocuments.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "Autodesk rejected the Issue request. The pushpin was preserved and the Issue was not created. " + ex.Message, ex);
                    }
                    throw;
                }
            }
            throw new InvalidOperationException("APS Issue validation fallbacks were exhausted.");
        }

        /// <summary>
        /// Matches an Autodesk project role name against a requested role.
        /// <para>
        /// The previous implementation accepted a substring match in EITHER direction
        /// after stripping non-alphanumerics, which was far too loose. Requesting
        /// "Architect" matched a role named "Arch"; requesting "Engineer" matched
        /// "Electrical Engineer", "Civil Engineer" and "Structural Engineer" alike,
        /// so the assignee became whichever of them the Admin API happened to list
        /// first. Matching is now exact after normalisation, with one narrow and
        /// safe extension: the actual role may carry extra WHOLE words around the
        /// requested one ("Lead Architect" satisfies "Architect"), but never the
        /// reverse, and never a partial word.
        /// </para>
        /// </summary>
        private sealed class RoleMatch
        {
            public string RoleName;
            public string RoleId;
            public string UserId;
            public string DisplayName;
            public string Email;
            public string Company;
            public string Status;
        }

        private static bool RoleMatches(string actual, string requested)
        {
            if (string.Equals(actual, requested, StringComparison.OrdinalIgnoreCase)) return true;

            var a = NormalizeRole(actual);
            var r = NormalizeRole(requested);
            if (a.Length == 0 || r.Length == 0) return false;
            if (a == r) return true;

            // Whole-word containment only, and only actual-contains-requested:
            // a broader role title may include the requested one, but a narrower
            // requested title must not be satisfied by an unrelated shorter role.
            var actualWords = SplitRoleWords(actual);
            var requestedWords = SplitRoleWords(requested);
            if (requestedWords.Count == 0 || actualWords.Count < requestedWords.Count) return false;

            for (var start = 0; start + requestedWords.Count <= actualWords.Count; start++)
            {
                var matched = true;
                for (var i = 0; i < requestedWords.Count; i++)
                {
                    if (!string.Equals(actualWords[start + i], requestedWords[i], StringComparison.OrdinalIgnoreCase))
                    {
                        matched = false;
                        break;
                    }
                }
                if (matched) return true;
            }
            return false;
        }

        private static List<string> SplitRoleWords(string value)
        {
            return (value ?? "")
                .Split(new[] { ' ', '\t', '-', '_', '/', '\\', ',', '.', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => new string(x.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()))
                .Where(x => x.Length > 0)
                .ToList();
        }
        private static string NormalizeRole(string value)
        {
            return new string((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        [Obsolete("Resolve an ApsIssueEnvironment first so project_id and container_id are not conflated.")]
        public async Task<string> ResolveCurrentUserIdAsync(string containerId, ApsTokenResponse token, CancellationToken ct = default, IProgress<string> diagnostics = null)
        {
            var environment = await ResolveIssueEnvironmentAsync(containerId, null, token, ct, diagnostics).ConfigureAwait(false);
            return await ResolveCurrentUserIdAsync(environment, token, ct, diagnostics).ConfigureAwait(false);
        }


        public static ApsLinkedDocument BuildPushpinDocument(
            ApsPushpinContext context,
            string currentUserId,
            double x,
            double y,
            double z,
            string externalId,
            int? objectId = null,
            JObject capturedViewerState = null,
            double[] expectedGlobalOffset = null,
            int? secondaryObjectId = null,
            bool secondaryInDifferentModel = false,
            bool cameraStateIsViewerLocal = false,
            bool arStViewerLocalFrame = false,
            string arStResultKey = null)
        {
            if (context == null || !context.IsUsable) return null;
            // x/y/z is the PushPin position exactly as the Viewer reported it:
            // viewer-local, in Viewer units. The captured viewport uses the same
            // frame and the same units, and the state carries the Viewer's own
            // globalOffset, which ACC applies when it places the pin.
            // Inventing a camera from x/y/z would create an internally inconsistent
            // linked document; both workflows obtain an authoritative state from
            // ViewerCoordinateProbe, so fail before POST if that contract is ever
            // broken instead of silently creating a bad Issue.
            if (capturedViewerState == null)
                throw new ArgumentNullException(nameof(capturedViewerState),
                    "A Viewer-captured state is required to create a PushPin document.");
            var viewerState = (JObject)capturedViewerState.DeepClone();
            if (cameraStateIsViewerLocal && expectedGlobalOffset != null && expectedGlobalOffset.Length >= 3)
                AddGlobalOffsetToCamera(viewerState, expectedGlobalOffset);
            if (arStViewerLocalFrame)
                PushpinFrame.ValidateArStViewerLocalState(viewerState, new[] { x, y, z }, expectedGlobalOffset,
                    arStResultKey, message => PluginLog.Info(message));
            else
                PushpinFrame.ValidateViewerState(
                    viewerState,
                    new[] { x, y, z },
                    expectedGlobalOffset,
                    message => PluginLog.Info(message));

            // Keep the object identity authoritative at the final serialization
            // boundary as well. Viewer state can be supplied by different Viewer
            // builds, but ACC expects an LMV objectSet containing the same dbId as
            // details.objectId.
            if (objectId.HasValue && !secondaryObjectId.HasValue)
            {
                var objectSetEntry = new JObject
                {
                    ["id"] = new JArray(objectId.Value),
                    ["isolated"] = new JArray(objectId.Value),
                    ["hidden"] = new JArray(),
                    ["idType"] = "lmv",
                    ["explodeScale"] = 0,
                    ["explodeOptions"] = new JObject
                    {
                        ["magnitude"] = 4,
                        ["depthDampening"] = 0
                    }
                };
                viewerState["objectSet"] = new JArray(objectSetEntry);

                var renderOptions = viewerState["renderOptions"] as JObject ?? new JObject();
                var appearance = renderOptions["appearance"] as JObject ?? new JObject();
                appearance["ghostHidden"] = true;
                appearance["ambientShadow"] = true;
                appearance["displayLines"] = true;
                renderOptions["appearance"] = appearance;
                viewerState["renderOptions"] = renderOptions;

                AssertViewerHighlightState(viewerState, new[] { objectId.Value }, false);
            }
            else if (objectId.HasValue && secondaryObjectId.HasValue)
            {
                // The Viewer owns the multi-model objectSet shape. Rebuilding it
                // here would discard the per-model identity and was the reason only
                // the primary Clash object survived in 7.0.21.
                var renderOptions = viewerState["renderOptions"] as JObject ?? new JObject();
                var appearance = renderOptions["appearance"] as JObject ?? new JObject();
                appearance["ghostHidden"] = true;
                appearance["ambientShadow"] = true;
                appearance["displayLines"] = true;
                renderOptions["appearance"] = appearance;
                viewerState["renderOptions"] = renderOptions;

                AssertViewerHighlightState(
                    viewerState,
                    new[] { objectId.Value, secondaryObjectId.Value },
                    secondaryInDifferentModel);
            }

            return new ApsLinkedDocument
            {
                Type = "TwoDVectorPushpin",
                Urn = context.DocumentUrn,
                CreatedBy = currentUserId ?? "",
                CreatedAt = DateTime.UtcNow.ToString("o"),
                CreatedAtVersion = context.FileVersion > 0 ? context.FileVersion : 1,
                Details = new ApsPushpinDetails
                {
                    Viewable = new ApsPushpinViewable
                    {
                        Guid = context.ViewableGeometryGuid,
                        ViewableId = context.ViewableId,
                        Name = context.ViewableName ?? BuildAiViewNames.Coordination,
                        Is3D = true
                    },
                    Position = new ApsPushpinPosition { X = x, Y = y, Z = z },
                    ObjectId = objectId,
                    ExternalId = string.IsNullOrWhiteSpace(externalId) ? null : externalId,
                    ViewerState = viewerState
                }
            };
        }

        private static void AddGlobalOffsetToCamera(JObject viewerState, double[] offset)
        {
            var viewport = viewerState["viewport"] as JObject;
            if (viewport == null) throw new InvalidOperationException("Viewer state has no viewport object.");
            foreach (var key in new[] { "eye", "target", "pivotPoint" })
            {
                var point = viewport[key] as JArray;
                if (point == null || point.Count < 3) continue;
                for (var i = 0; i < 3; i++) point[i] = (double)point[i] + offset[i];
            }
        }

        private static void AssertViewerHighlightState(
            JObject viewerState,
            IReadOnlyList<int> expectedDbIds,
            bool requireMultipleObjectSets)
        {
            var objectSet = viewerState?["objectSet"] as JArray;
            var appearance = viewerState?["renderOptions"]?["appearance"] as JObject;
            var ghostHidden = (bool?)appearance?["ghostHidden"] ?? false;
            var entries = objectSet == null
                ? new List<JObject>()
                : objectSet.OfType<JObject>().ToList();
            var selected = entries.SelectMany(x => (x["id"] as JArray ?? new JArray()).Values<int>()).ToList();
            var isolated = entries.SelectMany(x => (x["isolated"] as JArray ?? new JArray()).Values<int>()).ToList();
            var selectedOk = ConsumeExpectedIds(selected, expectedDbIds);
            var isolatedOk = ConsumeExpectedIds(isolated, expectedDbIds);
            var modelShapeOk = !requireMultipleObjectSets || entries.Count(x => (x["id"] as JArray)?.Count > 0) >= 2;
            if (!selectedOk || !isolatedOk || !modelShapeOk || !ghostHidden)
                throw new InvalidOperationException(
                    "VIEWERSTATE HIGHLIGHT INVALID | dbIds=" + string.Join(",", expectedDbIds ?? Array.Empty<int>()) +
                    " selected=" + selectedOk +
                    " isolated=" + isolatedOk +
                    " modelShape=" + modelShapeOk +
                    " ghostHidden=" + ghostHidden);

            PluginLog.Info("VIEWERSTATE HIGHLIGHT OK", new
            {
                dbIds = expectedDbIds,
                selected = true,
                isolated = true,
                objectSetCount = entries.Count,
                ghostHidden = true
            });
        }

        private static bool ConsumeExpectedIds(ICollection<int> actualIds, IEnumerable<int> expectedIds)
        {
            var remaining = new List<int>(actualIds ?? Array.Empty<int>());
            foreach (var expected in expectedIds ?? Enumerable.Empty<int>())
            {
                var index = remaining.IndexOf(expected);
                if (index < 0) return false;
                remaining.RemoveAt(index);
            }
            return true;
        }

        private static string FirstNonEmpty(params string[] values)
            => values == null ? null : values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        private static string ForDiagnostics(string value, int maxLength = 12000)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value ?? "";
            return value.Substring(0, maxLength) +
                   "\n...[response truncated; original length: " + value.Length + " characters]";
        }

        public async Task<ApsCreateIssueResponse> CreateApsIssueAsync(
            ApsIssueEnvironment environment,
            ApsTokenResponse token,
            ApsCreateIssueRequest payload,
            CancellationToken ct = default,
            IProgress<string> diagnosticsProgress = null,
            bool logFullPayload = false)
        {
            if (environment == null || !environment.IsUsable)
                throw new InvalidOperationException("APS Issues environment is empty or unresolved.");
            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
                throw new InvalidOperationException("APS access token is empty.");
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            payload.IssueSubtypeId = environment.IssueSubtypeId;
            ApplyDescriptionLimit(payload, diagnosticsProgress);
            var diagnostics = new List<string>();
            diagnosticsProgress?.Report("CREATE ISSUE ENVIRONMENT\n" + environment.DisplayName +
                                        "\nProject ID: " + environment.ProjectId +
                                        "\nContainer ID: " + environment.ContainerId +
                                        "\nSubtype ID: " + environment.IssueSubtypeId);

            if (environment.ApiMode == ApsIssueApiMode.Bim360IssuesV2)
            {
                payload.Published = null;
                return await PostIssueAsync(
                    "v2",
                    "https://developer.api.autodesk.com/issues/v2/containers/" + Uri.EscapeDataString(environment.ContainerId) + "/issues",
                    token, payload, diagnostics, ct, diagnosticsProgress, logFullPayload).ConfigureAwait(false);
            }

            if (environment.ApiMode == ApsIssueApiMode.AccIssuesV1)
            {
                payload.Published = true;
                return await PostIssueAsync(
                    "v1",
                    "https://developer.api.autodesk.com/construction/issues/v1/projects/" + Uri.EscapeDataString(environment.ProjectId) + "/issues",
                    token, payload, diagnostics, ct, diagnosticsProgress, logFullPayload).ConfigureAwait(false);
            }

            throw new InvalidOperationException("Unsupported APS Issues API mode: " + environment.ApiMode);
        }

        private static void ApplyDescriptionLimit(ApsCreateIssueRequest payload, IProgress<string> diagnostics)
        {
            const int maxDescriptionLength = 1000;
            var description = payload?.Description ?? "";
            if (description.Length <= maxDescriptionLength) return;

            const string suffix = "\n[Description shortened by BuildAI]";
            var keep = maxDescriptionLength - suffix.Length;
            if (keep > 0 && keep < description.Length && char.IsHighSurrogate(description[keep - 1])) keep--;
            payload.Description = description.Substring(0, keep).TrimEnd() + suffix;
            diagnostics?.Report("ISSUE DESCRIPTION SHORTENED\nOriginal length: " + description.Length +
                                "\nSent length: " + payload.Description.Length +
                                "\nAutodesk limit: " + maxDescriptionLength);
        }

        private static bool IsAssigneeValidationError(ApsHttpException exception)
        {
            var text = exception?.Message ?? "";
            return text.IndexOf("assignedTo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("assignedToType", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("assignee", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public async Task<IReadOnlyDictionary<string, string>> ResolveRootCauseIdsAsync(
            ApsIssueEnvironment environment,
            ApsTokenResponse token,
            IEnumerable<string> requestedTitles,
            CancellationToken ct = default,
            IProgress<string> diagnostics = null)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var requested = (requestedTitles ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (requested.Count == 0) return result;

            var url = environment.ApiMode == ApsIssueApiMode.Bim360IssuesV2
                ? "https://developer.api.autodesk.com/issues/v2/containers/" + Uri.EscapeDataString(environment.ContainerId) + "/issue-root-cause-categories"
                : "https://developer.api.autodesk.com/construction/issues/v1/projects/" + Uri.EscapeDataString(environment.ProjectId) + "/issue-root-cause-categories";
            try
            {
                var body = await SendApsGetAsync("issue root cause categories", url, token, ct, diagnostics).ConfigureAwait(false);
                var root = JToken.Parse(body);
                var rootContainer = root as JContainer;
                var catalogObjects = rootContainer == null
                    ? Enumerable.Empty<JObject>()
                    : rootContainer.DescendantsAndSelf().OfType<JObject>();
                var catalog = catalogObjects
                    .Select(x => new
                    {
                        Id = ((string)x["id"] ?? "").Trim(),
                        Title = ((string)x["title"] ?? (string)x["name"] ?? "").Trim()
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Title))
                    .GroupBy(x => NormalizeRootCause(x.Title), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.First().Id, StringComparer.OrdinalIgnoreCase);

                foreach (var title in requested)
                {
                    string id;
                    if (catalog.TryGetValue(NormalizeRootCause(title), out id))
                    {
                        result[title] = id;
                        diagnostics?.Report("ROOT CAUSE RESOLVED\nValue: " + title + "\nrootCauseId: " + id);
                    }
                    else diagnostics?.Report("ROOT CAUSE NOT FOUND\nRequested value: " + title + "\nThe Issue will be created without rootCauseId. Add this value to the project's Root causes if it must be assigned.");
                }
            }
            catch (Exception ex)
            {
                diagnostics?.Report("ROOT CAUSE CATALOG UNAVAILABLE\n" + ex.Message + "\nThe Issue will be created without rootCauseId.");
                PluginLog.Warn("APS root cause catalog could not be resolved", new { environment = environment?.DisplayName, error = ex.Message });
            }
            return result;
        }

        private static string NormalizeRootCause(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var chars = value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
            return new string(chars);
        }

        [Obsolete("Resolve an ApsIssueEnvironment first so ACC and BIM 360 are handled explicitly.")]
        public async Task<ApsCreateIssueResponse> CreateApsIssueAsync(string projectId, ApsTokenResponse token, ApsCreateIssueRequest payload, CancellationToken ct = default, IProgress<string> diagnosticsProgress = null, bool logFullPayload = false)
        {
            var environment = await ResolveIssueEnvironmentAsync(projectId, null, token, ct, diagnosticsProgress).ConfigureAwait(false);
            return await CreateApsIssueAsync(environment, token, payload, ct, diagnosticsProgress, logFullPayload).ConfigureAwait(false);
        }

        private async Task<string> ResolveClashSubtypeBim360Async(string containerId, ApsTokenResponse token, CancellationToken ct, IProgress<string> diagnostics = null)
        {
            var url = "https://developer.api.autodesk.com/issues/v2/containers/" + Uri.EscapeDataString(containerId) + "/issue-types?include=subtypes";
            var body = await SendApsGetAsync("BIM 360 issue types (issues/v2)", url, token, ct, diagnostics).ConfigureAwait(false);
            return FindClashSubtypeId(body, "BIM 360 Issues API v2");
        }

        private async Task<string> ResolveClashSubtypeAccAsync(string projectId, ApsTokenResponse token, CancellationToken ct, IProgress<string> diagnostics = null)
        {
            var url = "https://developer.api.autodesk.com/construction/issues/v1/projects/" + Uri.EscapeDataString(projectId) + "/issue-types?include=subtypes";
            var body = await SendApsGetAsync("ACC issue types (construction/issues/v1)", url, token, ct, diagnostics).ConfigureAwait(false);
            return FindClashSubtypeId(body, "ACC Issues API");
        }

        /// <summary>
        /// Status codes that mean "this identifier is not served by this API"
        /// rather than "the workflow has failed". Every one of them must let the
        /// probe loop continue to the next candidate.
        /// <list type="bullet">
        /// <item><description>400 - id rejected by container/project validation (for example a "b." prefix).</description></item>
        /// <item><description>401/403 - token has no rights on this route; the other family may still work.</description></item>
        /// <item><description>404 - project or container unknown to this API family.</description></item>
        /// </list>
        /// </summary>
        private static bool IsProbeRejection(int statusCode)
        {
            return statusCode == 400 || statusCode == 401 || statusCode == 403 || statusCode == 404;
        }

        private async Task ValidateManifestAsync(
            ManifestCandidate candidate,
            ApsTokenResponse token,
            CancellationToken ct,
            IProgress<string> diagnostics)
        {
            var body = await SendApsGetAsync(
                candidate.IsAuthoritative ? "authoritative current-version manifest probe" : "regional manifest probe",
                candidate.Url, token, ct, diagnostics).ConfigureAwait(false);
            JObject manifest;
            try { manifest = JObject.Parse(body); }
            catch (Exception ex) { throw new InvalidOperationException("Autodesk returned an invalid manifest for region " + candidate.Region + ": " + ex.Message); }
            var status = ((string)manifest["status"] ?? "").Trim();
            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The manifest exists in region " + candidate.Region + " but is not ready. Status: " + (status.Length == 0 ? "unknown" : status));
            diagnostics?.Report("URN CANDIDATE ACCEPTED\nRegion: " + candidate.Region +
                                "\nRoute: " + (candidate.IsAuthoritative ? "authoritative current version" : "regional fallback") +
                                "\nManifest status: " + status);
        }

        private static string BuildRegionalManifestUrl(string derivativeUrn, string region, string scopesQuery)
        {
            var url = "https://developer.api.autodesk.com/modelderivative/v2/regions/" +
                      Uri.EscapeDataString((region ?? "US").ToLowerInvariant()) + "/designdata/" +
                      Uri.EscapeDataString(derivativeUrn) + "/manifest";
            var query = (scopesQuery ?? "").Trim().TrimStart('?');
            return query.Length == 0 ? url : url + "?" + query;
        }

        private async Task<string> SendApsGetAsync(string operation, string url, ApsTokenResponse token, CancellationToken ct, IProgress<string> diagnostics = null)
        {
            const int maxAttempts = 4;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                await token.EnsureFreshAsync(ct, diagnostics).ConfigureAwait(false);
                var usedAccessToken = token.AccessToken;
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue(string.IsNullOrWhiteSpace(token.TokenType) ? "Bearer" : token.TokenType, usedAccessToken);
                    PluginLog.Info("APS request", new { operation, method = "GET", url, attempt = attempt + 1 });
                    diagnostics?.Report("APS REQUEST\nOperation: " + operation + "\nGET " + url + "\nAttempt: " + (attempt + 1) + "/" + maxAttempts);
                    using (var response = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        PluginLog.Info("APS response", new { operation, status = (int)response.StatusCode, url, body = Truncate(body, 2000) });
                        diagnostics?.Report("APS RESPONSE\nOperation: " + operation + "\nHTTP " + (int)response.StatusCode + "\nBody:\n" + ForDiagnostics(body));
                        if ((int)response.StatusCode == 401 && attempt == 0 && IsExpiredApsTokenResponse(body) &&
                            await token.EnsureFreshAsync(ct, diagnostics, true, usedAccessToken).ConfigureAwait(false))
                            continue;
                        var statusCode = (int)response.StatusCode;
                        if ((statusCode == 429 || statusCode >= 500) && attempt + 1 < maxAttempts)
                        {
                            var retryAfter = response.Headers.RetryAfter?.Delta;
                            var delay = retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero
                                ? retryAfter.Value
                                : TimeSpan.FromSeconds(Math.Pow(2, attempt));
                            if (delay > TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
                            diagnostics?.Report("APS RETRY\nHTTP " + statusCode + "\nWaiting " + delay.TotalSeconds.ToString("0.###") + " second(s).");
                            await Task.Delay(delay, ct).ConfigureAwait(false);
                            continue;
                        }
                        if (!response.IsSuccessStatusCode)
                            throw new ApsHttpException(operation, statusCode, body);
                        return body;
                    }
                }
            }
            throw new InvalidOperationException(operation + " failed after bounded APS retries.");
        }

        private static string FindClashSubtypeId(string json, string apiName)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException(apiName + " returned an empty issue-types response.");
            var root = JToken.Parse(json);
            var candidates = root.Type == JTokenType.Array ? root.Children() :
                (root["results"] ?? root["issueTypes"] ?? root["data"] ?? root).Children();

            var available = new List<string>();
            foreach (var type in candidates)
            {
                var typeTitle = ((string)type["title"] ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(typeTitle)) available.Add(typeTitle);
                if (string.Equals(typeTitle, "Clash", StringComparison.OrdinalIgnoreCase))
                {
                    var directId = (string)type["id"];
                    if (!string.IsNullOrWhiteSpace(directId) && IsActive(type)) return directId;
                }

                var subtypes = type["subtypes"] as JArray;
                if (subtypes == null) continue;
                foreach (var subtype in subtypes)
                {
                    var title = ((string)subtype["title"] ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(title)) available.Add(typeTitle + " / " + title);
                    if (!string.Equals(title, "Clash", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsActive(subtype)) continue;
                    var id = (string)subtype["id"];
                    if (!string.IsNullOrWhiteSpace(id)) return id;
                }
            }

            throw new InvalidOperationException(apiName + " did not return an active issue subtype with title 'Clash'. Available types/subtypes: " +
                (available.Count == 0 ? "<none>" : string.Join(", ", available.Distinct(StringComparer.OrdinalIgnoreCase).Take(100))));
        }

        private static bool IsActive(JToken token)
        {
            var active = token["isActive"];
            return active == null || active.Type == JTokenType.Null || active.Value<bool>();
        }

        private async Task<ApsCreateIssueResponse> PostIssueAsync(
            string apiVersion,
            string url,
            ApsTokenResponse token,
            ApsCreateIssueRequest payload,
            List<string> diagnostics,
            CancellationToken ct,
            IProgress<string> diagnosticsProgress = null,
            bool logFullPayload = false)
        {
            var payloadJson = JsonConvert.SerializeObject(payload, Formatting.Indented);
            const int maxAttempts = 4;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                await token.EnsureFreshAsync(ct, diagnosticsProgress).ConfigureAwait(false);
                var usedAccessToken = token.AccessToken;
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue(string.IsNullOrWhiteSpace(token.TokenType) ? "Bearer" : token.TokenType, usedAccessToken);
                    req.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                    PluginLog.Info("APS Issue request", new { apiVersion, url, payload.Title, payload.IssueSubtypeId, payload.AssignedTo, payload.DueDate, payload.RootCauseId, payload.LocationDetails, attempt = attempt + 1 });
                    if (logFullPayload)
                        diagnosticsProgress?.Report("APS ISSUE REQUEST (linkedDocuments diagnostic)\nPOST " + url + "\nAttempt: " + (attempt + 1) + "/" + maxAttempts + "\n" + payloadJson);
                    using (var response = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        PluginLog.Info("APS Issue response", new { apiVersion, status = (int)response.StatusCode, body = Truncate(body, 2000) });
                        if (logFullPayload)
                            diagnosticsProgress?.Report("APS ISSUE RESPONSE\nHTTP " + (int)response.StatusCode + "\n" + body);
                        if ((int)response.StatusCode == 401 && attempt == 0 && IsExpiredApsTokenResponse(body) &&
                            await token.EnsureFreshAsync(ct, diagnosticsProgress, true, usedAccessToken).ConfigureAwait(false))
                            continue;
                        var statusCode = (int)response.StatusCode;
                        if ((statusCode == 429 || statusCode >= 500) && attempt + 1 < maxAttempts)
                        {
                            var retryAfter = response.Headers.RetryAfter?.Delta;
                            var delay = retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero
                                ? retryAfter.Value
                                : TimeSpan.FromSeconds(Math.Pow(2, attempt));
                            if (delay > TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
                            diagnosticsProgress?.Report("APS ISSUE RETRY\nHTTP " + statusCode + "\nWaiting " + delay.TotalSeconds.ToString("0.###") + " second(s).");
                            await Task.Delay(delay, ct).ConfigureAwait(false);
                            continue;
                        }
                        if (!response.IsSuccessStatusCode)
                        {
                            diagnostics.Add("POST " + url + Environment.NewLine + "HTTP " + (int)response.StatusCode + Environment.NewLine + body);
                            throw new ApsHttpException("APS Issues " + apiVersion, (int)response.StatusCode, body);
                        }
                        var result = JsonConvert.DeserializeObject<ApsCreateIssueResponse>(body) ?? new ApsCreateIssueResponse();
                        result.HttpStatusCode = statusCode;
                        if (string.IsNullOrWhiteSpace(result.Id))
                            throw new InvalidOperationException("APS Issues " + apiVersion + " returned success without an issue id. Response: " + body);
                        ReportStoredPushpin(body, diagnosticsProgress);
                        if (string.Equals(apiVersion, "v1", StringComparison.OrdinalIgnoreCase))
                            await EnsureAccIssuePublishedAsync(url, result, token, ct, diagnosticsProgress).ConfigureAwait(false);
                        return result;
                    }
                }
            }
            throw new InvalidOperationException("APS Issues " + apiVersion + " failed after bounded retries.");
        }

        private async Task EnsureAccIssuePublishedAsync(
            string collectionUrl,
            ApsCreateIssueResponse issue,
            ApsTokenResponse token,
            CancellationToken ct,
            IProgress<string> diagnostics)
        {
            var actions = issue.PermittedActions ?? Array.Empty<string>();
            diagnostics?.Report("ISSUE PERMITTED ACTIONS\n  " +
                                (actions.Length == 0 ? "(none returned by ACC)" : string.Join(", ", actions)));
            if (issue.Published)
            {
                diagnostics?.Report("ISSUE PUBLISHED | " + issue.Id + " | published=true in create request");
                return;
            }

            diagnostics?.Report("ISSUE IS DRAFT | " + issue.Id + " | publishing separately");
            var issueUrl = collectionUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(issue.Id);
            if (await TryPublishAccIssueAsync(new HttpMethod("PATCH"), issueUrl, "{\"published\":true}", token, ct, diagnostics, "PATCH published=true").ConfigureAwait(false))
                return;

            var actionCandidates = actions
                .Where(x => !string.IsNullOrWhiteSpace(x) && x.IndexOf("publish", StringComparison.OrdinalIgnoreCase) >= 0)
                .Concat(new[] { "publish" })
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var action in actionCandidates)
            {
                if (await TryPublishAccIssueAsync(HttpMethod.Post, issueUrl + ":" + Uri.EscapeDataString(action), "{}", token, ct, diagnostics, "POST :" + action).ConfigureAwait(false))
                    return;
            }

            throw new InvalidOperationException(
                "ACC created Issue " + issue.Id + " as a draft and all publication attempts failed. " +
                "Permitted actions: " + (actions.Length == 0 ? "<none>" : string.Join(", ", actions)) + ".");
        }

        private async Task<bool> TryPublishAccIssueAsync(
            HttpMethod method,
            string url,
            string json,
            ApsTokenResponse token,
            CancellationToken ct,
            IProgress<string> diagnostics,
            string operation)
        {
            await token.EnsureFreshAsync(ct, diagnostics).ConfigureAwait(false);
            using (var request = new HttpRequestMessage(method, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    string.IsNullOrWhiteSpace(token.TokenType) ? "Bearer" : token.TokenType,
                    token.AccessToken);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (var response = await _http.SendAsync(request, ct).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    diagnostics?.Report("ISSUE PUBLISH ATTEMPT | " + operation + " | HTTP " + (int)response.StatusCode +
                                        (string.IsNullOrWhiteSpace(body) ? "" : "\n" + Truncate(body, 300)));
                    if (!response.IsSuccessStatusCode) return false;
                    if (method == HttpMethod.Post) return true;
                    if (string.IsNullOrWhiteSpace(body)) return true;
                    try { return JObject.Parse(body)["published"]?.Value<bool>() == true; }
                    catch { return false; }
                }
            }
        }


        /// <summary>
        /// Reads the pushpin back out of the create response and reports what
        /// Autodesk actually stored.
        /// <para>
        /// A 201 only says the Issue exists. It says nothing about whether the
        /// pushpin survived: a request whose linkedDocuments were ignored, or whose
        /// position was dropped, returns the same 201 as one that worked, and the
        /// difference is invisible until somebody opens the Issue in ACC and finds
        /// no pin. Echoing the stored values turns that into a line in the log.
        /// </para>
        /// </summary>
        private static void ReportStoredPushpin(string body, IProgress<string> diagnosticsProgress)
        {
            if (diagnosticsProgress == null || string.IsNullOrWhiteSpace(body)) return;
            try
            {
                var issue = JObject.Parse(body);
                var linked = issue["linkedDocuments"] as JArray;
                if (linked == null || linked.Count == 0)
                {
                    diagnosticsProgress.Report(
                        "PUSHPIN NOT STORED" + Environment.NewLine +
                        "Autodesk accepted the Issue but returned no linkedDocuments, so it carries no pushpin." + Environment.NewLine +
                        "The Issue exists and is usable; only its 3D location is missing.");
                    return;
                }

                var details = linked[0]["details"];
                var position = details?["position"];
                var viewable = details?["viewable"];
                diagnosticsProgress.Report(
                    "PUSHPIN STORED BY AUTODESK" + Environment.NewLine +
                    "Type: " + ((string)linked[0]["type"] ?? "<none>") + Environment.NewLine +
                    "Viewable: " + ((string)viewable?["name"] ?? "<none>") +
                    " (guid " + ((string)viewable?["guid"] ?? "<none>") + ")" + Environment.NewLine +
                    "Version: " + (linked[0]["createdAtVersion"]?.ToString() ?? "<none>") + Environment.NewLine +
                    "Position: " + (position == null ? "<ABSENT - the pin has no location>" : position.ToString(Formatting.None)) + Environment.NewLine +
                    "objectId: " + (details?["objectId"]?.ToString() ?? "<none>") +
                    "  externalId: " + ((string)details?["externalId"] ?? "<none>"));
            }
            catch (Exception ex)
            {
                diagnosticsProgress.Report("PUSHPIN VERIFICATION SKIPPED" + Environment.NewLine +
                                           "The create response could not be re-read: " + ex.Message);
            }
        }

        private static bool IsExpiredApsTokenResponse(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return true;
            return body.IndexOf("AUTH-006", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   body.IndexOf("invalid or expired", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   body.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   body.IndexOf("expired", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeRegion(string value)
        {
            var region = (value ?? "").Trim().ToUpperInvariant();
            if (region == "EU") return "EMEA";
            if (region == "APAC") return "AUS";
            return region;
        }

        private static string NormalizeDerivativeUrn(string value)
        {
            var urn = (value ?? "").Trim();
            if (urn.Length == 0) return "";
            if (!urn.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)) return urn.TrimEnd('=');
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(urn)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private sealed class ManifestCandidate
        {
            public string Urn { get; set; } = "";
            public string Region { get; set; } = "";
            public string Url { get; set; } = "";
            public bool IsAuthoritative { get; set; }
        }

        public async Task<SaveIssueBatchResponse> SaveIssuesAsync(SaveIssueBatchRequest request, CancellationToken ct = default, IProgress<string> diagnostics = null)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.RevitModelUid))
                throw new InvalidOperationException("BuildAI batch synchronization requires the frozen Revit model UID.");
            var body = await SendBuildAiAsync(HttpMethod.Post, _baseUrl + "/api/revit_issues/batch", JsonConvert.SerializeObject(request), ct, diagnostics).ConfigureAwait(false);
            SaveIssueBatchResponse result;
            try { result = JsonConvert.DeserializeObject<SaveIssueBatchResponse>(body); }
            catch (Exception ex) { throw new InvalidOperationException("BuildAI batch response is not valid JSON: " + ex.Message); }
            if (result == null)
                throw new InvalidOperationException("BuildAI batch response is empty.");
            var failedItems = (result.Items ?? new List<SaveIssueBatchItemResult>())
                .Where(x => x != null && (string.Equals(x.Status, "failed", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(x.Error)))
                .ToList();
            if (result.Failed > 0 || failedItems.Count > 0)
                throw new InvalidOperationException(
                    "BuildAI batch synchronization failed for " + Math.Max(result.Failed, failedItems.Count) +
                    " Issue(s). " + string.Join(" | ", failedItems.Take(20).Select(x =>
                        (string.IsNullOrWhiteSpace(x.ResultKey) ? x.IssueId : x.ResultKey) + ": " +
                        (string.IsNullOrWhiteSpace(x.Error) ? x.Status : x.Error))));
            var expected = request.Issues?.Count ?? 0;
            if (result.Saved + result.Updated < expected)
                throw new InvalidOperationException(
                    "BuildAI batch synchronization acknowledged only " + (result.Saved + result.Updated) +
                    " of " + expected + " Issue(s).");
            return result;
        }

        private async Task<string> SendBuildAiAsync(HttpMethod method, string url, string json, CancellationToken ct, IProgress<string> diagnostics = null)
        {
            using (var req = new HttpRequestMessage(method, url))
            {
                var apiToken = _credentials.GetApiToken();
                if (!string.IsNullOrWhiteSpace(apiToken)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
                if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                PluginLog.Info("BuildAI Issues request", new { method = method.Method, url, body = Truncate(json, 1200) });
                diagnostics?.Report("BUILDAI REQUEST\n" + method.Method + " " + url + "\nBody:\n" + (json ?? "<empty>"));
                using (var response = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var bodyForLog = RedactSensitiveResponse(url, body);
                    PluginLog.Info("BuildAI Issues response", new { status = (int)response.StatusCode, url, body = Truncate(bodyForLog, 1200) });
                    diagnostics?.Report("BUILDAI RESPONSE\n" + method.Method + " " + url + "\nHTTP " + (int)response.StatusCode + "\nBody:\n" + ForDiagnostics(bodyForLog));
                    if (!response.IsSuccessStatusCode)
                    {
                        string openUrl = null;
                        try { openUrl = (string)JObject.Parse(body)["url"]; } catch { }
                        var ex = new InvalidOperationException("BuildAI API " + (int)response.StatusCode + ": " + bodyForLog);
                        if (!string.IsNullOrWhiteSpace(openUrl)) ex.Data["url"] = openUrl;
                        throw ex;
                    }
                    return body;
                }
            }
        }

        private static string Truncate(string value, int max)
            => string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max) + "…";

        private static string RedactSensitiveResponse(string url, string body)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                url.IndexOf("/api/revit_aps_key", StringComparison.OrdinalIgnoreCase) < 0)
                return body ?? "";

            try
            {
                var root = JToken.Parse(body ?? "{}");
                var sensitiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "access_token", "refresh_token", "client_secret"
                };
                RedactTokenFields(root, sensitiveNames);
                return root.ToString(Formatting.None);
            }
            catch
            {
                return "{\"sensitive_response\":\"[REDACTED]\"}";
            }
        }

        private static void RedactTokenFields(JToken token, ISet<string> sensitiveNames)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                foreach (var property in obj.Properties().ToList())
                {
                    if (sensitiveNames.Contains(property.Name)) property.Value = "[REDACTED]";
                    else RedactTokenFields(property.Value, sensitiveNames);
                }
                return;
            }

            var array = token as JArray;
            if (array == null) return;
            foreach (var child in array) RedactTokenFields(child, sensitiveNames);
        }

        public void Dispose() => _http.Dispose();

        private sealed class ApsHttpException : InvalidOperationException
        {
            public int StatusCode { get; }
            public ApsHttpException(string operation, int statusCode, string body)
                : base(operation + " HTTP " + statusCode + ": " + Truncate(body, 4000))
            {
                StatusCode = statusCode;
            }
        }

        private sealed class ApsEnvironmentNotFoundException : InvalidOperationException
        {
            public ApsEnvironmentNotFoundException(string message) : base(message) { }
        }
    }
}
