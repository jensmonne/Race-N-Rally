// Logging used by the Core classes (no UnityEngine here; the Unity layer plugs in Debug.Log).
using System;
using System.Collections.Generic;

namespace YawVR.Next
{
    public enum LogLevel
    {
        Info,
        Warning,
        Error,
        /// <summary>Safety-relevant (e-stop, link loss, rejected commands). Always shown, prefixed loudly.</summary>
        Safety,
    }

    public interface ILog
    {
        void Write(LogLevel level, string message);
    }

    /// <summary>
    /// Suppresses repeats of the same message key within an interval and reports how many were
    /// suppressed when it lets one through again. Stops e.g. a dead network from flooding the console.
    /// Not thread-safe on its own: callers hold their own lock.
    /// </summary>
    public sealed class RateLimitedLog
    {
        readonly ILog _inner;
        readonly long _intervalMs;
        readonly Dictionary<string, (long lastMs, int suppressed)> _last = new Dictionary<string, (long, int)>();

        public RateLimitedLog(ILog inner, long intervalMs = 2000)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _intervalMs = intervalMs;
        }

        public void Write(long nowMs, LogLevel level, string key, string message)
        {
            if (_last.TryGetValue(key, out var e) && nowMs - e.lastMs < _intervalMs)
            {
                _last[key] = (e.lastMs, e.suppressed + 1);
                return;
            }
            int suppressed = e.suppressed;
            _last[key] = (nowMs, 0);
            _inner.Write(level, suppressed > 0 ? $"{message} ({suppressed} similar messages suppressed)" : message);
        }

        /// <summary>Unthrottled (for one-off events that must always appear).</summary>
        public void Always(LogLevel level, string message) => _inner.Write(level, message);
    }

    /// <summary>A log that discards everything (default for tests that do not care).</summary>
    public sealed class NullLog : ILog
    {
        public void Write(LogLevel level, string message) { }
    }
}
