using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Timers;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BuildAI.Core;
using BuildAI.Core.Configuration;
using BuildAI.Core.Logging;
using BuildAI.Core.Models;

namespace Plugin1.TimeModelAnalytics.Tracking
{
    /// <summary>
    /// Automatic per-document session lifecycle. Revit data is captured only in
    /// Revit event contexts; the timer only serializes already captured counters.
    /// Heartbeats are delta based and durable through BuildAiClient's offline queue.
    /// </summary>
    public sealed class SessionManager : IDisposable
    {
        private sealed class State
        {
            public string Key, ClientId, ModelUid, ProjectId, ProjectName, ProjectNumber, Title, Path, CentralPath, User, ActiveView, ActiveViewType;
            public bool IsWorkshared;
            public DateTime StartedUtc, LastActivityUtc, LastHeartbeatUtc;
            public long Sequence, Added, Modified, Deleted, Saves, Syncs, TotalAdded, TotalModified, TotalDeleted, TotalSaves, TotalSyncs, TotalActive, TotalIdle;
        }

        private readonly UIControlledApplication _ui;
        private readonly BuildAiClient _client;
        private readonly BuildAiOptions _options;
        private readonly Timer _timer;
        private readonly object _gate = new object();
        private readonly Dictionary<string, State> _states = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public ActivityTracker Activity { get; }
        public BuildAiClient Client => _client;

        public SessionManager(UIControlledApplication ui, BuildAiClient client, BuildAiOptions options)
        {
            _ui = ui; _client = client; _options = options;
            Activity = new ActivityTracker(options.IdleTimeoutMinutes);
            ui.ControlledApplication.DocumentOpened += OnOpened;
            ui.ControlledApplication.DocumentClosing += OnClosing;
            ui.ControlledApplication.DocumentChanged += OnChanged;
            ui.ControlledApplication.DocumentSaved += OnSaved;
            ui.ControlledApplication.DocumentSynchronizedWithCentral += OnSynced;
            ui.ViewActivated += OnViewActivated;
            _timer = new Timer(Math.Max(15, options.SessionUpdateSeconds) * 1000) { AutoReset = true };
            _timer.Elapsed += OnTimer; _timer.Start();
            PluginLog.Info("Automatic SessionManager started.");
        }

        private static string Key(Document d) => d == null ? null : (!string.IsNullOrWhiteSpace(d.PathName) ? d.PathName : d.Title + "|" + d.GetHashCode());

        private void OnOpened(object sender, DocumentOpenedEventArgs e)
        {
            if (e.Document == null) return;
            var sw = Stopwatch.StartNew();
            PluginLog.Info($"Session initialization started for {e.Document.Title}.");
            Ensure(e.Document);
            sw.Stop();
            PluginLog.Info($"Session initialization finished in {sw.ElapsedMilliseconds} ms for {e.Document.Title}.");
        }
        private void OnClosing(object sender, DocumentClosingEventArgs e) { if (e.Document != null) Finish(e.Document, "document_closed"); }
        private void OnSaved(object sender, DocumentSavedEventArgs e) { Touch(e.Document, s => { s.Saves++; s.TotalSaves++; }); }
        private void OnSynced(object sender, DocumentSynchronizedWithCentralEventArgs e) { Touch(e.Document, s => { s.Syncs++; s.TotalSyncs++; }); }

        private void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            var d = e.CurrentActiveView?.Document; if (d == null) return;
            Touch(d, s => { s.ActiveView = e.CurrentActiveView.Name; s.ActiveViewType = e.CurrentActiveView.ViewType.ToString(); });
        }

