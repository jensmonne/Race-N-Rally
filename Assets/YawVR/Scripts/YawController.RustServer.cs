// Optional coupling of this SDK to the new Rust yawvr-server (protocol v2).
//
// Off by default. With "Use Rust Server" unticked nothing in this file runs and the SDK works
// exactly as before (TCP 50020 / UDP 50010 to the original YAWVRServer).
//
// Ticked: the one-line hooks in YawController.cs route discovery, connect, start, stop/park,
// calibrate, disconnect, motion and LED here. This file then drives the SDK's own states,
// callbacks and events (SetState, callBacks, onConnected, DeviceDiscovered, ControllerDelegate,
// device.ActualPosition, ...) from the new protocol, so YawDeviceConnector, YawTracker,
// MotionCompensation and the game scripts work unchanged.
//
// The protocol itself (authenticated UDP, heartbeats, reconnect, e-stop) is in Assets/YawVR/Next.
// Differences from the old server, on purpose:
//  - Motion is only sent after StartDevice() succeeded and while the rig is ACTIVE (the old SDK
//    also streamed in Connected).
//  - A lost link is detected (the old SDK could not): the rig parks by itself, the state goes to
//    Connecting, and the connection comes back automatically. The motion then stays off until
//    StartDevice() is called again, unless "Rust Restart After Reconnect" is ticked.
//  - A latched emergency stop is cleared once per game start, on the first connection (owner's
//    choice), never on a scene load or a reconnect. The server refuses it while the magnet is off.
//    Latched faults alone are not cleared, but the same command also clears a fault latched
//    together with the e-stop if its cause is gone.
using System;
using System.Net;
using UnityEngine;
using YawVR.Next;

namespace YawVR
{
    public partial class YawController
    {
        [Header("Server: original YAWVRServer or the new Rust yawvr-server")]
        [Tooltip("Off: the original YAWVR server (as before). On: the Rust yawvr-server (protocol v2, UDP 50050).")]
        [SerializeField] private bool useRustServer = false;
        [Tooltip("Session port of the Rust yawvr-server.")]
        [SerializeField] private int rustServerPort = 50050;
        [Tooltip("The password set on the Pi (password = \"...\" under [auth] in /etc/yawvr-rust/yawvr-server.toml). Both sides must match exactly.")]
        [SerializeField] private string rustPassword = "";
        [Tooltip("After the link was lost and came back by itself: start the motion again if it was running. Off = the game has to call StartDevice() again.")]
        [SerializeField] private bool rustRestartAfterReconnect = false;

        /// <summary>True if this controller talks to the Rust yawvr-server. The toggle is read once, in Awake.</summary>
        public bool UsesRustServer => RustActive;

        private bool RustActive => rustSession != null;

        private const float RustCommandTimeout = 10f; // as the old ResponseTimeout
        private const string RustOwnDisconnect = "client: "; // reason prefix of disconnects this file asked for

        private System.Diagnostics.Stopwatch rustClock;
        private MainThreadQueue rustQueue;
        private YawSessionConfig rustConfig;
        private YawSession rustSession;
        private YawNetworkRunner rustRunner;
        private YawMotionSender rustMotion;
        private Action<YawEvent> rustDispatch;
        private bool rustDiscovering;
        private bool rustEverConnected;   // the current connection reached Connected at least once
        private bool rustWasStarted;      // motion was running when the link was lost
        private uint rustStartId, rustStopId;
        private bool rustStopAccepted;
        private float rustCommandDeadline;
        private YawVR.Proto.State rustRigState = YawVR.Proto.State.Unspecified;
        private bool rustClearPending;    // waiting for the game-start e-stop clear before finishing the connect
        private uint rustClearId;
        private bool rustClearAnswered;
        private float rustClearDeadline;
        private const float RustClearTimeout = 2f;
        private static bool rustGameStartHandled; // process-wide: survives scene loads, reset at every game start
        private Color32 rustLastLed;
        private float rustLastLedTime = -10f;

        // ------------------------------------------------------------------ lifecycle

        // Runs at every game start and every Play in the Editor. Needed because this project has
        // domain reload disabled, so a static would otherwise survive between Play sessions.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RustResetGameStart() => rustGameStartHandled = false;

