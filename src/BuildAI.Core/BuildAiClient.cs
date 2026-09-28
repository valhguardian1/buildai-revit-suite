using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using BuildAI.Core.Buffering;
using BuildAI.Core.Configuration;
using BuildAI.Core.Logging;
using BuildAI.Core.Models;
using BuildAI.Core.Transport;

namespace BuildAI.Core
{
    public enum ConnectionState { Disconnected, Connected, Paused }

    /// <summary>
    /// High-level facade every plugin uses. Owns the transport, the offline
    /// buffer, and the background flush loop with exponential backoff.
    /// Sending touches no Revit API, so it is safe to call from any thread —
    /// collect read-only data on the Revit thread (specification §12.1) and hand it here.
    /// </summary>
    public sealed class BuildAiClient : IDisposable
    {
        private readonly BuildAiOptions _options;
        private readonly IBuildAiTransport _transport;
        private readonly OfflineQueue _queue;
        private readonly SemaphoreSlim _flushGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _cts = new CancellationTokenSource();

        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public int PendingCount => _queue.Count;
        public event Action<ConnectionState> StateChanged;

        public BuildAiClient(BuildAiOptions options, IBuildAiTransport transport, OfflineQueue queue = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _queue = queue ?? new OfflineQueue();
        }

        public void Pause()  { SetState(ConnectionState.Paused); }
        public void Resume() { SetState(ConnectionState.Disconnected); _ = FlushAsync(); }

        /// <summary>Queue a work/element event for the /api/revit_work/ endpoint.</summary>
        public void EnqueueWork(RevitWorkItem item)
        {
            if (State == ConnectionState.Paused) return;
            _queue.Enqueue(new QueuedRequest
            {
                Url = _options.ResolveWorkUrl(),
                Json = JsonConvert.SerializeObject(item)
            });
            _ = FlushAsync();
        }

        public void EnqueueJson(string url, object payload)
        {
            if (State == ConnectionState.Paused || string.IsNullOrWhiteSpace(url) || payload == null) return;
            _queue.Enqueue(new QueuedRequest { Url = NormalizeLegacySessionHeartbeatUrl(url), Json = JsonConvert.SerializeObject(payload) });
        }

        public void EnqueueWorkBatch(IReadOnlyList<RevitWorkItem> items)
        {
            if (State == ConnectionState.Paused || items == null || items.Count == 0) return;
            _queue.Enqueue(new QueuedRequest
            {
                Url = _options.ResolveWorkUrl().TrimEnd('/') + "/batch",
                Json = JsonConvert.SerializeObject(new { events = items })
            });
        }

        /// <summary>Send the full materials array (specification Plugin 2).</summary>
        public void EnqueueMaterials(string projectId, IReadOnlyList<RevitMaterialItem> items)
        {
            if (State == ConnectionState.Paused) return;
            var url = _options.ResolveMaterialsUrl(projectId);
            _queue.Enqueue(new QueuedRequest
            {
                Url = url,
                Json = JsonConvert.SerializeObject(items)
            });
            PluginLog.Info("BuildAI materials request queued", new { projectId, rows = items?.Count ?? 0, url });
            _ = FlushAsync();
        }

        /// <summary>
        /// Drains the buffer. Re-entrant-safe; on failure items go back to the
        /// front conceptually (we re-enqueue) and we back off before retrying.
        /// </summary>
        public async Task FlushAsync()
        {
            if (State == ConnectionState.Paused) return;
            if (!await _flushGate.WaitAsync(0).ConfigureAwait(false)) return; // a flush is already running
            try
            {
                int backoffMs = 1000;
                while (_queue.TryDequeue(out var req))
                {
                    // Requests restored from an outbox created by 6.6.20 or older
                    // can still contain the unsupported /heartbeat suffix. Migrate
                    // them immediately before transport so an old durable entry can
                    // never bypass the corrected SessionManager route.
                    req.Url = NormalizeLegacySessionHeartbeatUrl(req.Url);
                    var result = await _transport.PostJsonAsync(req.Url, req.Json, _cts.Token)
                                                 .ConfigureAwait(false);
                    if (result.Success)
                    {
                        PluginLog.Info("BuildAI queued request delivered", new { req.Url, req.Attempts });
                        SetState(ConnectionState.Connected);
                        backoffMs = 1000;
                        continue;
                    }

                    // Failed: put it back, back off, and stop this drain pass.
                    req.Attempts++;
                    PluginLog.Warn("BuildAI queued request delivery failed", new { req.Url, req.Attempts, result.StatusCode, result.Error });
                    _queue.Enqueue(req);
                    _queue.Persist();
                    SetState(ConnectionState.Disconnected);
                    await Task.Delay(Math.Min(backoffMs, 30000), _cts.Token).ConfigureAwait(false);
                    break;
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex) { PluginLog.Error("FlushAsync failed", ex); }
            finally
            {
                // Remove successfully delivered legacy entries from the durable
                // mirror as part of the same drain pass. Otherwise a Revit crash
                // could restore the old URL from the stale on-disk outbox.
                _queue.Persist();
                _flushGate.Release();
            }
        }

        private string NormalizeLegacySessionHeartbeatUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;

            const string legacySuffix = "/heartbeat";
            var trimmed = url.TrimEnd('/');
            if (!trimmed.EndsWith(legacySuffix, StringComparison.OrdinalIgnoreCase)) return url;

            var sessionUrl = trimmed.Substring(0, trimmed.Length - legacySuffix.Length);
            var configuredRoot = _options.ResolveSessionsUrl().TrimEnd('/') + "/";
            var isConfiguredSession = sessionUrl.StartsWith(configuredRoot, StringComparison.OrdinalIgnoreCase)
                                      && sessionUrl.Length > configuredRoot.Length;
            var isStandardSession = sessionUrl.IndexOf("/api/revit/sessions/", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isConfiguredSession && !isStandardSession) return url;

            PluginLog.Info("Legacy session heartbeat URL migrated", new { OldUrl = url, Url = sessionUrl });
            return sessionUrl;
        }

        private void SetState(ConnectionState s)
        {
            if (s == State) return;
            State = s;
            try { StateChanged?.Invoke(s); } catch { /* UI handler must not break us */ }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            _queue.Persist();
            (_transport as IDisposable)?.Dispose();
            _cts.Dispose();
            _flushGate.Dispose();
        }
    }
}
