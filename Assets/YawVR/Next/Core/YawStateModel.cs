// The rig as the game sees it. Written by YawSession (network thread) from HelloAck,
// StateUpdate and Telemetry; read by the game through Snapshot (a copy, so no torn reads).
using YawVR.Proto;

namespace YawVR.Next
{
    public enum SessionPhase
    {
        /// <summary>Not connected and not trying.</summary>
        Idle,
        /// <summary>Handshake in progress.</summary>
        Connecting,
        /// <summary>Authenticated session.</summary>
        Connected,
        /// <summary>Waiting before the next connection attempt.</summary>
        Backoff,
        /// <summary>Gave up for a reason that retrying cannot fix (e.g. protocol version mismatch).</summary>
        Failed,
    }

    /// <summary>A copy of the rig state at one moment. Plain value type: safe to pass between threads.</summary>
    public struct YawRigState
    {
        public SessionPhase Phase;
        public State ServerState;
        public uint SessionId;
        public uint HeartbeatIntervalMs;
        public uint LinkTimeoutMs;
        public uint Faults;
        public uint EstopReasons;
        public uint Inputs;
        public bool HasActual;
        public float ActualYaw, ActualPitch, ActualRoll;
        public float TargetYaw, TargetPitch, TargetRoll;
        public int PwmRoll, PwmPitch, PwmYaw;
        public bool HasTemperature;
        public int TemperatureC;
        public float ThermalFactor;
        public float Ramp;
        public bool HasLimits;
        public float PitchBack, PitchForward, RollLimit, MaxPower;
        public ulong Tick;

        public bool IsEmergencyStopped => ServerState == State.EmergencyStopped || EstopReasons != 0;
    }

    public sealed class YawStateModel
    {
        readonly object _lock = new object();
        YawRigState _s;

        public YawRigState Snapshot { get { lock (_lock) return _s; } }

        internal void SetPhase(SessionPhase p) { lock (_lock) _s.Phase = p; }

        internal void ResetForNewSession()
        {
            lock (_lock)
            {
                var phase = _s.Phase;
                _s = default;
                _s.Phase = phase;
            }
        }

        internal void ApplyHelloAck(HelloAck a)
        {
            lock (_lock)
            {
                _s.SessionId = a.SessionId;
                _s.ServerState = a.State;
                _s.HeartbeatIntervalMs = a.HeartbeatIntervalMs;
                _s.LinkTimeoutMs = a.LinkTimeoutMs;
                if (a.Limits != null) ApplyLimits(a.Limits);
            }
        }

        void ApplyLimits(YawVR.Proto.Limits l)
        {
            _s.HasLimits = true;
            _s.PitchBack = l.PitchBack;
            _s.PitchForward = l.PitchForward;
            _s.RollLimit = l.Roll;
            _s.MaxPower = l.MaxPower;
        }

        /// <summary>Returns the previous server state.</summary>
        internal State ApplyStateUpdate(StateUpdate u)
        {
            lock (_lock)
            {
                var old = _s.ServerState;
                _s.ServerState = u.State;
                _s.Faults = u.Faults;
                _s.EstopReasons = u.EstopReasons;
                return old;
            }
        }

        /// <summary>Returns the previous server state.</summary>
        internal State ApplyTelemetry(Telemetry t)
        {
            lock (_lock)
            {
                var old = _s.ServerState;
                _s.Tick = t.Tick;
                _s.ServerState = t.State;
                _s.HasActual = t.Actual != null;
                if (t.Actual != null) { _s.ActualYaw = t.Actual.Yaw; _s.ActualPitch = t.Actual.Pitch; _s.ActualRoll = t.Actual.Roll; }
                if (t.Target != null) { _s.TargetYaw = t.Target.Yaw; _s.TargetPitch = t.Target.Pitch; _s.TargetRoll = t.Target.Roll; }
                _s.PwmRoll = t.PwmRoll; _s.PwmPitch = t.PwmPitch; _s.PwmYaw = t.PwmYaw;
                _s.HasTemperature = t.HasTemperatureC;
                _s.TemperatureC = t.TemperatureC;
                _s.ThermalFactor = t.ThermalFactor;
                _s.Ramp = t.Ramp;
                _s.EstopReasons = t.EstopReasons;
                _s.Faults = t.Faults;
                _s.Inputs = t.Inputs;
                if (t.Limits != null) ApplyLimits(t.Limits);
                return old;
            }
        }
    }
}