        private void RustAwake()
        {
            rustClock = System.Diagnostics.Stopwatch.StartNew();
            var clock = rustClock;
            rustQueue = new MainThreadQueue();
            rustConfig = new YawSessionConfig
            {
                ClientName = string.IsNullOrEmpty(gameName) ? "Unity" : gameName,
                AutoReconnect = true,
                Clock = () => clock.ElapsedMilliseconds,
            };
            var log = new RustUnityLog();
            rustSession = new YawSession(rustConfig, rustQueue, log);
            rustMotion = new YawMotionSender(rustSession);
            rustRunner = new YawNetworkRunner(rustSession, rustQueue, log, rustClock);
            rustDispatch = RustOnEvent;
            rustRunner.Start();
            Debug.Log("[YawController] Using the Rust yawvr-server (protocol v2, port " + rustServerPort + ")");
        }

        private void RustShutdown()
        {
            if (rustRunner == null) return;
            rustRunner.Dispose();
            rustSession.Dispose();
            rustRunner = null;
            rustSession = null;
        }

        private void Update()
        {
            if (!RustActive) return;
            rustQueue.Drain(rustDispatch);
            RustCheckProgress();
        }

        // ------------------------------------------------------------------ the SDK functions

        private void RustDiscoverDevices(int onPort)
        {
            // The old discovery port (50010) does not apply: protocol v2 discovers on UDP 50051 and
            // probes every second by itself, so repeated calls are fine.
            if (rustDiscovering) return;
            try
            {
                rustRunner.StartDiscovery(new YawDeviceDiscovery());
                rustDiscovering = true;
            }
            catch (System.Net.Sockets.SocketException e)
            {
                Debug.LogWarning("[YawController] Rust discovery unavailable: " + e.Message);
            }
        }

        private void RustConnectToDevice(YawDevice yawDevice, Action onSuccess, Action<string> onError)
        {
            if (yawDevice == null) { onError?.Invoke("[YawController] No device"); return; }
            if (state != ControllerState.Initial)
            {
                // Like the old SDK: an existing connection is closed first.
                rustSession.Disconnect(RustOwnDisconnect + "reconnect requested");
                SetState(ControllerState.Initial);
            }
            byte[] psk = RustLoadPsk(out string pskError);
            if (psk == null) { onError?.Invoke(pskError); return; }
            if (rustDiscovering) { rustRunner.StopDiscovery(); rustDiscovering = false; }

            device = yawDevice;
            callBacks.connectingSuccess = onSuccess;
            callBacks.connectingError = onError;
            rustEverConnected = false;
            rustWasStarted = false;
            rustClearPending = false;
            rustConfig.ClientName = string.IsNullOrEmpty(gameName) ? "Unity" : gameName;
            SetState(ControllerState.Connecting);
            // Always the Rust server's port: devices from the old discovery or the debug IP carry 50010.
            rustSession.Connect(new IPEndPoint(yawDevice.IPAddress, rustServerPort), psk);
        }

        private void RustStartDevice(Action onSuccess, Action<string> onError)
        {
            if (state != ControllerState.Connected)
            {
                onError?.Invoke("[YawController] Attempted to start device when device has not been in connected ready state");
                return;
            }
            uint id = rustSession.Start();
            if (id == 0) { onError?.Invoke("[YawController] Start not sent (not connected)"); return; }
            rustStartId = id;
            callBacks.startSuccess = onSuccess;
            callBacks.startError = onError;
            rustCommandDeadline = Time.unscaledTime + RustCommandTimeout;
            SetState(ControllerState.Starting);
        }

        private void RustStopDevice(bool park, Action onSuccess, Action<string> onError)
        {
            if (state != ControllerState.Started && state != ControllerState.Starting)
            {
                onError?.Invoke("[YawController] Attempted to stop simulator when simulator had not been in started state");
                return;
            }
            uint id = rustSession.Stop(park); // motion stops at once, before the answer
            if (id == 0) { onError?.Invoke("[YawController] Stop not sent (not connected)"); return; }
            RustFail(ref callBacks.startSuccess, ref callBacks.startError, "stopped");
            rustStopId = id;
            rustStopAccepted = false;
            callBacks.stopSuccess = onSuccess;
            callBacks.stopError = onError;
            rustCommandDeadline = Time.unscaledTime + RustCommandTimeout;
            SetState(ControllerState.Stopping);
        }