        private void OnChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                var d = e.GetDocument(); if (d == null) return;
                var a = e.GetAddedElementIds()?.Count ?? 0;
                var m = e.GetModifiedElementIds()?.Count ?? 0;
                var x = e.GetDeletedElementIds()?.Count ?? 0;
                Touch(d, s => { s.Added += a; s.Modified += m; s.Deleted += x; s.TotalAdded += a; s.TotalModified += m; s.TotalDeleted += x; });
                var batch = new List<RevitWorkItem>();
                batch.AddRange(ChangeCollector.Collect(d, e.GetAddedElementIds(), WorkMethod.Add, d.Application.Username));
                batch.AddRange(ChangeCollector.Collect(d, e.GetModifiedElementIds(), WorkMethod.Modify, d.Application.Username));
                batch.AddRange(ChangeCollector.Collect(d, e.GetDeletedElementIds(), WorkMethod.Delete, d.Application.Username));
                _client.EnqueueWorkBatch(batch);
            }
            catch (Exception ex) { PluginLog.Error("Session document change collection failed", ex); }
        }

        private void Ensure(Document d)
        {
            if (d == null || d.IsFamilyDocument) return;
            var key = Key(d); if (key == null) return;
            lock (_gate)
            {
                if (_states.ContainsKey(key)) return;
                var now = DateTime.UtcNow;
                var s = new State
                {
                    Key=key, ClientId=Guid.NewGuid().ToString("D"), ModelUid=d.ProjectInformation?.UniqueId ?? d.Title,
                    ProjectId=d.ProjectInformation?.Number ?? "", ProjectName=d.ProjectInformation?.Name ?? d.Title,
                    ProjectNumber=d.ProjectInformation?.Number ?? "", Title=d.Title, Path=d.PathName ?? "", IsWorkshared=d.IsWorkshared,
                    User=d.Application.Username ?? Environment.UserName, StartedUtc=now, LastActivityUtc=now, LastHeartbeatUtc=now
                };
                try { if (d.IsWorkshared) s.CentralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(d.GetWorksharingCentralModelPath()); } catch { }
                _states[key]=s;
                _client.EnqueueJson(_options.ResolveSessionsUrl(), new RevitSessionStart
                {
                    ClientSessionId=s.ClientId, RevitModelUid=s.ModelUid, ProjectId=s.ProjectId, ProjectName=s.ProjectName,
                    ProjectNumber=s.ProjectNumber, ModelTitle=s.Title, ModelPath=s.Path, IsWorkshared=s.IsWorkshared,
                    CentralModelPath=s.CentralPath, RevitVersion=d.Application.VersionNumber,
                    PluginVersion=Assembly.GetExecutingAssembly().GetName().Version?.ToString(), Username=s.User,
                    MachineName=Environment.MachineName, StartedAtUtc=now
                });
                PluginLog.Info($"Session started automatically: {s.ClientId}, {s.Title}");
            }
        }

        private void Touch(Document d, Action<State> change)
        {
            Ensure(d); var key=Key(d); if (key==null) return;
            lock(_gate) if (_states.TryGetValue(key,out var s)) { s.LastActivityUtc=DateTime.UtcNow; change?.Invoke(s); }
            Activity.Touch();
        }

        private void OnTimer(object sender, ElapsedEventArgs e)
        {
            try
            {
                List<RevitSessionHeartbeat> payloads = new List<RevitSessionHeartbeat>();
                lock (_gate)
                {
                    var now=DateTime.UtcNow;
                    foreach(var s in _states.Values)
                    {
                        var elapsed=Math.Max(0,(int)(now-s.LastHeartbeatUtc).TotalSeconds);
                        var idle=(now-s.LastActivityUtc).TotalMinutes >= _options.IdleTimeoutMinutes;
                        if(idle) s.TotalIdle += elapsed; else s.TotalActive += elapsed;
                        payloads.Add(new RevitSessionHeartbeat
                        {
                            ClientSessionId=s.ClientId, Sequence=++s.Sequence, SentAtUtc=now, State=idle?"idle":"active",
                            ActiveView=s.ActiveView, ActiveViewType=s.ActiveViewType,
                            ActiveSecondsDelta=idle?0:elapsed, IdleSecondsDelta=idle?elapsed:0,
                            AddedElementsDelta=(int)s.Added, ModifiedElementsDelta=(int)s.Modified, DeletedElementsDelta=(int)s.Deleted,
                            SaveCountDelta=(int)s.Saves, SyncCountDelta=(int)s.Syncs, PendingEvents=_client.PendingCount,
                            MemoryUsedMb=Process.GetCurrentProcess().WorkingSet64/1024/1024
                        });
                        s.Added=s.Modified=s.Deleted=s.Saves=s.Syncs=0; s.LastHeartbeatUtc=now;
                    }
                }
                foreach(var p in payloads)
                {
                    // BuildAI accepts session updates on the session resource itself.
                    // Do not append the legacy /heartbeat suffix: that route is not exposed by the backend.
                    var sessionUrl = _options.ResolveSessionsUrl().TrimEnd('/') + "/" + p.ClientSessionId;
                    PluginLog.Info("Session heartbeat queued", new { p.ClientSessionId, p.Sequence, Url = sessionUrl });
                    _client.EnqueueJson(sessionUrl, p);
                }
                _ = _client.FlushAsync();
            }
            catch(Exception ex){ PluginLog.Error("Session heartbeat failed",ex); }
        }

        private void Finish(Document d,string reason)
        {
            var key=Key(d); State s=null;
            lock(_gate){ if(key!=null && _states.TryGetValue(key,out s)) _states.Remove(key); }
            if(s==null) return;
            _client.EnqueueJson(_options.ResolveSessionsUrl().TrimEnd('/')+"/"+s.ClientId+"/finish",new RevitSessionFinish
            {
                ClientSessionId=s.ClientId, FinishedAtUtc=DateTime.UtcNow, FinishReason=reason,
                TotalActiveSeconds=s.TotalActive, TotalIdleSeconds=s.TotalIdle, TotalAddedElements=s.TotalAdded,
                TotalModifiedElements=s.TotalModified, TotalDeletedElements=s.TotalDeleted,
                TotalSaveCount=s.TotalSaves, TotalSyncCount=s.TotalSyncs
            });
        }

        public void Dispose()
        {
            if(_disposed)return; _disposed=true;
            try
            {
                _timer.Stop(); _timer.Elapsed-=OnTimer; _timer.Dispose();
                List<State> states; lock(_gate){ states=_states.Values.ToList(); _states.Clear(); }
                foreach(var s in states) _client.EnqueueJson(_options.ResolveSessionsUrl().TrimEnd('/')+"/"+s.ClientId+"/finish",new RevitSessionFinish
                { ClientSessionId=s.ClientId, FinishedAtUtc=DateTime.UtcNow, FinishReason="revit_closed", TotalActiveSeconds=s.TotalActive, TotalIdleSeconds=s.TotalIdle,
                  TotalAddedElements=s.TotalAdded, TotalModifiedElements=s.TotalModified, TotalDeletedElements=s.TotalDeleted, TotalSaveCount=s.TotalSaves, TotalSyncCount=s.TotalSyncs });
                _ui.ControlledApplication.DocumentOpened-=OnOpened; _ui.ControlledApplication.DocumentClosing-=OnClosing;
                _ui.ControlledApplication.DocumentChanged-=OnChanged; _ui.ControlledApplication.DocumentSaved-=OnSaved;
                _ui.ControlledApplication.DocumentSynchronizedWithCentral-=OnSynced; _ui.ViewActivated-=OnViewActivated;
                _ = _client.FlushAsync();
            }
            catch(Exception ex){PluginLog.Error("SessionManager.Dispose failed",ex);}
        }
    }
}
