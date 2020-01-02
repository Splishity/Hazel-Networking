using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;


namespace Hazel
{
    public abstract class NetworkConnection : IDisposable
    {
        public EndPoint RemoteEndPoint { get; protected set; }

        /// <summary>
        ///     Called when a message has been received.
        /// </summary>
        public event Action<DataReceivedEventArgs> DataReceived;

        /// <summary>
        /// Invoked when:
        /// * Disconnect is called locally
        /// * The remote sends a disconnect request
        /// * An error occurs localled
        /// </summary>
        public event EventHandler<DisconnectedEventArgs> Disconnected;

        public readonly ConnectionStatistics Statistics = new ConnectionStatistics();
        public IPMode IPMode { get; protected set; }

        public ConnectionState State { get; protected set; } = ConnectionState.NotConnected;

        public long GetIP4Address()
        {
            if (this.IPMode == IPMode.IPv4)
            {
                return ((IPEndPoint)this.RemoteEndPoint).Address.Address;
            }
            else
            {
                var bytes = ((IPEndPoint)this.RemoteEndPoint).Address.GetAddressBytes();
                return BitConverter.ToInt64(bytes, bytes.Length - 8);
            }
        }

        /// <summary>
        /// Sends a disconnect message to the end point.
        /// </summary>
        protected abstract bool SendDisconnect(MessageWriter writer);
        
        public abstract void Send(MessageWriter msg);
        public abstract void SendBytes(byte[] bytes, SendOption sendOption = SendOption.None);
        protected abstract void DisconnectRemote(string reason, MessageReader reader);

        /// <summary>
        ///     Called when the socket has been disconnected at the remote host.
        /// </summary>
        protected void InvokeDisconnectHandler(string reason, MessageReader reader)
        {
            try
            {
                var handler = this.Disconnected;
                if (handler != null)
                {
                    handler(this, new DisconnectedEventArgs(reason, reader));
                }
            }
            catch { }
        }

        /// <summary>
        ///     Called when the socket has been disconnected locally.
        /// </summary>
        public void Disconnect(string reason, MessageWriter writer = null)
        {
            if (this.SendDisconnect(writer))
            {
                InvokeDisconnectHandler(reason, null);
            }
        }
        
        protected void InvokeDataReceived(SendOption sendOption, MessageReader msg, int dataOffset, int bytesReceived)
        {
            msg.Offset = dataOffset;
            msg.Length = bytesReceived - dataOffset;
            msg.Position = 0;
            
            var handler = DataReceived;
            if (handler != null)
            {
                handler(new DataReceivedEventArgs(this, msg, sendOption));
            }
            else
            {
                msg.Recycle();
            }
        }

        /// <summary>
        ///     Disposes of this NetworkConnection.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///     Disposes of this NetworkConnection.
        /// </summary>
        /// <param name="disposing">Are we currently disposing?</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.DataReceived = null;
                this.Disconnected = null;
            }
        }
    }
}