        private void RustCalibrateDevice(bool allAxis)
        {
            // The Rust server answers NOT_IMPLEMENTED (logged as a warning when the answer arrives).
            if (state == ControllerState.Connected) rustSession.Calibrate(allAxis);
        }

        private void RustDisconnectFromDevice(Action onSuccess, Action<string> onError)
        {
            if (state == ControllerState.Initial)
            {
                onError?.Invoke("[YawController] Attempted to disconnect when no device was connected");
                return;
            }
            rustSession.Disconnect(RustOwnDisconnect + "disconnect requested"); // the server parks the rig if it was moving
            rustWasStarted = false;
            RustFailPending("disconnected");
            Debug.Log("[YawController] Disconnected from device");
            onDisconnected?.Invoke();
            SetState(ControllerState.Initial);
            onSuccess?.Invoke();
        }

        private void RustSendRotation(OVector rotation)
        {
            // Sent only while the motion gate is open (connected + started + rig ACTIVE); NaN never leaves.
            bool on = buzzer != null && buzzer.isOn;
            rustMotion.Send(rotation.yaw, rotation.pitch, rotation.roll, on,
                on ? buzzer.right_amp : 0, on ? buzzer.center_amp : 0, on ? buzzer.hz : 0);
        }

        private void RustSendLED(Color32[] colors)
        {
            if (colors == null || colors.Length != 129) return;
            // Protocol v2 has one colour for the whole strip. TODO: which LED the original honoured is unknown; colors[0].
            RustSendLED(colors[0]);
        }

        private void RustSendLED(Color32 color)
        {
            if (state == ControllerState.Initial || state == ControllerState.Connecting) return;
            // LED commands are reliable now: at most 10 per second, and repeats of the same colour only once a second.
            float now = Time.unscaledTime;
            bool same = color.r == rustLastLed.r && color.g == rustLastLed.g && color.b == rustLastLed.b;
            if (now - rustLastLedTime < 0.1f || (same && now - rustLastLedTime < 1f)) return;
            rustLastLed = color;
            rustLastLedTime = now;
            rustSession.SetLedColor(color.r, color.g, color.b);
        }

        // ------------------------------------------------------------------ events from the server (main thread)

