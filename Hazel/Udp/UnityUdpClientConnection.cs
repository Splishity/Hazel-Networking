using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;


namespace Hazel.Udp
{
    /// <summary>
    ///     Represents a client's connection to a server that uses the UDP protocol.
    /// </summary>
    /// <inheritdoc/>
    public class UnityUdpClientConnection : UdpConnection
    {
        private Socket socket;

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
        public UnityUdpClientConnection(int numThreads, IPEndPoint remoteEndPoint, IPMode ipMode = IPMode.IPv4)
            : base()
        {
            this.EndPoint = remoteEndPoint;
            this.RemoteEndPoint = remoteEndPoint;
            this.IPMode = ipMode;

            this.socket = CreateSocket(ipMode);
            this.socket.Blocking = false;

            this.sendThread = new Thread(this.SendLoop);
            this.sendThread.Name = "SendThread";
            this.receiveThread = new Thread(this.ReceiveLoop);
            this.receiveThread.Name = "ReceiveThread";
            this.dispatchThreads = new HazelThreadPool(numThreads, this.PooledReadCallback);
        }
        
        ~UnityUdpClientConnection()
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
                    catch (NullReferenceException)
                    {
                        msg.Recycle();
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        msg.Recycle();
                        break;
                    }
                    catch (SocketException ex)
                    {
                        msg.Recycle();
                        Disconnect("Could not read data as a SocketException occurred: " + ex.Message);
                        break;
                    }

                    try
                    {
                        receiveQueue.Add(msg);
                    }
                    catch { }
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
                catch (NullReferenceException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex)
                {
                    Disconnect("Could not send data as a SocketException occurred: " + ex.Message);
                    break;
                }
            }
        }

        /// <inheritdoc />
        protected override void WriteBytesToConnection(byte[] bytes, int length)
        {
            sendQueue.Add(new Tuple<byte[], int>(bytes, length));
        }

        public override void Connect(byte[] bytes = null, int timeout = 5000)
        {
            this.ConnectAsync(bytes, timeout);

            while (this.State == ConnectionState.Connecting)
            {
                Thread.Sleep(1);
            }
        }

        /// <inheritdoc />
        public override void ConnectAsync(byte[] bytes = null, int timeout = 5000)
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
                    var msg = this.receiveQueue.Take();
                    HandleReceive(msg, msg.Length);
                }
                catch
                {
                }
            }            
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

            this.disposed = true;
            this.sendQueue.CompleteAdding();
            this.receiveQueue.CompleteAdding();

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