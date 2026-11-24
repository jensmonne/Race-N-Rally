using System.Collections;
using System.Net;
using UnityEngine;

namespace YawVR
{
    public class YawDeviceConnector : MonoBehaviour
    {
        [SerializeField] private ConnectType connectType;
        [SerializeField] private string debugIpAddress;
        [SerializeField] private bool startOnConnect = true;

        private YawController controller;
        private Coroutine routine;

        private void OnEnable()
        {
            controller = YawController.Instance;
            controller.DeviceDiscovered += OnDeviceDiscovered;
            controller.StateChanged += OnStateChanged;
            Begin();
        }

        private void OnDisable()
        {
            StopRoutine();
            if (controller == null) return;
            controller.DeviceDiscovered -= OnDeviceDiscovered;
            controller.StateChanged -= OnStateChanged;
        }

        private void Begin()
        {
            StopRoutine();
            if (controller.State != ControllerState.Initial) return;
            
            if (connectType == ConnectType.ConnectFirstFoundDevice) routine = StartCoroutine(DiscoveryRoutine());
            else if (connectType == ConnectType.DebugConnectToIp) routine = StartCoroutine(DebugConnectRoutine());
        }

        private void OnStateChanged(ControllerState state)
        {
            if (state == ControllerState.Initial) Begin();
            else StopRoutine();
        }

        private IEnumerator DiscoveryRoutine()
        {
            for (int i = 0; i < 3; i++)
            {
                controller.DiscoverDevices(50010);
                yield return new WaitForSeconds(0.2f);
            }

            while (true)
            {
                controller.DiscoverDevices(50010);
                yield return new WaitForSeconds(1f);
            }
        }

        private IEnumerator DebugConnectRoutine()
        {
            yield return new WaitForSeconds(0.5f);
            YawDevice device = new YawDevice(IPAddress.Parse(debugIpAddress), 50020, 50010, "001", "DEBUG", DeviceStatus.Available);
            Connect(device);
        }

        private void OnDeviceDiscovered(YawDevice device)
        {
            if (connectType != ConnectType.ConnectFirstFoundDevice) return;
            if (controller.State != ControllerState.Initial) return;
            if (device.Status != DeviceStatus.Available && device.Status != DeviceStatus.Unknown) return;

            StopRoutine();
            Connect(device);
        }

        private void Connect(YawDevice device)
        {
            controller.ConnectToDevice(device,
                () => { if (startOnConnect) controller.StartDevice(); },
                error => Debug.Log($"[YawDeviceConnector] connection error: {error}"));
        }

        private void StopRoutine()
        {
            if (routine == null) return;
            StopCoroutine(routine);
            routine = null;
        }
    }
}