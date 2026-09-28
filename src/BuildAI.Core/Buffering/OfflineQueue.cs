using System;
using System.Collections.Concurrent;
using System.IO;
using Newtonsoft.Json;
using BuildAI.Core.Logging;

namespace BuildAI.Core.Buffering
{
    /// <summary>One buffered POST awaiting (re)delivery.</summary>
    public sealed class QueuedRequest
    {
        public string Url { get; set; }
        public string Json { get; set; }
        public DateTime EnqueuedUtc { get; set; } = DateTime.UtcNow;
        public int Attempts { get; set; }
    }

    /// <summary>
    /// Durable FIFO buffer (specification §4.4 / §9.2): if the connection drops, events are
    /// kept in memory and mirrored to disk so nothing is lost across a Revit
    /// restart. Flushed by BuildAiClient on reconnect with backoff.
    /// </summary>
    public sealed class OfflineQueue
    {
        private readonly ConcurrentQueue<QueuedRequest> _queue = new ConcurrentQueue<QueuedRequest>();
        private readonly string _file;
        private readonly object _fileGate = new object();

        public OfflineQueue(string fileName = "outbox.jsonl")
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BuildAI", "buffer");
            Directory.CreateDirectory(dir);
            _file = Path.Combine(dir, fileName);
            Restore();
        }

        public int Count => _queue.Count;

        public void Enqueue(QueuedRequest item)
        {
            _queue.Enqueue(item);
            AppendToDisk(item);
        }

        public bool TryDequeue(out QueuedRequest item) => _queue.TryDequeue(out item);

        /// <summary>Rewrites the disk mirror from the current in-memory state.</summary>
        public void Persist()
        {
            lock (_fileGate)
            {
                try
                {
                    using (var w = new StreamWriter(_file, false))
                        foreach (var r in _queue.ToArray())
                            w.WriteLine(JsonConvert.SerializeObject(r));
                }
                catch (Exception ex) { PluginLog.Error("OfflineQueue.Persist failed", ex); }
            }
        }

        private void AppendToDisk(QueuedRequest item)
        {
            lock (_fileGate)
            {
                try { File.AppendAllText(_file, JsonConvert.SerializeObject(item) + Environment.NewLine); }
                catch (Exception ex) { PluginLog.Error("OfflineQueue.Append failed", ex); }
            }
        }

        private void Restore()
        {
            try
            {
                if (!File.Exists(_file)) return;
                foreach (var line in File.ReadAllLines(_file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var r = JsonConvert.DeserializeObject<QueuedRequest>(line);
                    if (r != null) _queue.Enqueue(r);
                }
                PluginLog.Info($"OfflineQueue restored {_queue.Count} pending item(s).");
            }
            catch (Exception ex) { PluginLog.Error("OfflineQueue.Restore failed", ex); }
        }
    }
}