        private void RustOnEvent(YawEvent e)
        {
            switch (e.Kind)
            {
                case YawEventKind.Connected:
                    if (state != ControllerState.Connecting) break;
                    if (!rustGameStartHandled && e.State == YawVR.Proto.State.EmergencyStopped)
                    {
                        // Clear first and report Connected only afterwards: a StartDevice sent now could
                        // meet a stale "e-stopped" status, which would cancel it.
                        rustClearId = rustSession.ClearEmergencyStop();
                        if (rustClearId != 0)
                        {
                            Debug.LogWarning("[YawController] Game start: clearing the latched emergency stop");
                            rustClearPending = true;
                            rustClearAnswered = false;
                            rustClearDeadline = Time.unscaledTime + RustClearTimeout;
                            break;
                        }
                    }
                    RustFinishConnected();
                    break;

                case YawEventKind.ConnectFailed:
                    if (rustEverConnected) break; // reconnecting after a link loss: the session keeps trying
                    // The first connection failed: like the old SDK, back to Initial with the error.
                    Debug.LogWarning("[YawController] Connection to the Rust yawvr-server failed: " + e.Message + ". Check: is yawvr-rust running on the Pi, and does Rust Password match auth.password there? (A wrong password looks like a silent server.)");
                    rustSession.Disconnect(RustOwnDisconnect + "connect failed");
                    SetState(ControllerState.Initial);
                    RustFail(ref callBacks.connectingSuccess, ref callBacks.connectingError, e.Message);
                    break;

                case YawEventKind.Disconnected:
                    rustClearPending = false; // not resolved: the next connection tries again
                    // Our own Disconnect() calls are already handled where they were made.
                    if (state == ControllerState.Initial || (e.Message != null && e.Message.StartsWith(RustOwnDisconnect, StringComparison.Ordinal))) break;
                    onDisconnected?.Invoke();
                    if (ControllerDelegate != null) ControllerDelegate.DidDisconnectFrom(device);
                    RustFailPending("link lost: " + e.Message);
                    if (e.Permanent)
                    {
                        Debug.LogError("[YawController] Disconnected from the Rust yawvr-server: " + e.Message);
                        SetState(ControllerState.Initial);
                    }
                    else
                    {
                        rustWasStarted = state == ControllerState.Started || state == ControllerState.Starting || state == ControllerState.Stopping;
                        Debug.LogWarning("[YawController] Link to the Rust yawvr-server lost (" + e.Message + "): the rig parks, reconnecting...");
                        SetState(ControllerState.Connecting);
                    }
                    break;

                case YawEventKind.CommandAccepted:
                    if (e.CommandId != 0 && e.CommandId == rustStopId) rustStopAccepted = true;
                    if (rustClearPending && e.CommandId == rustClearId) rustClearAnswered = true;
                    break;

                case YawEventKind.CommandRejected:
                    Debug.LogWarning("[YawController] " + e.CommandName + " rejected by the Rust yawvr-server: " + e.Reason + " " + e.Message);
                    bool startCmd = e.CommandId == rustStartId || (e.CommandId == 0 && e.CommandName == "Start");
                    bool stopCmd = e.CommandId == rustStopId || (e.CommandId == 0 && e.CommandName == "Stop");
                    if (rustClearPending && e.CommandId == rustClearId)
                    {
                        // For example the magnet is still off: the rig stays stopped and Start will be refused.
                        Debug.LogError("[YawController] The emergency stop could not be cleared at game start. Fix the cause and restart the game.");
                        rustClearPending = false;
                        RustFinishConnected();
                    }
                    if (state == ControllerState.Starting && startCmd)
                    {
                        SetState(ControllerState.Connected);
                        RustFail(ref callBacks.startSuccess, ref callBacks.startError, e.Reason + " " + e.Message);
                    }
                    else if (state == ControllerState.Stopping && stopCmd)
                    {
                        SetState(rustSession.IsStreaming ? ControllerState.Started : ControllerState.Connected);
                        RustFail(ref callBacks.stopSuccess, ref callBacks.stopError, e.Reason + " " + e.Message);
                    }
                    break;

                case YawEventKind.StateChanged:
                    RustOnRigState(e.State);
                    break;

                case YawEventKind.DeviceDiscovered:
                    var d = e.Device;
                    if (d == null) break;
                    var found = new YawDevice(d.Address, 0, d.Port, d.Uuid, d.Name, d.Available ? DeviceStatus.Available : DeviceStatus.Reserved);
                    Debug.Log("[YawController] Found device: " + found.Name + " (Rust yawvr-server)");
                    DeviceDiscovered?.Invoke(found);
                    break;

                case YawEventKind.TelemetryUpdated:
                    var s = rustSession.Model.Snapshot;
                    if (device != null && s.HasActual)
                    {
                        device.ActualPosition.yaw = s.ActualYaw;
                        device.ActualPosition.pitch = s.ActualPitch;
                        device.ActualPosition.roll = s.ActualRoll;
                    }
                    if (s.ServerState != rustRigState) RustOnRigState(s.ServerState);
                    break;

                case YawEventKind.EmergencyStopUnconfirmed:
                    Debug.LogError("[YawController] EMERGENCY STOP NOT CONFIRMED by the server: " + e.Message);
                    break;
            }
        }

        private void RustFinishConnected()
        {
            rustGameStartHandled = true;
            bool reconnect = rustEverConnected;
            rustEverConnected = true;
            if (cancellation != null) Invoke(nameof(UpdateIMUOffset), 0.1f);
            foreach (Action a in OnConnectReceivers) a?.Invoke();
            onConnected?.Invoke();
            SetState(ControllerState.Connected);
            if (!reconnect)
            {
                Debug.Log("[YawController] Connected to " + (device != null ? device.Name : "?") + " (Rust yawvr-server)");
                RustSucceed(ref callBacks.connectingSuccess, ref callBacks.connectingError);
            }
            else
            {
                Debug.Log("[YawController] Reconnected to the Rust yawvr-server" + (rustWasStarted ? (rustRestartAfterReconnect ? ": restarting the motion" : ": motion stays off until StartDevice()") : ""));
                if (rustRestartAfterReconnect && rustWasStarted) StartDevice();
            }
            rustWasStarted = false;
        }

