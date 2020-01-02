using System;
using System.Net;

namespace Hazel.Udp
{
    /// <summary>
    /// Represents a servers's connection to a client that uses the UDP protocol.
    /// </summary>
    /// <remarks>
    /// Usage:
    /// 1. Created from UdpServerListener
    /// 3. Send (Recycle MessageWriters)
    /// 4. Receive (Recycle MessageReaders)
    /// 5a. Disconnect locally (Optional, sends message synchronously to remote)
    /// 5b. Remote sends disconnect (Connection will be disposed for you.)
    /// 6. Dispose (Doesn't send message to remote)
    /// </remarks>
    /// <inheritdoc/>
    internal sealed class UdpServerConnection : UdpConnection
    {
        /// <summary>
        ///     The connection listener that we use the socket of.
        /// </summary>
        /// <remarks>
        ///     Udp server connections utilize the same socket in the listener for sends/receives, this is the listener that 
        ///     created this connection and is hence the listener this conenction sends and receives via.
        /// </remarks>
        public IConnectionListener Listener { get; private set; }

        /// <summary>
        ///     Creates a UdpConnection for the virtual connection to the endpoint.
        /// </summary>
        /// <param name="listener">The listener that created this connection.</param>
        /// <param name="endPoint">The endpoint that we are connected to.</param>
        /// <param name="IPMode">The IPMode we are connected using.</param>
        internal UdpServerConnection(IConnectionListener listener, IPEndPoint endPoint, IPMode IPMode)
            : base()
        {
            this.Listener = listener;
            this.RemoteEndPoint = endPoint;
            this.IPMode = IPMode;

            State = ConnectionState.Connected;
            this.InitializeKeepAliveTimer();
        }

        /// <inheritdoc />
        protected override void WriteBytesToConnection(byte[] bytes, int length)
        {
            Listener.SendData(bytes, length, RemoteEndPoint);
        }

        /// <summary>
        ///     Sends a disconnect message to the end point.
        /// </summary>
        protected override bool SendDisconnect(MessageWriter data = null)
        {
            if (!Listener.RemoveConnectionTo(RemoteEndPoint))
            {
                return false;
            }

            this.State = ConnectionState.NotConnected;
            
            var bytes = EmptyDisconnectBytes;
            if (data != null && data.Length > 0)
            {
                if (data.SendOption != SendOption.None) throw new ArgumentException("Disconnect messages can only be unreliable.");

                bytes = data.ToByteArray(true);
                bytes[0] = (byte)UdpSendOption.Disconnect;
            }

            try
            {
                Listener.SendDataSync(bytes, bytes.Length, RemoteEndPoint);
            }
            catch { }

            return true;
        }

        protected override void DisconnectRemote(string reason, MessageReader reader)
        {
            if (this.Listener.RemoveConnectionTo(this.RemoteEndPoint))
            {
                InvokeDisconnectHandler(reason, reader);
                this.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (this.Listener.RemoveConnectionTo(this.RemoteEndPoint))
            {
                try
                {
                    this.Listener.SendDataSync(EmptyDisconnectBytes, EmptyDisconnectBytes.Length, this.RemoteEndPoint);
                }
                catch { }
            }
            
            base.Dispose(disposing);
        }
    }
}
