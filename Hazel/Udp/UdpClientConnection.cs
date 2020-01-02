using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;


namespace Hazel.Udp
{
    /// <summary>
    /// Represents a client's connection to a server that uses the UDP protocol.
    /// </summary>
    /// <remarks>
    /// Usage:
    /// 1. Create
    /// 2. Connect
    /// 3. Send (Recycle MessageWriters)
    /// 4. Receive (Recycle MessageReaders)
    /// 5a. Disconnect locally (Optional, sends message synchronously to remote)
    /// 5b. Remote sends disconnect (Don't dispose in callback)
    /// 6. Dispose (Doesn't send message to remote);
    /// </remarks>
    /// <inheritdoc/>
    public class UdpClientConnection : UdpConnection
    {
        private Socket socket;
        private Action<string> logger;

        private Thread receiveThread;
        private Thread sendThread;
        private HazelThreadPool dispatchThreads;

        private bool disposed;

        private BlockingCollection<MessageReader> receiveQueue = new BlockingCollection<MessageReader>();
        private BlockingCollection<Tuple<byte[], int>> sendQueue = new BlockingCollection<Tuple<byte[], int>>();

        /// <summary>
        ///     Creates a new UdpClientConnection.
        /// </summary>
        /// <param name="remoteEndPoint">A <see cref="NetworkEndPoint"/> to connect to.</param>
        public UdpClientConnection(int numThreads, IPEndPoint remoteEndPoint, IPMode ipMode = IPMode.IPv4, Action<string> logger = null)
            : base()
        {
            this.RemoteEndPoint = remoteEndPoint;
            this.IPMode = ipMode;
            this.logger = logger;

            this.socket = CreateSocket(ipMode);
            this.socket.Blocking = false;

            this.sendThread = new Thread(this.SendLoop);
            this.sendThread.Name = "SendThread";
            this.receiveThread = new Thread(this.ReceiveLoop);
            this.receiveThread.Name = "ReceiveThread";
            this.dispatchThreads = new HazelThreadPool(numThreads, this.PooledReadCallback);
        }

        ~UdpClientConnection()
        {
            this.Dispose(false);
        }

        public void FixedUpdate()
        {
            base.ManageReliablePackets();
        }

        private void ReceiveLoop()
        {
            while (!this.disposed)
            {
                if (socket.Poll(-1, SelectMode.SelectRead))
                {
                    MessageReader msg = MessageReader.GetSized(ushort.MaxValue);

                    try
                    {
                        msg.Offset = 0;
                        msg.Length = socket.Receive(msg.Buffer, 0, msg.Buffer.Length, SocketFlags.None);
                    }
                    catch (SocketException ex)
                    {
                        msg.Recycle();
                        Disconnect("Could not read data as a SocketException occurred: " + ex.Message);
                        break;
                    }
                    catch (Exception e)
                    {
                        this.logger?.Invoke("ClientException in ReadLoop: " + e.Message);
                        msg.Recycle();
                        break;
                    }

                    try
                    {
                        receiveQueue.Add(msg);
                    }
                    catch { break; }
                }
                else
                {
                    Disconnect("Socket.Poll returned false.");
                    break;
                }
            }
        }

        private void SendLoop()
        {
            while (!this.disposed)
            {
                Tuple<byte[], int> bytesAndLength;
                try
                {
                    bytesAndLength = sendQueue.Take();
                }
                catch { break; }

                try
                {
                    socket.SendTo(bytesAndLength.Item1, 0, bytesAndLength.Item2, SocketFlags.None, this.RemoteEndPoint);
                }
                catch (SocketException ex)
                {
                    Disconnect("Could not send data as a SocketException occurred: " + ex.Message);
                    break;
                }
                catch (Exception e)
                {
                    this.logger?.Invoke("ClientException in SendLoop: " + e.Message);
                    break;
                }
            }
        }

        /// <inheritdoc />
        protected override void WriteBytesToConnection(byte[] bytes, int length)
        {
            try
            {
                sendQueue.Add(new Tuple<byte[], int>(bytes, length));
            }
            catch { }
        }

        public void Connect(byte[] bytes = null, int timeout = 5000)
        {
            this.ConnectAsync(bytes, timeout);

            while (this.State == ConnectionState.Connecting)
            {
                Thread.Sleep(1);
            }
        }

        /// <inheritdoc />
        public void ConnectAsync(byte[] bytes = null, int timeout = 5000)
        {
            this.State = ConnectionState.Connecting;

            try
            {
                if (IPMode == IPMode.IPv4)
                    socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                else
                    socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            }
            catch (SocketException)
            {
                this.State = ConnectionState.NotConnected;
                throw;
            }

            this.receiveThread.Start();
            this.sendThread.Start();
            this.dispatchThreads.Start();

            // Write bytes to the server to tell it hi (and to punch a hole in our NAT, if present)
            // When acknowledged set the state to connected
            SendHello(bytes, () =>
            {
                this.State = ConnectionState.Connected;
                this.InitializeKeepAliveTimer();
            });
        }

        private void PooledReadCallback()
        {
            while (!this.disposed)
            {
                try
                {
                    MessageReader msg;
                    try
                    {
                        msg = this.receiveQueue.Take();
                    }
                    catch { break; }

                    HandleReceive(msg, msg.Length);
                }
                catch (Exception e)
                {
                    this.logger?.Invoke("ClientException in dispatch: " + e.Message);
                }
            }
        }

        protected override void DisconnectRemote(string reason, MessageReader reader)
        {
            lock (this)
            {
                if (this.State == ConnectionState.NotConnected) return;
                this.State = ConnectionState.NotConnected;
            }
            
            InvokeDisconnectHandler(reason, reader);
        }

        /// <summary>
        ///     Sends a disconnect message to the end point.
        ///     You may include optional disconnect data. The SendOption must be unreliable.
        /// </summary>
        protected override bool SendDisconnect(MessageWriter data = null)
        {
            lock (this)
            {
                if (this.State == ConnectionState.NotConnected) return false;
                this.State = ConnectionState.NotConnected;
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
            this.disposed = true;
            try
            {
                this.sendQueue.CompleteAdding();
                this.sendQueue.Dispose();
            }
            catch { }
            try
            {
                this.receiveQueue.CompleteAdding();
                this.receiveQueue.Dispose();
            }
            catch { }

            try { this.socket.Shutdown(SocketShutdown.Both); } catch { }
            try { this.socket.Close(); } catch { }
            try { this.socket.Dispose(); } catch { }

            this.sendThread.Join();
            this.receiveThread.Join();
            this.dispatchThreads.Join();

            base.Dispose(disposing);
        }
    }
}