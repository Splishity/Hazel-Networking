using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;


namespace Hazel.Udp
{
    /// <summary>
    /// Unity doesn't always get along with thread pools well, so this interface will hopefully suit that case better.
    /// Be very careful since this interface is likely unstable or actively changing
    /// </summary>
    /// <inheritdoc/>
    public class ThreadLimitedUnityUdpClientConnection : UdpConnection
    {
        private const int BufferSize = ushort.MaxValue;
        
        private Socket socket;

        private Thread receiveThread;
        private Thread sendThread;
        private bool isActive = true;

        private BlockingCollection<byte[]> sendQueue = new BlockingCollection<byte[]>();

        public ThreadLimitedUnityUdpClientConnection(IPEndPoint remoteEndPoint, IPMode ipMode = IPMode.IPv4)
            : base()
        {
            this.EndPoint = remoteEndPoint;
            this.RemoteEndPoint = remoteEndPoint;
            this.IPMode = ipMode;

            this.socket = CreateSocket(ipMode);
            this.socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
            this.socket.Blocking = false;

            this.receiveThread = new Thread(ReceiveLoop);
            this.sendThread = new Thread(SendLoop);
        }
        
        ~ThreadLimitedUnityUdpClientConnection()
        {
            this.Dispose(false);
        }

        public void FixedUpdate()
        {
            base.ManageReliablePackets();
        }

        private void ReceiveLoop()
        {
            EndPoint remoteEP = new IPEndPoint(this.EndPoint.Address, this.EndPoint.Port);
            while (this.isActive)
            {
                if (this.socket.Poll(Timeout.Infinite, SelectMode.SelectRead))
                {
                    MessageReader message = MessageReader.GetSized(BufferSize);
                    try
                    {
                        message.Length = socket.ReceiveFrom(message.Buffer, 0, message.Buffer.Length, SocketFlags.None, ref remoteEP);
                    }
                    catch (Exception)
                    {
                        message.Recycle();
                        return;
                    }

                    if (message.Length == 0)
                    {
                        message.Recycle();
                        DisconnectInternal(HazelInternalErrors.ReceivedZeroBytes, "Received 0 bytes");
                        return;
                    }

                    HandleReceive(message, message.Length);
                }
            }
        }

        private void SendLoop()
        {
            while (this.isActive)
            {
                try
                {
                    while (this.sendQueue.TryTake(out var msg, -1))
                    {
                        this.socket.SendTo(msg, 0, msg.Length, SocketFlags.None, this.RemoteEndPoint);
                    }
                }
                catch (SocketException)
                {
                    Thread.Sleep(100);
                }
                catch
                {
                    return;
                }
            }
        }

        /// <inheritdoc />
        protected override void WriteBytesToConnection(byte[] bytes)
        {
            try
            {
                this.sendQueue.TryAdd(bytes);
            }
            catch
            {

            }
        }

        public override void Connect(byte[] bytes = null, int timeout = 5000)
        {
            throw new NotImplementedException("Use ConnectAsync and check State != ConnectionState.Connecting instead.");
        }

        /// <inheritdoc />
        public override void ConnectAsync(byte[] bytes = null)
        {
            this.State = ConnectionState.Connecting;

            try
            {
                if (IPMode == IPMode.IPv4)
                    socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                else
                    socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            }
            catch (SocketException e)
            {
                this.State = ConnectionState.NotConnected;
                throw new HazelException("A SocketException occurred while binding to the port.", e);
            }

            try
            {
                this.sendThread.Start();
                this.receiveThread.Start();
            }
            catch (ObjectDisposedException)
            {
                // If the socket's been disposed then we can just end there but make sure we're in NotConnected state.
                // If we end up here I'm really lost...
                this.State = ConnectionState.NotConnected;
                return;
            }
            catch (SocketException e)
            {
                Dispose();
                throw new HazelException("A SocketException occurred while initiating a receive operation.", e);
            }

            // Write bytes to the server to tell it hi (and to punch a hole in our NAT, if present)
            // When acknowledged set the state to connected
            SendHello(bytes, () =>
            {
                this.State = ConnectionState.Connected;
            });

            this.InitializeKeepAliveTimer();
        }

        /// <summary>
        ///     Sends a disconnect message to the end point.
        ///     You may include optional disconnect data. The SendOption must be unreliable.
        /// </summary>
        protected override bool SendDisconnect(MessageWriter data = null)
        {
            lock (this)
            {
                if (this._state == ConnectionState.NotConnected) return false;
                this._state = ConnectionState.NotConnected;
            }

            var bytes = EmptyDisconnectBytes;
            if (data != null && data.Length > 0)
            {
                if (data.SendOption != SendOption.None) throw new ArgumentException("Disconnect messages can only be unreliable.");

                bytes = data.ToByteArray(true);
                bytes[0] = (byte)UdpSendOption.Disconnect;
            }

            try
            {
                socket.SendTo(
                    bytes,
                    0,
                    bytes.Length,
                    SocketFlags.None,
                    RemoteEndPoint);
            }
            catch { }

            return true;
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SendDisconnect();
            }

            this.isActive = false;
            this.sendQueue.CompleteAdding();

            try { this.socket.Shutdown(SocketShutdown.Both); } catch { }
            try { this.socket.Close(); } catch { }
            try { this.socket.Dispose(); } catch { }

            base.Dispose(disposing);
        }
    }
}