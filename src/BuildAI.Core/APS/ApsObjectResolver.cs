using System;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildAI.Core.Issues;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BuildAI.Core.APS
{
    public sealed class ApsObjectMatch
    {
        /// <summary>
        /// Model Derivative property-database id of the matched row. Since 7.3.27
        /// this is the value written into details.objectId and objectSet, because it
        /// is the id ACC resolves when it opens the issue; the runtime Viewer dbId is
        /// kept only as probe evidence.
        /// </summary>
        public int LookupObjectId { get; set; }

        /// <summary>
        /// Exact externalId returned by Model Derivative. For a linked Revit
        /// element this is the full composite externalId required by Viewer.
        /// </summary>
        public string ExternalId { get; set; } = "";

        public bool IsValid => LookupObjectId > 0 && !string.IsNullOrWhiteSpace(ExternalId);
    }

    /// <summary>
    /// Optional diagnostic lookup against the filtered Model Derivative
    /// property query. Issue creation does not depend on this class: final
    /// identity and geometry are resolved from the loaded Viewer scene.
    /// </summary>
    public sealed class ApsObjectResolver : IDisposable
    {
        // Model Derivative can accept a property query with HTTP 202 while the
        // publication's property database is still being built. A short retry
        // window turns that normal state into a false missing-object rejection.
        // Keep the wait bounded: no Issue POST is allowed until the query returns
        // a resolved property row.
        private const int MaxQueryAttempts = 40;
        private static readonly TimeSpan PollDelayMin = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan PollDelayMax = TimeSpan.FromSeconds(15);
        private static TimeSpan BackOff(int attempt)
        {
            var seconds = Math.Min(PollDelayMax.TotalSeconds, PollDelayMin.TotalSeconds * Math.Pow(1.4, attempt - 1));
            return TimeSpan.FromSeconds(seconds);
        }
        private readonly HttpClient _http;

        public ApsObjectResolver(HttpClient http = null)
        {
            _http = http ?? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = TimeSpan.FromSeconds(90) };
        }

        public async Task<ApsObjectMatch> ResolveObjectAsync(
            ApsPushpinContext context,
            ApsTokenResponse token,
            string externalId,
            int elementId = 0,
            string modelUid = null,
            string linkInstanceUid = null,
            string sourceName = null,
            string category = null,
            CancellationToken ct = default,
            IProgress<string> diagnostics = null)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.DerivativeUrn) ||
                string.IsNullOrWhiteSpace(context.ModelPropertiesGuid) || string.IsNullOrWhiteSpace(externalId))
                return null;
            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken)) return null;

            var queryUrl = BuildPropertiesQueryUrl(context);
            var payload = new JObject
            {
                ["query"] = new JObject { ["$in"] = new JArray("externalId", externalId) },
                ["fields"] = new JArray("objectid", "externalId", "name"),
                ["pagination"] = new JObject { ["limit"] = 20, ["offset"] = 0 }
            };

            for (var attempt = 1; attempt <= MaxQueryAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                using (var req = CreateRequest(HttpMethod.Post, queryUrl, token.AccessToken,
                           new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json")))
                {
                    diagnostics?.Report("APS OBJECT LOOKUP REQUEST\nAttempt: " + attempt + "/" + MaxQueryAttempts +
                                        "\nExternalId: " + externalId + "\nPOST " + queryUrl + "\n" +
                                        payload.ToString(Formatting.None));
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        diagnostics?.Report("APS OBJECT LOOKUP RESPONSE\nAttempt: " + attempt +
                                            "\nHTTP " + (int)resp.StatusCode + "\n" + body);

                        if (resp.StatusCode == HttpStatusCode.Accepted)
                        {
                            diagnostics?.Report("APS OBJECT LOOKUP PENDING\nThe Model Derivative property database is still processing. Retrying in " +
                                                BackOff(attempt).TotalSeconds.ToString("0.0") + " seconds.");
                            await Task.Delay(BackOff(attempt), ct).ConfigureAwait(false);
                            continue;
                        }

                        if (resp.IsSuccessStatusCode)
                        {
                            var found = TryFindObject(body, externalId, elementId, modelUid, linkInstanceUid, sourceName, category, diagnostics);
                            if (found != null && found.IsValid)
                            {
                                diagnostics?.Report("APS OBJECT LOOKUP MATCH\nExternalId: " + externalId +
                                                    "\nLookup objectId: " + found.LookupObjectId +
                                                    "\nResolved full externalId: " + found.ExternalId +
                                                    "\nSource: filtered properties query");
                                return found;
                            }

                            // Some link elements are indexed under the bare element externalId
                            // rather than the "<linkInstance>/<element>" composite. Retry once
                            // with the bare id in the SAME property database: still authoritative.
                            var slash = externalId.LastIndexOf('/');
                            if (slash > 0 && slash < externalId.Length - 1)
                            {
                                var bare = externalId.Substring(slash + 1);
                                var bareMatch = await ResolveBareExternalIdAsync(token, bare, externalId, queryUrl, ct, diagnostics).ConfigureAwait(false);
                                if (bareMatch != null && bareMatch.IsValid) return bareMatch;
                            }

                            diagnostics?.Report("APS OBJECT LOOKUP FILTERED MISS\nThe filtered query returned no exact externalId match. Full-properties download is disabled; Viewer will resolve composite candidates.");
                            break;
                        }

                        diagnostics?.Report("APS OBJECT LOOKUP QUERY ERROR\nFiltered query failed with HTTP " +
                                            (int)resp.StatusCode + ". Full-properties download is disabled.");
                        break;
                    }
                }
            }

            diagnostics?.Report("APS OBJECT LOOKUP TIMEOUT\nProperty Database did not become queryable within " +
                                " bounded polling budget for ExternalId: " + externalId +
                                "\nIssue creation remains blocked; no runtime dbId fallback is allowed.");
            return null;
        }

        public async Task<string> ResolveExternalIdByObjectIdAsync(
            ApsPushpinContext context, ApsTokenResponse token, int objectId,
            CancellationToken ct = default, IProgress<string> diagnostics = null)
        {
            if (context == null || token == null || objectId <= 0 || string.IsNullOrWhiteSpace(token.AccessToken) ||
                string.IsNullOrWhiteSpace(context.DerivativeUrn) || string.IsNullOrWhiteSpace(context.ModelPropertiesGuid))
                return null;
            var payload = new JObject {
                ["query"] = new JObject { ["$in"] = new JArray("objectid", objectId) },
                ["fields"] = new JArray("objectid", "externalId"),
                ["pagination"] = new JObject { ["limit"] = 20, ["offset"] = 0 }
            };
            var queryUrl = BuildPropertiesQueryUrl(context);
            for (var attempt = 1; attempt <= MaxQueryAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                using (var req = CreateRequest(HttpMethod.Post, queryUrl, token.AccessToken,
                    new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json")))
                using (var response = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    diagnostics?.Report("APS OBJECT REVERSE LOOKUP RESPONSE\nAttempt: " + attempt +
                        "/" + MaxQueryAttempts + "\nHTTP " + (int)response.StatusCode +
                        "\nObjectId: " + objectId + "\n" + Limit(body, 1200));
                    if (response.StatusCode == HttpStatusCode.Accepted)
                    {
                        await Task.Delay(BackOff(attempt), ct).ConfigureAwait(false);
                        continue;
                    }
                    if (!response.IsSuccessStatusCode) return null;
                    JObject root;
                    try { root = JObject.Parse(body); } catch { return null; }
                    foreach (var item in EnumerateObjects(root))
                        if (ReadObjectId(item) == objectId && !string.IsNullOrWhiteSpace(ReadExternalId(item)))
                            return ReadExternalId(item);
                    return null;
                }
            }
            return null;
        }

        /// <summary>
        /// Resolves the geometry bounds from the ACC Model Properties index for the same
        /// Viewer object that was selected for objectSet. Model Properties returns the
        /// large-coordinate bounding box in metres. The center must be mapped to local
        /// Viewer coordinates before it is used for Issue position, target and pivotPoint.
        /// </summary>
        public async Task<ApsObjectBounds> ResolveObjectBoundsAsync(
            ApsPushpinContext context,
            ApsTokenResponse token,
            int objectId,
            string externalId,
            CancellationToken ct = default,
            IProgress<string> diagnostics = null)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.ProjectId) ||
                string.IsNullOrWhiteSpace(context.VersionUrn) || objectId <= 0)
                return null;
            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken)) return null;

            var projectId = context.ProjectId.StartsWith("b.", StringComparison.OrdinalIgnoreCase)
                ? context.ProjectId.Substring(2)
                : context.ProjectId;
            var batchUrl = "https://developer.api.autodesk.com/construction/index/v2/projects/" +
                           Uri.EscapeDataString(projectId) + "/indexes:batch-status";

            // Optional diagnostic path only. Active Issue workflows do not
            // require this index response and resolve final identity in Viewer.
            var conditions = new JArray
            {
                new JObject { ["$eq"] = new JArray("s.lmvId", objectId) },
                new JObject { ["$eq"] = new JArray("s.svf2Id", objectId) }
            };
            if (!string.IsNullOrWhiteSpace(externalId))
                conditions.Add(new JObject { ["$eq"] = new JArray("s.externalId", "'" + externalId.Replace("'", "''") + "'") });

            var versionRequest = new JObject
            {
                ["versionUrn"] = context.VersionUrn,
                ["query"] = new JObject { ["$or"] = conditions },
                ["columns"] = new JObject
                {
                    ["lmvId"] = "s.lmvId",
                    ["svf2Id"] = "s.svf2Id",
                    ["externalId"] = "s.externalId",
                    ["bboxMin"] = "s.bboxMin",
                    ["bboxMax"] = "s.bboxMax",
                    ["views"] = "s.views"
                }
            };
            var payload = new JObject { ["versions"] = new JArray(versionRequest) };

            string indexId = null;
            string queryId = null;
            string queryPropertiesUrl = null;
            for (var attempt = 1; attempt <= 18; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                using (var req = CreateRequest(HttpMethod.Post, batchUrl, token.AccessToken,
                           new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json")))
                {
                    diagnostics?.Report("APS OBJECT BOUNDS INDEX REQUEST\nAttempt: " + attempt + "/18\nObjectId: " + objectId +
                                        "\nVersion URN: " + context.VersionUrn + "\nPOST " + batchUrl);
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        diagnostics?.Report("APS OBJECT BOUNDS INDEX RESPONSE\nHTTP " + (int)resp.StatusCode +
                                            "\n" + Limit(body, 6000));
                        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.Accepted) return null;
                        JObject root;
                        try { root = JObject.Parse(body); }
                        catch { return null; }
                        indexId = FirstString(root, "indexId", "indexid") ?? indexId;
                        queryId = FirstString(root, "queryId", "queryid") ?? queryId;
                        queryPropertiesUrl = FirstString(root, "queryResultsUrl", "propertiesUrl", "queryPropertiesUrl") ?? queryPropertiesUrl;
                        var state = FirstString(root, "state", "status") ?? "";
                        if (string.Equals(state, "FAILED", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(state, "ERROR", StringComparison.OrdinalIgnoreCase)) return null;
                        if ((!string.IsNullOrWhiteSpace(queryId) && !string.IsNullOrWhiteSpace(indexId)) ||
                            !string.IsNullOrWhiteSpace(queryPropertiesUrl)) break;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(attempt < 4 ? 2 : 4), ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(queryPropertiesUrl))
            {
                if (string.IsNullOrWhiteSpace(indexId) || string.IsNullOrWhiteSpace(queryId)) return null;
                queryPropertiesUrl = "https://developer.api.autodesk.com/construction/index/v2/projects/" +
                                     Uri.EscapeDataString(projectId) + "/indexes/" + Uri.EscapeDataString(indexId) +
                                     "/queries/" + Uri.EscapeDataString(queryId) + "/properties";
            }

            for (var attempt = 1; attempt <= 18; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                using (var req = CreateRequest(HttpMethod.Get, queryPropertiesUrl, token.AccessToken, null))
                {
                    diagnostics?.Report("APS OBJECT BOUNDS RESULT REQUEST\nAttempt: " + attempt + "/18\nGET " + queryPropertiesUrl);
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        diagnostics?.Report("APS OBJECT BOUNDS RESULT RESPONSE\nHTTP " + (int)resp.StatusCode +
                                            "\nBody length: " + body.Length + "\n" + Limit(body, 6000));
                        if (resp.StatusCode == HttpStatusCode.Accepted || resp.StatusCode == HttpStatusCode.NotFound)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(attempt < 4 ? 2 : 4), ct).ConfigureAwait(false);
                            continue;
                        }
                        if (!resp.IsSuccessStatusCode) return null;
                        var bounds = ParseBounds(body, objectId, externalId);
                        if (bounds != null && bounds.IsValid)
                        {
                            diagnostics?.Report("APS OBJECT BOUNDS AND IDENTITY MATCH\nLookup objectId: " + objectId +
                                "\nIndex lmvId -> lookup correlation only: " + bounds.LmvId +
                                "\nIndex svf2Id -> Issue objectId/objectSet: " + bounds.Svf2Id +
                                "\nFull index externalId -> Issue externalId: " + bounds.ExternalId +
                                "\nMin: X=" + bounds.MinX.ToString("0.######") + "; Y=" + bounds.MinY.ToString("0.######") + "; Z=" + bounds.MinZ.ToString("0.######") +
                                "\nMax: X=" + bounds.MaxX.ToString("0.######") + "; Y=" + bounds.MaxY.ToString("0.######") + "; Z=" + bounds.MaxZ.ToString("0.######") +
                                "\nCenter: X=" + bounds.CenterX.ToString("0.######") + "; Y=" + bounds.CenterY.ToString("0.######") + "; Z=" + bounds.CenterZ.ToString("0.######") +
                                "\nCoordinate source for the final Issue: Viewer fragment world bounds; this index bbox is diagnostic only");
                            return bounds;
                        }
                        return null;
                    }
                }
            }
            return null;
        }

        private static ApsObjectBounds ParseBounds(string body, int objectId, string externalId)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            var lines = body.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                JToken token;
                try { token = JToken.Parse(line.Trim()); }
                catch { continue; }
                foreach (var item in EnumerateObjects(token))
                {
                    var lmvId = (int?)item["lmvId"] ?? (int?)item["lmvid"] ?? (int?)item["objectId"] ?? 0;
                    var svf2Id = (int?)item["svf2Id"] ?? (int?)item["svf2id"] ?? 0;
                    var ext = (string)item["externalId"] ?? "";
                    var idMatch = lmvId == objectId || svf2Id == objectId;
                    var extMatch = !string.IsNullOrWhiteSpace(externalId) && string.Equals(ext, externalId, StringComparison.OrdinalIgnoreCase);
                    if (!idMatch && !extMatch) continue;
                    var min = item["bboxMin"] ?? item["bbxMin"];
                    var max = item["bboxMax"] ?? item["bbxMax"];
                    if (min == null || max == null) continue;
                    double minX, minY, minZ, maxX, maxY, maxZ;
                    if (!TryDouble(min["x"], out minX) || !TryDouble(min["y"], out minY) || !TryDouble(min["z"], out minZ) ||
                        !TryDouble(max["x"], out maxX) || !TryDouble(max["y"], out maxY) || !TryDouble(max["z"], out maxZ)) continue;
                    return new ApsObjectBounds
                    {
                        ObjectId = lmvId,
                        LmvId = lmvId,
                        Svf2Id = svf2Id,
                        ExternalId = ext,
                        MinX = minX, MinY = minY, MinZ = minZ,
                        MaxX = maxX, MaxY = maxY, MaxZ = maxZ
                    };
                }
            }
            return null;
        }

        private static System.Collections.Generic.IEnumerable<JObject> EnumerateObjects(JToken token)
        {
            if (token == null) yield break;
            var obj = token as JObject;
            if (obj != null)
            {
                yield return obj;
                foreach (var child in obj.Properties())
                    foreach (var nested in EnumerateObjects(child.Value)) yield return nested;
                yield break;
            }
            var array = token as JArray;
            if (array != null)
                foreach (var child in array)
                    foreach (var nested in EnumerateObjects(child)) yield return nested;
        }

        private static string FirstString(JToken root, params string[] names)
        {
            foreach (var obj in EnumerateObjects(root))
                foreach (var name in names)
                {
                    var value = (string)obj[name];
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            return null;
        }

        private static bool TryDouble(JToken token, out double value)
        {
            value = 0;
            if (token == null) return false;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                value = (double)token;
                return true;
            }
            return double.TryParse((string)token, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static string Limit(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value ?? "";
            return value.Substring(0, max) + "\n...[truncated; original length: " + value.Length + "]";
        }

        private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string accessToken, HttpContent content)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.Accept.ParseAdd("application/json");
            req.Content = content;
            return req;
        }

        private static string BuildPropertiesUrl(ApsPushpinContext context)
        {
            var prefix = "https://developer.api.autodesk.com/modelderivative/v2/";
            if (!string.IsNullOrWhiteSpace(context.DerivativeRegion))
                prefix += "regions/" + Uri.EscapeDataString(context.DerivativeRegion.Trim().ToLowerInvariant()) + "/";
            return prefix + "designdata/" +
                   Uri.EscapeDataString(context.DerivativeUrn) + "/metadata/" +
                   Uri.EscapeDataString(context.ModelPropertiesGuid) + "/properties";
        }

        /// <summary>
        /// Authoritative retry in the same property database using the bare element
        /// externalId. Used when the composite "<linkInstance>/<element>" form is not
        /// indexed for this viewable (observed 2026-09-20 on one AR-ST floor).
        /// </summary>
        private async Task<ApsObjectMatch> ResolveBareExternalIdAsync(
            ApsTokenResponse token, string bareExternalId, string compositeExternalId,
            string queryUrl, CancellationToken ct, IProgress<string> diagnostics)
        {
            var payload = new JObject
            {
                ["query"] = new JObject { ["$in"] = new JArray("externalId", bareExternalId) },
                ["fields"] = new JArray("objectid", "externalId", "name"),
                ["pagination"] = new JObject { ["limit"] = 20, ["offset"] = 0 }
            };
            using (var req = CreateRequest(HttpMethod.Post, queryUrl, token.AccessToken,
                       new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json")))
            {
                diagnostics?.Report("APS OBJECT LOOKUP BARE RETRY\nComposite externalId not indexed: " + compositeExternalId +
                                    "\nRetrying with the element externalId: " + bareExternalId);
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    diagnostics?.Report("APS OBJECT LOOKUP BARE RESPONSE\nHTTP " + (int)resp.StatusCode + "\n" + body);
                    if (!resp.IsSuccessStatusCode) return null;
                    var found = TryFindObject(body, bareExternalId, 0, null, null, null, null, diagnostics);
                    if (found == null || !found.IsValid) return null;
                    diagnostics?.Report("APS OBJECT LOOKUP MATCH\nExternalId: " + bareExternalId +
                                        "\nLookup objectId: " + found.LookupObjectId +
                                        "\nSource: filtered properties query (bare element externalId)");
                    return found;
                }
            }
        }

        private static string BuildPropertiesQueryUrl(ApsPushpinContext context)
        {
            var url = BuildPropertiesUrl(context) + ":query";
            if (!string.IsNullOrWhiteSpace(context.DerivativeScopes))
            {
                var scopes = context.DerivativeScopes.Trim();
                if (scopes.StartsWith("scopes=", StringComparison.OrdinalIgnoreCase))
                    scopes = scopes.Substring("scopes=".Length);
                if (!string.IsNullOrWhiteSpace(scopes))
                    url += "?scopes=" + Uri.EscapeDataString(scopes);
            }
            return url;
        }

        private static ApsObjectMatch TryFindObject(
            string body,
            string externalId,
            int elementId,
            string modelUid,
            string linkInstanceUid,
            string sourceName,
            string category,
            IProgress<string> diagnostics)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            JObject json;
            try { json = JObject.Parse(body); }
            catch { return null; }

            var collections = new[]
            {
                json["data"]?["collection"] as JArray,
                json["collection"] as JArray,
                json["data"] as JArray
            };

            var all = collections.Where(x => x != null).SelectMany(x => x).ToList();
            if (all.Count == 0) return null;

            // 1. Exact Revit UniqueId/externalId match remains the safest route.
            foreach (var item in all)
            {
                var returnedExternalId = ReadExternalId(item);
                if (!string.Equals(returnedExternalId, externalId, StringComparison.OrdinalIgnoreCase)) continue;
                var exact = ReadObjectId(item);
                if (exact.HasValue)
                    return new ApsObjectMatch
                    {
                        LookupObjectId = exact.Value,
                        ExternalId = returnedExternalId
                    };
            }

            // 2. Composite publications can expose more than one object with the same Revit
            // element id. Build a candidate set, log every candidate, and resolve it using
            // model/link/source/category context. Never select an arbitrary object.
            var suffix = GetElementSuffix(externalId, elementId);
            var candidates = all
                .Select(item => new Candidate
                {
                    Item = item,
                    ObjectId = ReadObjectId(item),
                    ExternalId = ReadExternalId(item),
                    SuffixMatch = ExternalIdMatchesSuffix(ReadExternalId(item), suffix),
                    ElementIdMatch = elementId > 0 && ContainsElementIdProperty(item, elementId)
                })
                .Where(x => x.ObjectId.HasValue && (x.SuffixMatch || x.ElementIdMatch))
                .GroupBy(x => x.ObjectId.Value)
                .Select(x => x.First())
                .ToList();

            if (candidates.Count == 0) return null;

            foreach (var candidate in candidates)
            {
                candidate.Score = ScoreCandidate(candidate, externalId, modelUid, linkInstanceUid, sourceName, category);
                diagnostics?.Report(FormatCandidateDiagnostic(candidate, externalId, elementId, modelUid, linkInstanceUid, sourceName, category));
            }

            if (candidates.Count == 1)
            {
                diagnostics?.Report("APS COMPOSITE OBJECT MATCH\nStrategy: single candidate after suffix/Element ID search" +
                                    "\nObjectId/dbId: " + candidates[0].ObjectId.Value +
                                    "\nScore: " + candidates[0].Score);
                return ToMatch(candidates[0]);
            }

            var ordered = candidates.OrderByDescending(x => x.Score).ThenBy(x => x.ObjectId.Value).ToList();
            var best = ordered[0];
            var second = ordered[1];
            var strongContextMatch = best.Score >= 100;
            var clearMargin = best.Score - second.Score >= 25;
            if (strongContextMatch && clearMargin)
            {
                diagnostics?.Report("APS COMPOSITE OBJECT MATCH\nStrategy: deterministic context scoring" +
                                    "\nObjectId/dbId: " + best.ObjectId.Value +
                                    "\nBest score: " + best.Score +
                                    "\nSecond score: " + second.Score +
                                    "\nMargin: " + (best.Score - second.Score));
                return ToMatch(best);
            }

            diagnostics?.Report("APS COMPOSITE OBJECT AMBIGUOUS\nMultiple Viewer candidates remain after deterministic filtering; no unsafe match was selected." +
                                "\nCandidate count: " + candidates.Count +
                                "\nBest score: " + best.Score +
                                "\nSecond score: " + second.Score +
                                "\nRequired score: 100; required margin: 25" +
                                "\nElementId: " + elementId +
                                "\nModel UID: " + (modelUid ?? "") +
                                "\nLink instance UID: " + (linkInstanceUid ?? "") +
                                "\nSource name: " + (sourceName ?? "") +
                                "\nCategory: " + (category ?? ""));

            return null;
        }

        private static ApsObjectMatch ToMatch(Candidate candidate)
        {
            if (candidate == null || !candidate.ObjectId.HasValue || string.IsNullOrWhiteSpace(candidate.ExternalId))
                return null;
            return new ApsObjectMatch
            {
                LookupObjectId = candidate.ObjectId.Value,
                ExternalId = candidate.ExternalId
            };
        }

        private sealed class Candidate
        {
            public JToken Item { get; set; }
            public int? ObjectId { get; set; }
            public string ExternalId { get; set; }
            public bool SuffixMatch { get; set; }
            public bool ElementIdMatch { get; set; }
            public int Score { get; set; }
        }

        private static int ScoreCandidate(Candidate candidate, string requestedExternalId, string modelUid, string linkInstanceUid, string sourceName, string category)
        {
            var score = 0;
            if (candidate.SuffixMatch) score += 20;
            if (candidate.ElementIdMatch) score += 20;
            if (ContainsContext(candidate.Item, linkInstanceUid)) score += 120;
            else if (ContainsContext(candidate.Item, GuidPart(linkInstanceUid))) score += 70;
            if (ContainsContext(candidate.Item, modelUid)) score += 100;
            else if (ContainsContext(candidate.Item, GuidPart(modelUid))) score += 60;
            if (ContainsContext(candidate.Item, sourceName)) score += 45;
            if (ContainsContext(candidate.Item, category)) score += 30;
            var requestedPrefix = UniqueIdPrefix(requestedExternalId);
            if (!string.IsNullOrWhiteSpace(requestedPrefix) &&
                (candidate.ExternalId ?? "").StartsWith(requestedPrefix, StringComparison.OrdinalIgnoreCase)) score += 50;
            return score;
        }

        private static string FormatCandidateDiagnostic(Candidate candidate, string requestedExternalId, int elementId, string modelUid, string linkInstanceUid, string sourceName, string category)
        {
            var json = candidate.Item == null ? "<null>" : candidate.Item.ToString(Formatting.None);
            if (json.Length > 6000) json = json.Substring(0, 6000) + "...<truncated>";
            return "APS COMPOSITE CANDIDATE" +
                   "\nObjectId/dbId: " + (candidate.ObjectId.HasValue ? candidate.ObjectId.Value.ToString() : "<none>") +
                   "\nReturned externalId: " + (candidate.ExternalId ?? "") +
                   "\nRequested externalId: " + (requestedExternalId ?? "") +
                   "\nElementId: " + elementId +
                   "\nSuffix match: " + candidate.SuffixMatch +
                   "\nElement ID property match: " + candidate.ElementIdMatch +
                   "\nModel UID match: " + ContainsContext(candidate.Item, modelUid) +
                   "\nLink instance UID match: " + ContainsContext(candidate.Item, linkInstanceUid) +
                   "\nSource name match: " + ContainsContext(candidate.Item, sourceName) +
                   "\nCategory match: " + ContainsContext(candidate.Item, category) +
                   "\nScore: " + candidate.Score +
                   "\nCandidate JSON: " + json;
        }

        private static bool ContainsContext(JToken item, string expected)
        {
            if (item == null || string.IsNullOrWhiteSpace(expected)) return false;
            var normalizedExpected = Normalize(expected);
            if (normalizedExpected.Length < 3) return false;
            foreach (var token in Walk(item))
            {
                if (token is JProperty property)
                {
                    if (Normalize(property.Name).Contains(normalizedExpected)) return true;
                    var value = property.Value == null ? "" : property.Value.ToString();
                    if (Normalize(value).Contains(normalizedExpected)) return true;
                }
                else if (!(token is JContainer))
                {
                    if (Normalize(token.ToString()).Contains(normalizedExpected)) return true;
                }
            }
            return false;
        }

        private static System.Collections.Generic.IEnumerable<JToken> Walk(JToken token)
        {
            if (token == null) yield break;
            yield return token;
            var container = token as JContainer;
            if (container == null) yield break;
            foreach (var child in container.Children())
                foreach (var nested in Walk(child))
                    yield return nested;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var chars = value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
            return new string(chars);
        }

        private static string GuidPart(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var last = value.LastIndexOf('-');
            return last > 35 ? value.Substring(0, last) : value;
        }

        private static string UniqueIdPrefix(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var last = value.LastIndexOf('-');
            return last > 0 ? value.Substring(0, last + 1) : value;
        }

        private static string ReadExternalId(JToken item)
        {
            return (string)item?["externalId"] ?? (string)item?["externalid"] ?? "";
        }

        private static int? ReadObjectId(JToken item)
        {
            var value = item?["objectid"] ?? item?["objectId"] ?? item?["object_id"];
            return value != null && int.TryParse(value.ToString(), out var objectId) ? objectId : (int?)null;
        }

        private static string GetElementSuffix(string externalId, int elementId)
        {
            if (elementId > 0) return elementId.ToString("x8");
            if (string.IsNullOrWhiteSpace(externalId)) return "";
            var dash = externalId.LastIndexOf('-');
            return dash >= 0 && dash + 1 < externalId.Length ? externalId.Substring(dash + 1) : "";
        }

        private static bool ExternalIdMatchesSuffix(string candidate, string suffix)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(suffix)) return false;
            return candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                   candidate.EndsWith("-" + suffix, StringComparison.OrdinalIgnoreCase) ||
                   candidate.IndexOf(suffix, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsElementIdProperty(JToken item, int elementId)
        {
            var expectedDecimal = elementId.ToString();
            var expectedHex = elementId.ToString("x8");
            if (item == null) return false;

            foreach (var token in Walk(item))
            {
                if (!(token is JProperty property)) continue;
                var key = (property.Name ?? "").Replace("_", " ").Replace("-", " ").ToLowerInvariant();
                if (!key.Contains("element") && !key.Contains("id")) continue;
                var value = property.Value?.ToString() ?? "";
                if (string.Equals(value, expectedDecimal, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, expectedHex, StringComparison.OrdinalIgnoreCase) ||
                    value.EndsWith("-" + expectedHex, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public void Dispose() => _http.Dispose();
    }
}