        /// <summary>Finishes Start/Stop when the rig gets there, notices motion stopping by itself, applies timeouts.</summary>
        private void RustCheckProgress()
        {
            if (rustClearPending)
            {
                if (state != ControllerState.Connecting) rustClearPending = false;
                else if (rustClearAnswered && rustSession.Model.Snapshot.ServerState != YawVR.Proto.State.EmergencyStopped)
                {
                    Debug.LogWarning("[YawController] Emergency stop cleared at game start");
                    rustClearPending = false;
                    RustFinishConnected();
                }
                else if (Time.unscaledTime > rustClearDeadline)
                {
                    Debug.LogError("[YawController] No answer to the emergency stop clear at game start: continuing");
                    rustClearPending = false;
                    RustFinishConnected();
                }
            }
            if (state == ControllerState.Starting && rustSession.IsStreaming)
            {
                SetState(ControllerState.Started);
                RustSucceed(ref callBacks.startSuccess, ref callBacks.startError);
            }
            // Stop is done when the server accepted it and the rig is no longer moving or parking
            // (the server accepts Start only from IDLE or PARKED).
            if (state == ControllerState.Stopping && rustStopAccepted
                && rustRigState != YawVR.Proto.State.Active && rustRigState != YawVR.Proto.State.Parking)
            {
                SetState(ControllerState.Connected);
                RustSucceed(ref callBacks.stopSuccess, ref callBacks.stopError);
            }
            // Motion stopped without StopDevice(): e-stop, fault, or the server parked after a link problem.
            if (state == ControllerState.Started && !rustSession.IsStreaming)
            {
                Debug.LogWarning("[YawController] The rig stopped by itself (state " + rustRigState + ")");
                SetState(ControllerState.Connected);
                if (ControllerDelegate != null) ControllerDelegate.DeviceStoppedFromApp();
            }
            if ((state == ControllerState.Starting || state == ControllerState.Stopping) && Time.unscaledTime > rustCommandDeadline)
            {
                bool starting = state == ControllerState.Starting;
                SetState(rustSession.IsStreaming ? ControllerState.Started : ControllerState.Connected);
                if (starting) RustFail(ref callBacks.startSuccess, ref callBacks.startError, "Command timeout");
                else RustFail(ref callBacks.stopSuccess, ref callBacks.stopError, "Command timeout");
            }
        }

        private void RustOnRigState(YawVR.Proto.State rig)
        {
            rustRigState = rig;
            if (device == null) return;
            // Same mapping as the old GET_STATE strings: simulation mode / parking / emergency mode / disabled.
            DeviceState ds = rig == YawVR.Proto.State.Active ? DeviceState.Started
                : rig == YawVR.Proto.State.Parking ? DeviceState.Parking
                : (rig == YawVR.Proto.State.EmergencyStopped || rig == YawVR.Proto.State.Fault) ? DeviceState.NoTracker
                : DeviceState.Stopped;
            if (device.State != ds)
            {
                onStateChanged?.Invoke(ds);
                Debug.Log($"[YawController] Device state changed to: {ds} (rig {rig})");
            }
            device.State = ds;
        }

        // ------------------------------------------------------------------ helpers

        private byte[] RustLoadPsk(out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(rustPassword))
            {
                error = "[YawController] No password for the Rust yawvr-server: fill in Rust Password (same as auth.password on the Pi)";
                Debug.LogError(error);
                return null;
            }
            return KeyDerivation.FromPassword(rustPassword);
        }

        private void RustFailPending(string reason)
        {
            RustFail(ref callBacks.startSuccess, ref callBacks.startError, reason);
            RustFail(ref callBacks.stopSuccess, ref callBacks.stopError, reason);
        }

        private static void RustSucceed(ref Action success, ref Action<string> error)
        {
            var cb = success;
            success = null;
            error = null;
            cb?.Invoke();
        }

        private static void RustFail(ref Action success, ref Action<string> error, string reason)
        {
            var cb = error;
            success = null;
            error = null;
            cb?.Invoke(reason);
        }

        /// <summary>Protocol log lines into the Unity console (callable from the network thread).</summary>
        private sealed class RustUnityLog : ILog
        {
            public void Write(LogLevel level, string message)
            {
                switch (level)
                {
                    case LogLevel.Info: Debug.Log(message); break;
                    case LogLevel.Warning: Debug.LogWarning(message); break;
                    default: Debug.LogError(level == LogLevel.Safety ? "[YAW SAFETY] " + message : message); break;
                }
            }
        }
    }
}
