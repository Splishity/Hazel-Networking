using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Threading;
using Hazel.Udp;

namespace Hazel.UnitTests
{
    public partial class UdpConnectionTests
    {
        /// <summary>
        ///     Tests automatic reliable fragmentation for client to server on the UdpConnection.
        /// </summary>
        [TestMethod]
        public void UdpFragmentedClientToServerTest()
        {
            MessageWriter testData = MessageWriter.Get(SendOption.Reliable);
            Random r = new Random(0);
            r.NextBytes(testData.Buffer);
            testData.Length = 4321;

            using (UdpConnectionListener listener = new UdpConnectionListener(new IPEndPoint(IPAddress.Any, 4296)))
            using (UdpConnection connection = new UdpClientConnection(new IPEndPoint(IPAddress.Loopback, 4296)))
            using (ManualResetEventSlim mut = new ManualResetEventSlim())
            {
                MessageReader dataGot = null;
                listener.NewConnection += (conArgs) =>
                {
                    conArgs.Connection.DataReceived += (dataArgs) =>
                    {
                        dataGot = dataArgs.Message;
                        mut.Set();
                    };
                };

                listener.Start();
                connection.Connect();

                connection.Send(testData);

                mut.Wait(5000);
                Thread.Sleep(100);

                Assert.AreEqual(1, MessageWriter.WriterPool.NumberInUse);
                Assert.AreEqual(4, MessageReader.ReaderPool.NumberInUse);

                // -3 Removes the reliable header
                Assert.AreEqual(testData.Length - 3, connection.Statistics.DataBytesSent - 1); // We also send one byte during Connection.
                Assert.IsNotNull(dataGot);
                Assert.AreEqual(testData.Length - 3, dataGot.Length);
                for (int i = 3; i < testData.Length; ++i)
                {
                    Assert.AreEqual(testData.Buffer[i], dataGot.ReadByte(), $"Data differs on byte: {i}");
                }
            }
        }
    }
}
