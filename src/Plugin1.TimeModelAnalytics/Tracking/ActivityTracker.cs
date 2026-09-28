using System;

namespace Plugin1.TimeModelAnalytics.Tracking
{
    /// <summary>
    /// Active-time accounting (specification §4.2.1). Any Revit interaction calls Touch().
    /// Idle gaps longer than the configured timeout are NOT counted as active.
    /// Pure bookkeeping — no Revit API, no threads.
    /// </summary>
    public sealed class ActivityTracker
    {
        private readonly TimeSpan _idleTimeout;
        private DateTime? _lastActivityUtc;
        private TimeSpan _activeTotal = TimeSpan.Zero;

        public DateTime SessionStartUtc { get; } = DateTime.UtcNow;
        public long EventsSent { get; private set; }

        public ActivityTracker(int idleTimeoutMinutes)
            => _idleTimeout = TimeSpan.FromMinutes(Math.Max(1, idleTimeoutMinutes));

        /// <summary>Register a user interaction at "now" (UTC).</summary>
        public void Touch()
        {
            var now = DateTime.UtcNow;
            if (_lastActivityUtc.HasValue)
            {
                var gap = now - _lastActivityUtc.Value;
                if (gap <= _idleTimeout) _activeTotal += gap; // count only non-idle spans
            }
            _lastActivityUtc = now;
        }

        public void MarkEventSent() => EventsSent++;

        /// <summary>Active time accrued so far (idle excluded).</summary>
        public TimeSpan ActiveTime
        {
            get
            {
                if (_lastActivityUtc.HasValue)
                {
                    var gap = DateTime.UtcNow - _lastActivityUtc.Value;
                    if (gap <= _idleTimeout) return _activeTotal + gap;
                }
                return _activeTotal;
            }
        }
    }
}
