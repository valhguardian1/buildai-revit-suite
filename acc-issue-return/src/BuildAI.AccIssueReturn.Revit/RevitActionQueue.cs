using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BuildAI.AccIssueReturn.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace BuildAI.AccIssueReturn.Revit;

// The window owns one queue and one ExternalEvent for its entire modeless lifetime.
internal sealed class RevitActionQueue : IExternalEventHandler, IDisposable
{
    private readonly object gate = new();
    private readonly LinkedList<Request> pending = new();
    private ExternalEvent? externalEvent;
    private bool disposed;
    private bool running;
    private readonly Document document;
    internal bool IsRevitActionRunning { get { lock (gate) return running; } }
    internal string PendingAction { get { lock (gate) return pending.First?.Value.Action ?? ""; } }
    internal bool CancellationRequested { get { lock (gate) return disposed; } }
    internal int QueueLength { get { lock (gate) return pending.Count; } }

    internal RevitActionQueue(Document document) { this.document = document; externalEvent = ExternalEvent.Create(this); }

    internal Task<T> Enqueue<T>(string action, string? issueId, Func<UIApplication, T> execute, bool coalesceOpen = false)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (disposed) { completion.SetException(new ObjectDisposedException(nameof(RevitActionQueue))); return completion.Task; }
            if (coalesceOpen)
            {
                var node = pending.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (node.Value.Action == "OpenIssue" || node.Value.Action == "Prefetch") { pending.Remove(node); node.Value.Cancel(); }
                    node = next;
                }
            }
            var request = new Request(action, issueId, app => execute(app), value => completion.TrySetResult((T)value!),
                error => completion.TrySetException(error), () => completion.TrySetCanceled());
            if (coalesceOpen) pending.AddFirst(request); else pending.AddLast(request);
            AccIssueReturnLog.Event("ACC_EXTERNAL_EVENT_REQUESTED", new { action, issueIdSuffix = Short(issueId), queueLength = pending.Count, isActionRunning = running });
        }
        try
        {
            var status = externalEvent!.Raise();
            if (status == ExternalEventRequest.Denied || status == ExternalEventRequest.TimedOut)
                FailAll(new InvalidOperationException("Revit did not accept the external event: " + status));
        }
        catch (Exception ex) { FailAll(ex); }
        return completion.Task;
    }

    public void Execute(UIApplication app)
    {
        while (true)
        {
            Request request;
            lock (gate)
            {
                if (disposed || pending.Count == 0) { running = false; return; }
                request = pending.First!.Value; pending.RemoveFirst(); running = true;
            }
            var timer = Stopwatch.StartNew(); Exception? error = null;
            try
            {
                if (request.Action != "CloseReview" && (app.ActiveUIDocument?.Document == null || !app.ActiveUIDocument.Document.Equals(document)))
                    throw new InvalidOperationException("The active Revit document changed. Reload the Revit context before continuing.");
                request.Succeed(request.Execute(app));
            }
            catch (Exception ex) { error = ex; request.Fail(ex); }
            finally
            {
                timer.Stop();
                AccIssueReturnLog.Event("ACC_EXTERNAL_EVENT_COMPLETED", new { action = request.Action,
                    issueIdSuffix = Short(request.IssueId), success = error == null, elapsedMs = timer.ElapsedMilliseconds,
                    warning = "", errorType = error?.GetType().Name ?? "" });
            }
        }
    }

    public string GetName() => "BuildAI ACC Issue Return actions";

    internal void CancelPending()
    {
        lock (gate) { foreach (var request in pending) request.Cancel(); pending.Clear(); }
    }

    private void FailAll(Exception error)
    {
        lock (gate) { foreach (var request in pending) request.Fail(error); pending.Clear(); }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; foreach (var request in pending) request.Cancel(); pending.Clear(); }
        externalEvent?.Dispose(); externalEvent = null;
    }

    private static string Short(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value!.Substring(Math.Max(0, value.Length - 8));

    private sealed class Request
    {
        internal readonly string Action; internal readonly string? IssueId;
        internal readonly Func<UIApplication, object?> Execute;
        internal readonly Action<object?> Succeed; internal readonly Action<Exception> Fail; internal readonly Action Cancel;
        internal Request(string action, string? issueId, Func<UIApplication, object?> execute,
            Action<object?> succeed, Action<Exception> fail, Action cancel)
        { Action = action; IssueId = issueId; Execute = execute; Succeed = succeed; Fail = fail; Cancel = cancel; }
    }
}
