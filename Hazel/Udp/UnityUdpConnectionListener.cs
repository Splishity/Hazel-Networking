using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Hazel.Udp
{
    public class UnityUdpConnectionListener : NetworkConnectionListener
    {
        private struct SendQueueData
        {
            public byte[] Buffer;
            public int Length;
            public EndPoint Remote;
        }

        private const int SendReceiveBufferSize = 1024 * 1024;
        private const int BufferSize = ushort.MaxValue;

        /// <summary>
        /// A callback for early connection rejection. 
        /// * Return false to reject connection.
        /// * A null response is ok, we just won't send anything.
        /// </summary>
        public AcceptConnectionCheck AcceptConnection;
        public delegate bool AcceptConnectionCheck(IPEndPoint endPoint, byte[] input, out byte[] response);

        private Socket socket;
        private Action<string> Logger;
        private Timer reliablePacketTimer;

        private Thread receiveThread;
        private Thread sendThread;
        private HazelThreadPool dispatchThreads;

        private bool disposed;

        private ConcurrentDictionary<EndPoint, UdpServerConnection> allConnections = new ConcurrentDictionary<EndPoint, UdpServerConnection>();

        private BlockingCollection<Tuple<MessageReader, EndPoint>> receiveQueue = new BlockingCollection<Tuple<MessageReader, EndPoint>>();
        private BlockingCollection<SendQueueData> sendQueue = new BlockingCollection<SendQueueData>();

        public int ConnectionCount { get { return this.allConnections.Count; } }
        public int SendQueueLength { get { return this.sendQueue.Count; } }
        public int ReceiveQueueLength { get { return this.receiveQueue.Count; } }

        /// <summary>
        ///     Creates a new UdpConnectionListener for the given <see cref="IPAddress"/>, port and <see cref="IPMode"/>.
        /// </summary>
        /// <param name="endPoint">The endpoint to listen on.</param>
        public UnityUdpConnectionListener(int numThreads, IPEndPoint endPoint, IPMode ipMode = IPMode.IPv4, Action<string> logger = null)
        {
            this.Logger = logger;
            this.EndPoint = endPoint;
            this.IPMode = ipMode;

            this.socket = UdpConnection.CreateSocket(this.IPMode);
            socket.Blocking = false;
            socket.ReceiveBufferSize = SendReceiveBufferSize;
            socket.SendBufferSize = SendReceiveBufferSize;
            
            reliablePacketTimer = new Timer(ManageReliablePackets, null, Timeout.Infinite, Timeout.Infinite);

            this.sendThread = new Thread(this.SendLoop);
            this.sendThread.Name = "SendThread";
            this.receiveThread = new Thread(this.ReceiveLoop);
            this.receiveThread.Name = "ReceiveThread";
            this.dispatchThreads = new HazelThreadPool(numThreads, this.PooledReadCallback);
        }

        ~UnityUdpConnectionListener()
        {
            this.Dispose(false);
        }
        
        private void ManageReliablePackets(object state)
        {
            foreach (var kvp in this.allConnections)
            {
                var sock = kvp.Value;
                sock.ManageReliablePackets();
            }

            try
            {
                this.reliablePacketTimer.Change(100, Timeout.Infinite);
            }
            catch { }
        }

        /// <inheritdoc />
        public override void Start()
        {
            try
            {
                socket.Bind(EndPoint);
            }
            catch (SocketException e)
            {
                throw new HazelException("Could not start listening as a SocketException occurred", e);
            }

            reliablePacketTimer.Change(100, Timeout.Infinite);

            this.sendThread.Start();
            this.receiveThread.Start();
            this.dispatchThreads.Start();
        }

        private void ReceiveLoop()
        {
            try
            {
                while (!this.disposed)
                {
                    if (socket.Poll(-1, SelectMode.SelectRead))
                    {
                        MessageReader msg = MessageReader.GetSized(ushort.MaxValue);
                        EndPoint remoteEP = this.EndPoint;

                        try
                        {
                            msg.Offset = 0;
                            msg.Length = socket.ReceiveFrom(msg.Buffer, 0, msg.Buffer.Length, SocketFlags.None, ref remoteEP);
                        }
                        catch (Exception sx)
                        {
                            msg.Recycle();
                            this.Logger?.Invoke("Socket Ex in ReadLoop: " + sx.Message);
                            break;
                        }

                        try
                        {
                            receiveQueue.Add(new Tuple<MessageReader, EndPoint>(msg, remoteEP));
                        }
                        catch { }
                    }
                    else
                    {
                        this.Logger?.Invoke("Socket.Poll returned false.");
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                this.Logger?.Invoke("ReadThread exited because: " + e.Message);
            }
        }

        private void SendLoop()
        {
            try
            {
                while (!this.disposed)
                {
                    SendQueueData sendData;
                    try
                    {
                        sendData = sendQueue.Take();
                    }
                    catch { break; }

                    try
                    {
                        socket.SendTo(sendData.Buffer, 0, sendData.Length, SocketFlags.None, sendData.Remote);
                    }
                    catch (Exception sx)
                    {
                        this.Logger?.Invoke("Socket Ex in SendLoop: " + sx.Message);
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                this.Logger?.Invoke("SendThread exited because: " + e.Message);
            }
        }

        private void PooledReadCallback()
        {
            while (!this.disposed)
            {
                Tuple<MessageReader, EndPoint> msg;
                try
                {
                    msg = this.receiveQueue.Take();
                }
                catch { break; }

                var message = msg.Item1;
                var remoteEndPoint = msg.Item2;

                try
                {
                    HandleMessage(message, remoteEndPoint);
                }
                catch (Exception e)
                {
                    this.Logger?.Invoke("Error while handling message: " + e);
                }
            }
        }

        private void HandleMessage(MessageReader message, EndPoint remoteEndPoint)
        {
            bool aware = true;
            bool isHello = message.Buffer[0] == (byte)UdpSendOption.Hello;

            // If we're aware of this connection use the one already
            // If this is a new client then connect with them!
            UdpServerConnection connection;
            if (!this.allConnections.TryGetValue(remoteEndPoint, out connection))
            {
                lock (this.allConnections)
                {
                    if (!this.allConnections.TryGetValue(remoteEndPoint, out connection))
                    {
                        // Check for malformed connection attempts
                        if (!isHello)
                        {
                            message.Recycle();
                            return;
                        }

                        if (AcceptConnection != null)
                        {
                            if (!AcceptConnection((IPEndPoint)remoteEndPoint, message.Buffer, out var response))
                            {
                                message.Recycle();
                                if (response != null)
                                {
                                    SendData(response, response.Length, remoteEndPoint);
                                }

                                return;
                            }
                        }

                        aware = false;
                        connection = new UdpServerConnection(this, (IPEndPoint)remoteEndPoint, this.IPMode);
                        if (!this.allConnections.TryAdd(remoteEndPoint, connection))
                        {
                            throw new HazelException("Failed to add a connection. This should never happen.");
                        }
                    }
                }
            }

            //Inform the connection of the buffer (new connections need to send an ack back to client)
            var bytesReceived = message.Length;
            connection.HandleReceive(message, bytesReceived);

            //If it's a new connection invoke the NewConnection event.
            if (!aware)
            {
                // Skip header and hello byte;
                message.Offset = 4;
                message.Length = bytesReceived - 4;
                message.Position = 0;
                InvokeNewConnection(message, connection);
            }
            else if (isHello)
            {
                message.Recycle();
            }
        }

        /// <inheritdoc />
        internal override void SendData(byte[] bytes, int length, EndPoint endPoint)
        {
            sendQueue.Add(new SendQueueData
            {
                Buffer = bytes,
                Length = length,
                Remote = endPoint
            });
        }

        /// <summary>
        ///     Sends data from the listener socket.
        /// </summary>
        /// <param name="bytes">The bytes to send.</param>
        /// <param name="endPoint">The endpoint to send to.</param>
        internal override void SendDataSync(byte[] bytes, int length, EndPoint endPoint)
        {
            try
            {
                socket.SendTo(
                    bytes,
                    0,
                    length,
                    SocketFlags.None,
                    endPoint
                );
            }
            catch { }
        }

        /// <summary>
        ///     Removes a virtual connection from the list.
        /// </summary>
        /// <param name="endPoint">The endpoint of the virtual connection.</param>
        internal override bool RemoveConnectionTo(EndPoint endPoint)
        {
            return this.allConnections.TryRemove(endPoint, out _);
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            foreach (var kvp in this.allConnections)
            {
                kvp.Value.Dispose();
            }

            this.reliablePacketTimer.Dispose();

            this.disposed = true;
            this.receiveQueue.CompleteAdding();
            this.sendQueue.CompleteAdding();

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
