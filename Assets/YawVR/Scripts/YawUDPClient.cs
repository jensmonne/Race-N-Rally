using System;
using System.Net.Sockets;
using System.Text;
using System.Net;
using UnityEngine;
using System.Threading;
using System.Threading.Tasks;

namespace YawVR
{
    public interface IYawUDPClientDelegate
    {
        void DidRecieveUDPMessage(string message, IPEndPoint remoteEndPoint);
    }

    public class YawUDPClient
    {
        private readonly int listeningPort;
        private UdpClient udpClient;
        private IPEndPoint remoteEndPoint;
        public IYawUDPClientDelegate udpDelegate;

        private CancellationTokenSource cts;

        public YawUDPClient(int listeningPort)
        {
            this.listeningPort = listeningPort;
            InitializeUdpClient();
        }

        private void InitializeUdpClient()
        {
            try
            {
                udpClient = new UdpClient(listeningPort)
                {
                    EnableBroadcast = true
                };
            }
            catch (Exception err)
            {
                Debug.LogError($"[YawUDPClient] Error initializing UDP socket on port {listeningPort}: {err.Message}");
            }
        }

        public void SetRemoteEndPoint(IPAddress ipAddress, int port)
        {
            remoteEndPoint = new IPEndPoint(ipAddress, port);
        }

        public void StartListening()
        {
            if (udpClient == null)
            {
                InitializeUdpClient();
            }

            cts?.Cancel();
            cts = new CancellationTokenSource();

            _ = ReceiveLoopAsync(cts.Token);
        }

        public void StopListening()
        {
            cts?.Cancel();
            cts?.Dispose();
            cts = null;

            if (udpClient != null)
            {
                try
                {
                    udpClient.Close();
                }
                catch (Exception err)
                {
                    Debug.LogWarning($"[YawUDPClient] Error closing UDP client: {err.Message}");
                }
                finally
                {
                    udpClient = null;
                }
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && udpClient != null)
            {
                try
                {
                    UdpReceiveResult result = await udpClient.ReceiveAsync();

                    byte[] bytes = result.Buffer;
                    IPEndPoint remoteEP = result.RemoteEndPoint;
                    string message = Encoding.ASCII.GetString(bytes);

                    if (!message.Contains("YAW_CALLING"))
                    {
                        ActionBus.Instance.Add(() =>
                        {
                            udpDelegate?.DidRecieveUDPMessage(message, remoteEP);
                        });
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (NullReferenceException) when (token.IsCancellationRequested || udpClient == null)
                {
                    break;
                }
                catch (SocketException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception err)
                {
                    Debug.LogError($"[YawUDPClient] Error receiving UDP packet: {err.Message}");
                }
            }
        }

        public void SendBroadcast(int port, byte[] data)
        {
            if (udpClient == null || data == null || data.Length == 0) return;

            try
            {
                IPEndPoint broadcastEndPoint = new(IPAddress.Broadcast, port);
                udpClient.Send(data, data.Length, broadcastEndPoint);
            }
            catch (Exception err)
            {
                Debug.LogError($"[YawUDPClient] Error sending broadcast: {err.Message}");
            }
        }

        public void Send(byte[] data)
        {
            if (udpClient == null || remoteEndPoint == null || data == null || data.Length == 0) return;

            try
            {
                udpClient.Send(data, data.Length, remoteEndPoint);
            }
            catch (Exception err)
            {
                Debug.LogError($"[YawUDPClient] Error sending UDP data: {err.Message}");
            }
        }
    }
}