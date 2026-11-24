// The one background thread that drives a YawSession (and discovery, if active).
//
// Why a dedicated Thread and not async/Tasks: the loop must keep running at a steady pace
// while Unity's main thread renders; it never touches the Unity API (events go through
// MainThreadQueue); and a plain thread avoids "async void" and SynchronizationContext surprises
// under IL2CPP. Exceptions inside the loop are caught and logged (rate-limited) so the thread
// can never die silently.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace YawVR.Next
{
    public sealed class YawNetworkRunner : IDisposable
    {
        readonly YawSession _session;
        readonly IEventSink _events;
        readonly RateLimitedLog _log;
        readonly Stopwatch _clock;
        readonly List<DiscoveredDevice> _found = new List<DiscoveredDevice>();
        Thread _thread;
        volatile bool _stop;
        IDeviceDiscovery _discovery;
        long _nextProbeMs;
        readonly object _discoveryLock = new object();

        public const int LoopWaitMs = 5;
        public const int ProbeIntervalMs = 1000;

        public YawNetworkRunner(YawSession session, IEventSink events, ILog log, Stopwatch clock)
        {
            _session = session;
            _events = events;
            _log = new RateLimitedLog(log ?? new NullLog());
            _clock = clock;
        }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "YawVR network" };
            _thread.Start();
        }

        /// <summary>Starts (or replaces) discovery; probes every second until StopDiscovery().</summary>
        public void StartDiscovery(IDeviceDiscovery discovery)
        {
            lock (_discoveryLock)
            {
                _discovery?.Dispose();
                _discovery = discovery;
                _nextProbeMs = 0;
            }
        }

        public void StopDiscovery()
        {
            lock (_discoveryLock)
            {
                _discovery?.Dispose();
                _discovery = null;
            }
        }

        void Loop()
        {
            while (!_stop)
            {
                try
                {
                    _session.WaitForTraffic(LoopWaitMs);
                    _session.Pump();
                    PumpDiscovery();
                }
                catch (Exception e)
                {
                    _log.Write(_clock.ElapsedMilliseconds, LogLevel.Error, "loop", "[YawNetworkRunner] unexpected error: " + e);
                    Thread.Sleep(10);
                }
            }
        }

        void PumpDiscovery()
        {
            lock (_discoveryLock)
            {
                if (_discovery == null) return;
                long now = _clock.ElapsedMilliseconds;
                if (now >= _nextProbeMs)
                {
                    _discovery.Probe();
                    _nextProbeMs = now + ProbeIntervalMs;
                }
                _found.Clear();
                _discovery.Poll(_found);
                foreach (var d in _found) _events.Post(new YawEvent { Kind = YawEventKind.DeviceDiscovered, Device = d });
            }
        }

        /// <summary>Stops the thread (waits up to 1 s). Call from OnDisable/OnDestroy.</summary>
        public void Dispose()
        {
            _stop = true;
            var t = _thread;
            _thread = null;
            if (t != null && t != Thread.CurrentThread) t.Join(1000);
            StopDiscovery();
        }
    }
}
