using System;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using UnityEngine;
using System.Threading.Tasks;
using System.IO;

namespace YawVR
{
    public interface IYawTCPClientDelegate
    {
        void DidRecieveTCPMessage(byte[] data);
        void DidLostServerConnection();
    }

    public class YawTCPClient
    {
        private TcpClient tcpClient;
        public IYawTCPClientDelegate tcpDelegate;

        private CancellationTokenSource cts;
        private bool connected = false;

        public bool Connected => tcpClient != null && tcpClient.Connected && connected;
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private int connectionAttemptId = 0;

        public async void Initialize(string ip, int port, Action onConnectionSuccess, Action<string> onConnectionError)
        {
            int myAttempt = ++connectionAttemptId;
            CloseConnection();

            cts = new CancellationTokenSource();

            try
            {
                tcpClient = new TcpClient();
                IPAddress ipAddress = IPAddress.Parse(ip);
                
                await tcpClient.ConnectAsync(ipAddress, port);

                if (tcpClient.Connected)
                {
                    if (myAttempt != connectionAttemptId) return;
                    connected = true;
                    Debug.Log($"[YawTCPClient] Connected to: {ip}:{port}");

                    try
                    {
                        ActionBus.Instance.Add(() => onConnectionSuccess?.Invoke());
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[YawTCPClient] ActionBus unavailable: {e.Message}");
                    }

                    _ = ReadLoopAsync(cts.Token);
                }
                else
                {
                    HandleConnectionError("Unable to connect to TCP server.", onConnectionError);
                }
            }
            catch (Exception ex)
            {
                HandleConnectionError($"Connection failed: {ex.Message}", onConnectionError);
            }
        }

        public void StopConnecting()
        {
            cts?.Cancel();
            CloseConnection();
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            var buffer = new byte[4096];

            try
            {
                NetworkStream ns = tcpClient.GetStream();

                while (!token.IsCancellationRequested)
                {
                    var client = tcpClient;
                    if (client == null || !client.Connected) break;

                    int bytesRead = await ns.ReadAsync(buffer, 0, buffer.Length, token);
                    if (bytesRead > 0)
                    {
                        byte[] data = new byte[bytesRead];
                        Array.Copy(buffer, data, bytesRead);

                        ActionBus.Instance.Add(() =>
                        {
                            tcpDelegate?.DidRecieveTCPMessage(data);
                        });
                    }
                    else break;
                }
            }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                Debug.LogWarning($"[YawTCPClient] Read error: {ex.Message}");
            }
            finally
            {
                bool wasIntentional = token.IsCancellationRequested;
                CloseConnection();
                if (!wasIntentional)
                {
                    ActionBus.Instance?.Add(() => tcpDelegate?.DidLostServerConnection());
                }
            }
        }

        public async void BeginSend(byte[] data)
        {
            if (tcpClient == null || !tcpClient.Connected || data == null || data.Length == 0) return;

            await sendLock.WaitAsync();
            try
            {
                NetworkStream ns = tcpClient.GetStream();
                await ns.WriteAsync(data, 0, data.Length);
            }
            catch (Exception err)
            {
                Debug.LogError($"[YawTCPClient] Error sending data: {err.Message}");
            }
            finally
            {
                sendLock.Release();
            }
        }

        public void CloseConnection()
        {
            connected = false;

            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
                cts = null;
            }

            if (tcpClient != null)
            {
                try
                {
                    tcpClient.Close();
                }
                catch (Exception err)
                {
                    Debug.Log($"[YawTCPClient] Error closing client: {err.Message}");
                }
                finally
                {
                    tcpClient = null;
                }
            }
        }

        private void HandleConnectionError(string message, Action<string> onErrorCallback)
        {
            CloseConnection();
            ActionBus.Instance.Add(() =>
            {
                onErrorCallback?.Invoke(message);
            });
        }
    }
}