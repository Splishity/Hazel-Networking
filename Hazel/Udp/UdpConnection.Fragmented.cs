using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;


namespace Hazel.Udp
{
    partial class UdpConnection
    {
        private const int FragmentedHeaderSizeBytes = 7;

        private int fragmentMessageId = ushort.MaxValue;
        private ConcurrentDictionary<ushort, MessageReader[]> fragmentsInProgress = new ConcurrentDictionary<ushort, MessageReader[]>();

        public bool AutomaticFragmentation = true;

        /// <summary>
        /// Should be in the range [256, 1200]
        /// Too small and the fragment header can overflow, overhead grows large, and reliability can suffer.
        /// Too large and the fragments are too likely to be bigger than the route MTU.
        /// Default = 1000
        /// </summary>
        public int MTUSizeInBytes = 1000;

        private void FragmentedReliableSend(byte[] data, int fullLength)
        {
            int maxDataSize = MTUSizeInBytes - FragmentedHeaderSizeBytes;
            byte numFragments = (byte)Math.Ceiling(fullLength / (float)maxDataSize);

            ushort msgId = (ushort)Interlocked.Increment(ref this.fragmentMessageId);

            for (byte fragId = 0; fragId < numFragments; ++fragId)
            {
                int length = Math.Min(maxDataSize, fullLength - fragId * maxDataSize);
                byte[] bytes = new byte[length + FragmentedHeaderSizeBytes];

                // Add message type
                bytes[0] = (byte)UdpSendOption.Fragment;

                // Add reliable ID
                AttachReliableID(bytes, 1, bytes.Length);

                // Add fragment info
                bytes[3] = (byte)(msgId >> 0);
                bytes[4] = (byte)(msgId >> 8);

                bytes[5] = fragId;
                bytes[6] = numFragments;

                Buffer.BlockCopy(data, fragId * maxDataSize, bytes, FragmentedHeaderSizeBytes, length);

                WriteBytesToConnection(bytes, bytes.Length);
                this.Statistics.LogFragmentedSend(length, bytes.Length);
            }
        }

        /// <summary>
        /// Handles a fragment message being received and invokes the data event when the message is complete.
        /// </summary>
        /// <param name="message">The buffer received.</param>
        private void FragmentedMessageReceive(MessageReader message, int bytesReceived)
        {
            if (bytesReceived < FragmentedHeaderSizeBytes)
            {
                // TODO: Bad message. Should we just disconnect or what?
                message.Recycle();
                return;
            }

            // Piggybacking off Reliable messaging will prevent us from double processing fragments
            if (ProcessReliableReceive(message.Buffer, 1, out ushort id))
            {
                ushort msgId = (ushort)(
                    (message.Buffer[3] << 0) |
                    (message.Buffer[4] << 8));

                byte fragmentId = message.Buffer[5];
                byte numFragments = message.Buffer[6];
                if (!this.fragmentsInProgress.TryGetValue(msgId, out MessageReader[] fragments))
                {
                    fragments = new MessageReader[numFragments];
                    if (!this.fragmentsInProgress.TryAdd(msgId, fragments))
                    {
                        this.fragmentsInProgress.TryGetValue(msgId, out fragments);
                    }
                }

                if (fragments.Length != numFragments
                    || fragmentId >= numFragments)
                {
                    // TODO: Bad message. Should we just disconnect or what?
                    message.Recycle();
                    return;
                }

                // Determine if this is the last fragment needed
                bool missingFragmentFound = false;
                lock (fragments)
                {
                    if (fragments[fragmentId] == null)
                    {
                        fragments[fragmentId] = message;
                        for (int i = 0; i < fragments.Length; ++i)
                        {
                            if (fragments[i] == null)
                            {
                                missingFragmentFound = true;
                                break;
                            }
                        }
                    }
                }

                if (!missingFragmentFound
                    && this.fragmentsInProgress.TryRemove(msgId, out _))
                {
                    // Reconstruct the message
                    MessageReader target = fragments[0];
                    for (int i = 1; i < fragments.Length; ++i)
                    {
                        var src = fragments[i];
                        Buffer.BlockCopy(src.Buffer, FragmentedHeaderSizeBytes, target.Buffer, target.Length, src.Length);
                        target.Length += (src.Length - FragmentedHeaderSizeBytes);

                        src.Recycle();
                    }

                    InvokeDataReceived(SendOption.Reliable, target, FragmentedHeaderSizeBytes, target.Length);
                }
            }
            else
            {
                message.Recycle();
            }

            Statistics.LogFragmentedReceive(message.Length - FragmentedHeaderSizeBytes, message.Length);
        }
    }
}
