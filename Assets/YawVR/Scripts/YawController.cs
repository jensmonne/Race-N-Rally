using System.Collections;
using UnityEngine;
using System;
using System.Net;
using System.Threading;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using System.Collections.Generic;
using UnityEngine.Events;

namespace YawVR
{
    /// <summary>
    /// OVector is a Vector3D with yaw,pitch,roll named variables.
    /// </summary>
    [Serializable]
    public struct OVector
    {
        public float yaw, pitch, roll;

        public OVector(float yaw, float pitch, float roll)
        {
            this.yaw = yaw;
            this.pitch = pitch;
            this.roll = roll;
        }
    }

    [Serializable]
    public struct Parameters
    {
        public byte Power, RollLimit, PitchLimitF, PitchLimitB;
        public UInt32 YawLimit;
        public bool hasYawLimit;
    }

    /// <summary>
    /// Buzzer info
    /// Amplitudes and hz
    /// </summary>
    [Serializable]
    public class Buzzer
    {
        public bool isOn;
        public int right_amp, center_amp, left_amp, hz;

        public void SetBuzzerAmps(int right, int center, int left)
        {
            right_amp = right;
            center_amp = center;
            left_amp = left;
        }

        public void SetHz(int buzzerHz)
        {
            hz = buzzerHz;
        }

        public void SetOn(bool b)
        {
            isOn = b;
        }
    }

    /// <summary>
    /// Game Limits
    /// The limits are applied to the YawVR Tracker
    /// </summary>
    [Serializable]
    public class Limits
    {
        public float yaw = -1, pitch = -1, roll = -1;

        public Limits(float yaw, float pitch, float roll)
        {
            this.yaw = yaw;
            this.pitch = pitch;
            this.roll = roll;
        }
    }

    /// <summary>
    /// The script, that needs to receive notifications, is need to inherited from YawControllerDelegate
    /// </summary>
    public interface IYawControllerDelegate
    {
        void ControllerStateChanged(ControllerState state);

        /// <summary>
        /// A found is device on network
        /// </summary>
        void DidFoundDevice(YawDevice device);

        /// <summary>
        /// Disconnected from device
        /// </summary>
        void DidDisconnectFrom(YawDevice device);

        void DeviceStoppedFromApp(); // will be called when device stopped from app
        void DeviceStartedFromApp(); // will be called when device started from app
    }

    public interface IYawControllerType
    {
        //Properties 
        ControllerState State { get; }
        YawDevice Device { get; }
        IYawControllerDelegate ControllerDelegate { get; set; }

        //Motion related properties
        Vector3 RotationMultiplier { get; }

        Limits Limits { get; }

        Buzzer Buzzer { get; }

        //Game related setters
        void SetGameName(string gameName);

        //Methods triggering delegate functions
        void DiscoverDevices(int onPort);
        void SetTiltLimits(float yawLimit, float pitchLimit, float rollLimit);

        //Methods with success/error action callbacks
        void ConnectToDevice(YawDevice yawDevice, Action onSuccess, Action<string> onError);
        void StartDevice(Action onSuccess, Action<string> onError);
        void StopDevice(bool park, Action onSuccess, Action<string> onError);
        void DisconnectFromDevice(Action onSuccess, Action<string> onError);
        void CalibrateDevice(bool allAxis);

        void SetRotationMultiplier(float yaw, float pitch, float roll);
    }

    [Serializable]
    public class StateChangeEvent : UnityEvent<DeviceState> { }

    public class YawController : MonoBehaviour, IYawControllerType, IYawTCPClientDelegate, IYawUDPClientDelegate
    {
        private static YawController instance;
        public static List<Action> OnConnectReceivers = new();

        private YawTCPClient tcpCLient;
        private YawUDPClient udpClient;

        [SerializeField] private YawDevice device = null;
        private ControllerState state = ControllerState.Initial;
        private int discoveryPort = 0;

        private CallBacks callBacks = new();
        private CallbackTimeouts callbackTimeouts = new();

        private Orientation orientation;
        private YawTracker yawTracker;

