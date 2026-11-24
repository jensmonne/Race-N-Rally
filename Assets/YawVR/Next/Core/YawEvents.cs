// Events the session produces. They are created on the network thread and handed to the
// main thread through MainThreadQueue; the Unity layer raises them as C# events in Update().
using System.Collections.Concurrent;
using System.Net;
using System.Threading;

namespace YawVR.Next
{
    public enum YawEventKind
    {
        /// <summary>Handshake done; the session is authenticated.</summary>
        Connected,
        /// <summary>The session ended (Message says why). Pose streaming has stopped.</summary>
        Disconnected,
        /// <summary>A connection attempt failed (Message says why); a retry may follow.</summary>
        ConnectFailed,
        /// <summary>The rig's state changed (State).</summary>
        StateChanged,
        CommandAccepted,
        /// <summary>The server (or the client itself, e.g. "not connected") refused a command.</summary>
        CommandRejected,
        EmergencyStopSent,
        /// <summary>The server confirmed the e-stop (CommandAccepted or state EMERGENCY_STOPPED).</summary>
        EmergencyStopConfirmed,
        /// <summary>No confirmation within the deadline. The rig may NOT be stopped: use the physical e-stop.</summary>
        EmergencyStopUnconfirmed,
        DeviceDiscovered,
        /// <summary>New telemetry is available in the state model (coalesced: at most one per drain).</summary>
        TelemetryUpdated,
    }

    public sealed class DiscoveredDevice
    {
        public IPAddress Address;
        public int Port;
        public string Uuid;
        public string Name;
        public bool Available;
        public uint ProtocolVersion;
        public override string ToString() => $"{Name} ({Uuid}) at {Address}:{Port} {(Available ? "AVAILABLE" : "RESERVED")}";
    }

    public sealed class YawEvent
    {
        public YawEventKind Kind;
        public string Message;
        public string CommandName;
        public uint CommandId;
        public YawVR.Proto.Reason Reason;
        public YawVR.Proto.State State;
        /// <summary>For Disconnected/ConnectFailed: true if no automatic retry will follow.</summary>
        public bool Permanent;
        public DiscoveredDevice Device;

        public override string ToString() =>
            $"{Kind} {CommandName} {(Reason != YawVR.Proto.Reason.Unspecified ? Reason.ToString() : "")} {State} {Message}".Trim();
    }

    public interface IEventSink
    {
        void Post(YawEvent e);
    }

    /// <summary>
    /// Thread-safe hand-off from the network thread to Unity's main thread: Post() from any
    /// thread, Drain() from Update(). Telemetry is coalesced (latest wins, no backlog); every
    /// other event is kept, so safety events (Disconnected, CommandRejected, e-stop) are never
    /// dropped. Non-safety events are bounded to avoid unbounded growth if Update() stalls.
    /// </summary>
    public sealed class MainThreadQueue : IEventSink
    {
        public const int MaxQueued = 512;
        readonly ConcurrentQueue<YawEvent> _queue = new ConcurrentQueue<YawEvent>();
        int _telemetryPending;
        static readonly YawEvent TelemetryEvent = new YawEvent { Kind = YawEventKind.TelemetryUpdated };

        public void Post(YawEvent e)
        {
            if (e.Kind == YawEventKind.TelemetryUpdated)
            {
                Interlocked.Exchange(ref _telemetryPending, 1); // no allocation at 20 Hz
                return;
            }
            bool safety = e.Kind == YawEventKind.Disconnected || e.Kind == YawEventKind.CommandRejected
                || e.Kind == YawEventKind.EmergencyStopSent || e.Kind == YawEventKind.EmergencyStopConfirmed
                || e.Kind == YawEventKind.EmergencyStopUnconfirmed || e.Kind == YawEventKind.StateChanged
                || e.Kind == YawEventKind.Connected;
            if (!safety && _queue.Count >= MaxQueued) return;
            _queue.Enqueue(e);
        }

        /// <summary>Main thread only. Calls <paramref name="handler"/> for everything queued so far.</summary>
        public void Drain(System.Action<YawEvent> handler)
        {
            int n = _queue.Count;
            while (n-- > 0 && _queue.TryDequeue(out var e)) handler(e);
            if (Interlocked.Exchange(ref _telemetryPending, 0) == 1) handler(TelemetryEvent);
        }
    }
}
