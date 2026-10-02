namespace YawVR
{
    /// <summary>
    /// SDK Connection type
    /// </summary>
    public enum ConnectType
    {
        ConnectFirstFoundDevice,
        DebugConnectToIp,
        NoAutoConnect
    }

    /// <summary>
    /// YawDevice's status
    /// </summary>
    public enum DeviceStatus
    {
        Available,
        Reserved,
        Unknown
    }

    /// <summary>
    /// The controller's inner state
    /// </summary>
    public enum ControllerState
    {
        Initial,
        Connecting,
        Connected,
        Starting,
        Started,
        Stopping,
        Disconnecting
        // TODO: Add connectionlost state???
    }
}