        #region PROPERTIES
        public static YawController Instance
        {
            get
            {
                if (instance == null) throw new Exception("[YawController] Please drag YawController prefab into your scene.");
                return instance;
            }
        }

        public YawTracker TrackerObject => yawTracker;
        public ControllerState State => state;
        public YawDevice Device => device;
        public IYawControllerDelegate ControllerDelegate { get; set; }
        public Vector3 RotationMultiplier => rotationMultiplier;
        public Limits Limits => gameLimits;
        public Buzzer Buzzer => buzzer;
        #endregion

        [SerializeField] private Transform referenceTransform; // we will copy this objects rotation, and send it to the sim
        [SerializeField] private string gameName; // name of the game
        [SerializeField] private ConnectType connectType; // connect type, for debug purposes
        [SerializeField] private string debug_ipAddress; // ip to connect in debug mode
        [SerializeField] private int udpClientPort;

        private OVector referenceRotation; // the rotation of the YAWTracker

        [SerializeField] private Vector3 rotationMultiplier = new(1, 1, 1); // multiplier for YAWTracker
        [SerializeField] private Limits gameLimits;
        [SerializeField] private Buzzer buzzer;
        [SerializeField] private byte smartPlug;

        [Header("Camera Cancellation")]
        [SerializeField] private MotionCompensation cancellation;

        [Header("Events")]
        [SerializeField] private UnityEvent onConnected;
        [SerializeField] private UnityEvent onDisconnected;
        [SerializeField] private StateChangeEvent onStateChanged;

