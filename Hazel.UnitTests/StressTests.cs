using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Hazel.Udp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hazel.UnitTests
{
    [TestClass]
    public class StressTests
    {
        [TestMethod]
        public void StressTestOpeningConnections()
        {
            using (UdpConnectionListener listener = new UdpConnectionListener(3, new IPEndPoint(IPAddress.Any, 22023)))
            {
                listener.Start();

                // Start a listener in another process, or even better, 
                // adjust the target IP and start listening on another computer.
                var ep = new IPEndPoint(IPAddress.Loopback, 22023);
                Parallel.For(0, 1000,
                    new ParallelOptions { MaxDegreeOfParallelism = 64 },
                    (i) =>
                    {

                        var connection = new UdpClientConnection(1, ep);
                        connection.KeepAliveInterval = 50;

                        connection.Connect(new byte[5]);

                        connection.Dispose();
                    });
            }
        }
    }
}
