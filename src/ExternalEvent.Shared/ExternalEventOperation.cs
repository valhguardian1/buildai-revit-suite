using System;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace BuildAI.RevitCompatibility
{
    /// <summary>
    /// Tracks both boundaries of a modeless Revit ExternalEvent: the moment
    /// Execute starts and the moment the requested operation completes.
    /// </summary>
    public sealed class ExternalEventOperation<T>
    {
        private readonly TaskCompletionSource<bool> _started =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<T> _completion =
            new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancelled;

        public ExternalEventOperation(string name, IProgress<string> progress)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Unnamed" : name;
            Progress = progress;
        }

        public string Name { get; }
        public IProgress<string> Progress { get; }
        public Task Started => _started.Task;
        public Task<T> Completion => _completion.Task;
        public bool IsCancelled => Volatile.Read(ref _cancelled) != 0;

        public bool TryEnter()
        {
            if (IsCancelled) return false;
            _started.TrySetResult(true);
            return !IsCancelled;
        }

        public void Cancel()
        {
            Interlocked.Exchange(ref _cancelled, 1);
        }

        public bool TrySetResult(T result)
        {
            return !IsCancelled && _completion.TrySetResult(result);
        }

        public bool TrySetException(Exception exception)
        {
            return !IsCancelled && _completion.TrySetException(exception);
        }
    }

    public static class ExternalEventAwaiter
    {
        /// <summary>
        /// How long to wait for Revit to call Execute() before giving up.
        /// <para>
        /// This was 30 seconds, which is a performance expectation rather than a
        /// safety net, and large models exceed it routinely: on 30 Aug 2026 a run
        /// on a 3251-result model failed here, while earlier successful runs on the
        /// same project family entered Execute() only after 51.1 s (publication
        /// views) and 41.0 s (synchronise). Those runs were inside the margin only
        /// by luck. The purpose of the limit is to stop waiting on a Revit that will
        /// never become idle - a modal dialog left open, an unfinished command - and
        /// five minutes serves that without failing healthy work.
        /// </para>
        /// </summary>
        public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromMinutes(5);

        public static async Task<T> RaiseAndWaitAsync<T>(
            ExternalEvent externalEvent,
            ExternalEventOperation<T> operation,
            TimeSpan startTimeout,
            TimeSpan completionTimeout)
        {
            if (externalEvent == null) throw new InvalidOperationException("The Revit ExternalEvent is not initialized.");
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            ExternalEventRequest raiseResult;
            try
            {
                raiseResult = externalEvent.Raise();
            }
            catch (Exception ex)
            {
                operation.Cancel();
                operation.Progress?.Report(
                    "EXTERNAL EVENT RAISE FAILED\nOperation: " + operation.Name +
                    "\nException: " + ex);
                throw;
            }

            var resultText = raiseResult.ToString();
            operation.Progress?.Report(
                "EXTERNAL EVENT RAISE\nOperation: " + operation.Name +
                "\nResult: " + resultText +
                "\nStart timeout: " + startTimeout.TotalSeconds.ToString("0") + " seconds" +
                "\nCompletion timeout: " + completionTimeout.TotalSeconds.ToString("0") + " seconds");

            if (!string.Equals(resultText, "Accepted", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(resultText, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                operation.Cancel();
                throw new InvalidOperationException(
                    "Revit rejected the ExternalEvent for " + operation.Name +
                    " (Raise result: " + resultText + "). Close any active Revit dialog and try again.");
            }

            // Revit runs Execute() only when its main loop next becomes idle. On a
            // large federated model that can legitimately take a long time - measured
            // delays for this exact operation range from 0.0 s on a small model to
            // 51.1 s on a large one - and the wait is longest right after a heavy
            // command such as reloading links, which is precisely when Issue creation
            // is normally started. The budget here is a safety net against a wedged
            // Revit, not a performance expectation, so it is generous and the wait is
            // reported as it happens rather than surfacing as a silent stall followed
            // by a sudden failure.
            var startDeadline = DateTime.UtcNow + startTimeout;
            var heartbeat = TimeSpan.FromSeconds(10);
            var waitedForStart = TimeSpan.Zero;
            while (true)
            {
                var remaining = startDeadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;

                var slice = remaining < heartbeat ? remaining : heartbeat;
                var winner = await Task.WhenAny(operation.Started, Task.Delay(slice));
                if (winner == operation.Started) break;

                waitedForStart += slice;
                operation.Progress?.Report(
                    "EXTERNAL EVENT WAITING FOR REVIT" + Environment.NewLine +
                    "Operation: " + operation.Name + Environment.NewLine +
                    "Waited: " + waitedForStart.TotalSeconds.ToString("0") + "s of " +
                    startTimeout.TotalSeconds.ToString("0") + "s" + Environment.NewLine +
                    "Revit has not become idle yet. This is normal on large models, " +
                    "especially straight after a link reload. It also happens when a Revit " +
                    "dialog or an active command is open.");
            }

            if (!operation.Started.IsCompleted)
            {
                operation.Cancel();
                operation.Progress?.Report(
                    "EXTERNAL EVENT START TIMEOUT\nOperation: " + operation.Name +
                    "\nExecute() did not start within " + startTimeout.TotalSeconds.ToString("0") + " seconds.");
                throw new TimeoutException(
                    "Revit did not start " + operation.Name + " within " +
                    startTimeout.TotalMinutes.ToString("0.#") +
                    " minutes. Revit never became idle. Close any open Revit dialog, finish any " +
                    "active command, then try again. Nothing was changed and no Issues were created.");
            }

            var completed = await Task.WhenAny(operation.Completion, Task.Delay(completionTimeout));
            if (completed != operation.Completion)
            {
                operation.Cancel();
                operation.Progress?.Report(
                    "EXTERNAL EVENT COMPLETION TIMEOUT\nOperation: " + operation.Name +
                    "\nExecute() did not complete within " + completionTimeout.TotalSeconds.ToString("0") + " seconds.");
                throw new TimeoutException(
                    "Revit started " + operation.Name + " but did not complete it within " +
                    completionTimeout.TotalMinutes.ToString("0.#") +
                    " minutes. See the Issue Creation log for the last Execute stage.");
            }

            return await operation.Completion;
        }
    }
}
