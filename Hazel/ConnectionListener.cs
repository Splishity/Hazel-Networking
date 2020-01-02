using System;
using System.Net;

namespace Hazel
{
    public interface IConnectionListener
    {
        void Start();
        
        void SendData(byte[] bytes, int length, EndPoint endPoint);
        void SendDataSync(byte[] bytes, int length, EndPoint endPoint);
        bool RemoveConnectionTo(EndPoint endPoint);
    }
}