        private void Awake()
        {
            if (instance == null)
            {
                instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else if (instance != this)
            {
                DestroyImmediate(gameObject);
                return;
            }

            orientation = GetComponentInChildren<Orientation>();
            yawTracker = GetComponentInChildren<YawTracker>();

            tcpCLient = new YawTCPClient
            {
                tcpDelegate = this
            };

            udpClient = new YawUDPClient(udpClientPort)
            {
                udpDelegate = this
            };

            udpClient.StartListening();

            Debug.Log("[YawController] Initialized");
        }

        private void Start()
        {
            if (connectType == ConnectType.CONNECT_FIRST_FOUND_DEVICE) AutoConnectFirst();

            if (connectType == ConnectType.DEBUG_CONNECT_TO_IP)
            {
                ConnectToDevice(new YawDevice(IPAddress.Parse(debug_ipAddress), 50020, 50010, "001", "DEBUG", DeviceStatus.Available), null, null);
            }
        }

        private void FixedUpdate()
        {
            referenceRotation.pitch = orientation.pitch;
            referenceRotation.yaw = orientation.yaw;
            referenceRotation.roll = orientation.roll;

            if (state == ControllerState.Started || state == ControllerState.Connected)
            {
                SendMotionData();
            }
        }

        private void OnDestroy()
        {
            if (instance != this) return;

            if (state != ControllerState.Initial && state != ControllerState.Disconnecting && device != null)
            {
                DisconnectFromDevice(null, null);
            }

            tcpCLient?.CloseConnection();
            udpClient?.StopListening();

            instance = null;
        }

        private void OnApplicationQuit()
        {
            if (state != ControllerState.Initial && state != ControllerState.Disconnecting && device != null)
            {
                DisconnectFromDevice(null, null);
            }

            tcpCLient?.CloseConnection();
            udpClient?.StopListening();
        }

        public void SetGameName(string gameName) => this.gameName = gameName;

        public void DiscoverDevices(int onPort)
        {
            discoveryPort = onPort;
            udpClient.SendBroadcast(onPort, Commands.DEVICE_DISCOVERY);
        }

        public void ConnectToDevice(YawDevice yawDevice, Action onSuccess, Action<string> onError)
        {
            if (state == ControllerState.Initial)
            {
                SetState(ControllerState.Connecting);

                callbackTimeouts.tcpConnectionAttemptTimeout = StartCoroutine(ResponseTimeout((error) =>
                {
                    onError?.Invoke("Failed to create TCP connection");
                    SetState(ControllerState.Initial);
                    tcpCLient.StopConnecting();
                }));

                tcpCLient.Initialize(yawDevice.IPAddress.ToString(), yawDevice.TCPPort,
                    () =>
                    {
                        StopCoroutineSafe(ref callbackTimeouts.tcpConnectionAttemptTimeout);
                        device = yawDevice;

                        callBacks.connectingError = onError;
                        callBacks.connectingSuccess = onSuccess;

                        callbackTimeouts.connectingTimeout = StartCoroutine(ResponseTimeout((error) =>
                        {
                            onError?.Invoke(error);
                            SetState(ControllerState.Initial);
                        }));

                        tcpCLient.BeginSend(Commands.CHECK_IN(udpClientPort, gameName));
                        StartCoroutine(DeviceHeartbeat());
                    },
                    (error) =>
                    {
                        //Could not connect to tcp server
                        //Stop tcp connection timeout
                        StopCoroutineSafe(ref callbackTimeouts.tcpConnectionAttemptTimeout);
                        onError?.Invoke(error);
                        SetState(ControllerState.Initial);
                    }
                );
            }
            else
            {
                DisconnectFromDevice(
                    () => ConnectToDevice(yawDevice, onSuccess, onError),
                    (error) =>
                    {
                        onError?.Invoke(error);
                        ConnectToDevice(yawDevice, onSuccess, onError);
                    }
                );
            }
        }

        public void StartDevice(Action onSuccess = null, Action<string> onError = null)
        {
            if (state == ControllerState.Connected)
            {
                callBacks.startSuccess = onSuccess;
                callBacks.startError = onError;
                callbackTimeouts.startTimeout = StartCoroutine(ResponseTimeout(onError));
                SetState(ControllerState.Starting);
                tcpCLient.BeginSend(Commands.START);
            }
            else onError?.Invoke("Attempted to start device when device has not been in connected ready state");
        }

        public void StopDevice(bool park, Action onSuccess = null, Action<string> onError = null)
        {
            if (state == ControllerState.Started)
            {
                callBacks.stopSuccess = onSuccess;
                callBacks.stopError = onError;
                callbackTimeouts.stopTimeout = StartCoroutine(ResponseTimeout(onError));
                SetState(ControllerState.Stopping);
                tcpCLient.BeginSend(new byte[] { Commands.STOP, (byte)(park ? 1 : 0) });
            }
            else onError?.Invoke("Attempted to stop simulator when simulator had not been in started state");
        }

        public void CalibrateDevice(bool allAxis)
        {
            if (state == ControllerState.Connected)
            {
                tcpCLient.BeginSend(new byte[2] { Commands.CALIBRATE[0], (byte)(allAxis ? 1 : 0) });
            }
        }

        public void DisconnectFromDevice(Action onSuccess, Action<string> onError)
        {
            if (state != ControllerState.Initial)
            {
                callBacks.exitSuccess = onSuccess;
                callBacks.exitError = onError;

                callbackTimeouts.exitTimeout = StartCoroutine(ResponseTimeout((error) =>
                {
                    SetState(ControllerState.Initial);
                    onError?.Invoke(error);
                }));

                tcpCLient.BeginSend(Commands.EXIT);
                SetState(ControllerState.Disconnecting);
                onDisconnected?.Invoke();
            }
            else onError?.Invoke("Attempted to disconnect when no device was connected");
        }

        public void DidRecieveUDPMessage(string message, IPEndPoint remoteEndPoint)
        {
            //MatchCollection regular = Regex.Matches(message, @"(?:S?([YPR]|U))\[(-?[0-9]+(?:\.[0-9]+))\]");

            if (message.Contains("Y[") || message.Contains("P[") || message.Contains("R["))
            {
                ExtractValue(message, "Y[", ref device.ActualPosition.yaw);
                ExtractValue(message, "P[", ref device.ActualPosition.pitch);
                ExtractValue(message, "R[", ref device.ActualPosition.roll);
                
                if (ExtractValue(message, "U[", ref device.batteryVoltage))
                {
                    device.batteryPercent = Mathf.InverseLerp(2.8f, 4.2f, device.batteryVoltage);
                }
            }

            if (message.Contains("YAWDEVICE"))
            {
                var messageParts = message.Split(';');
                if (messageParts.Length >= 5 && int.TryParse(messageParts[3], out int tcp))
                {
                    DeviceStatus status = messageParts[4] == "AVAILABLE" ? DeviceStatus.Available : DeviceStatus.Reserved;
                    var yawDevice = new YawDevice(remoteEndPoint.Address, tcp, discoveryPort, messageParts[1], messageParts[2], status);
                    ControllerDelegate?.DidFoundDevice(yawDevice);
                }
            }
        }

        private bool ExtractValue(string msg, string key, ref float result)
        {
            int startIdx = msg.IndexOf(key);
            if (startIdx == -1) return false;
            
            startIdx += 2;
            int endIdx = msg.IndexOf(']', startIdx);
            
            if (endIdx != -1)
            {
                string valStr = msg.Substring(startIdx, endIdx - startIdx);
                return float.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            }
            return false;
        }

        public void DidRecieveTCPMessage(byte[] data)
        {
            if (data == null || data.Length == 0) return;

            byte commandId = data[0];
            switch (commandId)
            {
                case CommandIds.CHECK_IN_ANS:
                    Invoke(nameof(UpdateIMUOffset), 0.1f);
                    StopCoroutineSafe(ref callbackTimeouts.connectingTimeout);

                    if (state == ControllerState.Connecting)
                    {
                        string message = Encoding.ASCII.GetString(data, 1, data.Length - 1);
                        if (message.Contains("AVAILABLE"))
                        {
                            foreach (Action a in OnConnectReceivers) a?.Invoke();
                            onConnected?.Invoke();

                            udpClient.SetRemoteEndPoint(device.IPAddress, device.UDPPort);
                            SetState(ControllerState.Connected);

                            callBacks.connectingSuccess?.Invoke();
                            ClearCallbacks(ref callBacks.connectingSuccess, ref callBacks.connectingError);
                        }
                        else
                        {
                            var messageParts = message.Split(';');
                            if (messageParts.Length != 3) return;

                            SetState(ControllerState.Initial);
                            callBacks.connectingError?.Invoke($"Device is in use from: {messageParts[2]} with game: {messageParts[1]}");
                            ClearCallbacks(ref callBacks.connectingSuccess, ref callBacks.connectingError);
                        }
                    }
                    break;

                case CommandIds.START:
                    //Stop timeout
                    if (callbackTimeouts.startTimeout != null)
                    {
                        StopCoroutine(callbackTimeouts.startTimeout);
                        callbackTimeouts.startTimeout = null;
                    }
                    if (state == ControllerState.Starting)
                    {
                        //Set state to started
                        SetState(ControllerState.Started);
                        //Call success callback
                        if (callBacks.startSuccess != null)
                        {
                            callBacks.startSuccess();
                            callBacks.startSuccess = null;
                            callBacks.startError = null;
                        }
                    }
                    else
                    {
                        SetState(ControllerState.Started);
                        if (ControllerDelegate != null)
                        {
                            ControllerDelegate.DeviceStartedFromApp();
                        }
                    }
                    break;

                case CommandIds.STOP:
                    //Stop timeout
                    if (callbackTimeouts.stopTimeout != null)
                    {
                        StopCoroutine(callbackTimeouts.stopTimeout);
                        callbackTimeouts.stopTimeout = null;
                    }

                    if (state != ControllerState.Initial && state != ControllerState.Disconnecting)
                    {
                        //Set state back to connected
                        SetState(ControllerState.Connected);
                        //Call success callback
                        if (callBacks.stopSuccess != null)
                        {
                            callBacks.stopSuccess();
                            callBacks.stopSuccess = null;
                            callBacks.stopError = null;
                        }
                        else
                        {
                            SetState(ControllerState.Connected);
                            if (ControllerDelegate != null) ControllerDelegate.DeviceStoppedFromApp();
                        }
                    }
                    break;

                case CommandIds.EXIT:
                    //Stop timeout
                    if (callbackTimeouts.exitTimeout != null)
                    {
                        StopCoroutine(callbackTimeouts.exitTimeout);
                        callbackTimeouts.exitTimeout = null;
                    }
                    //Whenever we got an exit command from connected simulator, we close connection, not only if we invoked that
                    SetState(ControllerState.Initial);
                    //Call success callback - if we have one
                    if (callBacks.exitSuccess != null)
                    {
                        callBacks.exitSuccess();
                        callBacks.exitSuccess = null;
                        callBacks.exitError = null;
                    }
                    break;

                case CommandIds.SET_POWER:
                    //  byte[] buffer = new byte[34 + 34];
                    if (data.Length > 5)
                    {
                        Console.WriteLine("GETALL_PARAMS response " + data.Length);
                        device.deviceParams.Power = data[4];
                        device.deviceParams.PitchLimitF = data[13];
                        device.deviceParams.PitchLimitB = data[9];
                        device.deviceParams.RollLimit = data[17];
                        device.deviceParams.YawLimit = Helpers.ReadInt(data, 19, false);
                        device.deviceParams.hasYawLimit = data[24] == 1;
                    }
                    else
                    {
                        device.deviceParams.Power = data[4];
                    }
                    break;

                case CommandIds.GET_STATE:
                    string statestring = Encoding.ASCII.GetString(data, 2, data.Length - 2).Trim();
                    DeviceState newState = DeviceState.STOPPED;
                    switch (statestring)
                    {
                        case "disabled":
                            newState = DeviceState.STOPPED;
                            break;
                        case "simulation mode":
                            newState = DeviceState.STARTED;
                            break;
                        case "emergency mode":
                            newState = DeviceState.NOTRACKER;
                            break;
                        case "parking":
                            newState = DeviceState.PARKING;
                            break;
                    }
                    if (device.State != newState) onStateChanged.Invoke(newState);
                    this.device.State = newState;
                    break;

                case CommandIds.GET_TEMPS:
                    device.temps[0] = data[1];
                    device.temps[1] = data[2];
                    device.temps[2] = data[3];
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// Lost server connection. The controllerDelegates corresponding function's will be called
        /// </summary>
        public void DidLostServerConnection()
        {
            onDisconnected.Invoke();
            Debug.Log("TCP Client have disconnected");
            if (ControllerDelegate != null)
            {
                ControllerDelegate.DidDisconnectFrom(device);
            }
            SetState(ControllerState.Initial);
        }

        /// <summary>
        /// Set rotation limits for the YawTracker
        /// </summary>     
        public void SetTiltLimits(float yawLimit, float pitchLimit, float rollLimit)
        {
            gameLimits.yaw = yawLimit;
            gameLimits.pitch = pitchLimit;
            gameLimits.roll = rollLimit;
        }

        /// <summary>
        /// Set rotation multiplier for the YawTracker
        /// </summary>
        public void SetRotationMultiplier(float yaw, float pitch, float roll)
        {
            rotationMultiplier.x = pitch;
            rotationMultiplier.y = yaw;
            rotationMultiplier.z = roll;
        }

        private void SendMotionData()
        {
            if (device == null) return;

            float yaw = 0, pitch = 0, roll = 0;

            yaw = SignedForm(referenceRotation.yaw);
            pitch = SignedForm(referenceRotation.pitch);
            roll = SignedForm(referenceRotation.roll);

            SendRotation(new OVector(yaw, pitch, roll));
        }

        //MARK: - UDP command sender functions

        /// <summary>
        /// Send gamedata to the device
        /// </summary>
        private void SendRotation(OVector rotation)
        {
            udpClient.Send(Commands.MOTION_DATA(rotation.yaw, rotation.pitch, rotation.roll, buzzer, smartPlug));
        }

        public void SendLED(Color32[] colors)
        {
            if (colors.Length != 129) return;

            udpClient.Send(Commands.UDP_LED_CMD(colors));
        }

        public void SendLED(Color32 color)
        {
            udpClient.Send(Commands.UDP_LED_CMD(color));
        }

        /// <summary>
        /// Mark the current IMU data as origin for the Camera rotation cancellation
        /// </summary>
        public void UpdateIMUOffset()
        {
            cancellation.UpdateOffset();
        }

        //MARK: - Helper functions

        /// <summary>
        /// Sets the SDK's inner state
        /// </summary>
        private void SetState(ControllerState newState)
        {
            state = newState;
            //   Debug.Log("state changed to " + state);
            if (newState == ControllerState.Initial)
            {
                device = null;
                if (tcpCLient.Connected)
                {
                    tcpCLient.CloseConnection();
                }
            }
            if (ControllerDelegate != null)
            {
                ControllerDelegate.ControllerStateChanged(newState);
            }
        }

        private IEnumerator ResponseTimeout(Action<string> onError)
        {
            if (onError == null) yield break;
            yield return new WaitForSeconds(10f);
            onError("Command timeout");
        }

        private void StopCoroutineSafe(ref Coroutine routine)
        {
            if (routine != null)
            {
                StopCoroutine(routine);
                routine = null;
            }
        }

        private void ClearCallbacks(ref Action success, ref Action<string> error)
        {
            success = null;
            error = null;
        }

        private float SignedForm(float angle)
        {
            return angle >= 180 ? angle - 360 : angle;
        }

        private float UnsignedForm(float angle)
        {
            return angle < 0 ? 360 + angle : angle;
        }

        //MARK: - Helper structs
        private struct CallBacks
        {
            public Action connectingSuccess;
            public Action<string> connectingError;
            public Action startSuccess;
            public Action<string> startError;
            public Action stopSuccess;
            public Action<string> stopError;
            public Action exitSuccess;
            public Action<string> exitError;
            //public Action deviceStoppedFromApp;
        }

        private struct CallbackTimeouts
        {
            public Coroutine connectingTimeout;
            public Coroutine startTimeout;
            public Coroutine stopTimeout;
            public Coroutine exitTimeout;
            public Coroutine tcpConnectionAttemptTimeout;
        }

        #region AutoConnect
        private void AutoConnectFirst()
        {
            Debug.Log("-----------------------------DISCOVER---------------------------");
            // starting a repeating call to YawController.Instance().DiscoverDevices(udpPort) with the help of a coroutine - calling continuously because udp packet may be lost
            StartCoroutine(DeviceDiscoveryCoroutine());
            // We receive device in the DidFoundDevice(YawDevice) method
        }

        private IEnumerator DeviceDiscoveryCoroutine()
        {
            while (state == ControllerState.Initial)
            {
                DiscoverDevices(50010);

                yield return new WaitForSeconds(1);
            }
        }

        private IEnumerator DeviceHeartbeat()
        {
            WaitForSeconds wait = new WaitForSeconds(1f);
            yield return wait;
            tcpCLient.BeginSend(new byte[] { CommandIds.GET_ALL_APP_PARAMS });
            while (tcpCLient.Connected)
            {
                tcpCLient.BeginSend(new byte[] { CommandIds.GET_STATE });
                tcpCLient.BeginSend(new byte[] { CommandIds.GET_TEMPS });
                yield return wait;
            }
        }

        // YawControllerDelegate functions
        private void DidFoundDevice(YawDevice device)
        {
            //    Debug.Log("Did found device: " + device.Name);
            if (YawController.Instance.State == ControllerState.Initial && (device.Status == DeviceStatus.Available || device.Status == DeviceStatus.Unknown))
            {
                Debug.Log("-----------------------------CONNECT TO A DEVICE---------------------------");
                YawController.Instance.ConnectToDevice(device, () =>
                {
                    Debug.Log("YAWCONTROLLER: connected");
                }, (error) => { Debug.Log("kapcsolat error"); });
            }
        }
        #endregion
    }
}