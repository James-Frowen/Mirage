using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using UnityEngine;

namespace Mirage
{

    /// <summary>
    /// A connection that is directly connected to another connection
    /// If you send data in one of them,  you receive it on the other one
    /// </summary>
    public class PipeConnection : IConnection
    {

        private PipeConnection connected;
        private EndPoint _endPoint;

        // should only be created by CreatePipe
        private PipeConnection()
        {

        }

        // buffer where we can queue up data
        readonly NetworkWriter writer = new NetworkWriter();

        public static (IConnection, IConnection) CreatePipe()
        {
            var c1 = new PipeConnection();
            var c2 = new PipeConnection();

            c1.connected = c2;
            c2.connected = c1;

            return (c1, c2);
        }

        public int Receive(byte[] buffer, out int length, out EndPoint endpoint)
        {
            throw new NotImplementedException();
        }

        public void Disconnect()
        {
            // disconnect both ends of the pipe
            //connected.Disconnected?.Invoke();

            //Disconnected?.Invoke();
        }
        public void Bind(EndPoint endPoint)
        {
            throw new NotImplementedException();
        }

        public bool Poll()
        {
            Debug.Log("Polling pipeconn");
            var data = writer.ToArraySegment();

            if (data.Count == 0)
                return false;

            using (PooledNetworkReader reader = NetworkReaderPool.GetReader(data))
            {
                while (reader.Position < reader.Length)
                {
                    int channel = reader.ReadPackedInt32();
                    ArraySegment<byte> packet = reader.ReadBytesAndSizeSegment();

                    //MessageReceived(packet, channel);
                }
            }

            writer.SetLength(0);

            return true;
        }
        public bool Supported { get; set; }
        public long ReceivedBytes { get; set; }
        public long SentBytes { get; set; }
        public IConnection Connect(Uri uri)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<Uri> ServerUri()
        {
            throw new NotImplementedException();
        }
        // technically not an IPEndpoint,  will fix later
        public EndPoint GetEndPointAddress
        {
            get
            {
                return new IPEndPoint(IPAddress.Loopback, 0);
            }
            set
            {
                _endPoint = value;
            }
        }


        public IEnumerable<string> Scheme { get; set; }
        public void Send(ArraySegment<byte> data, int channel = Channel.Reliable)
        {
            // add some data to the writer in the connected connection
            // and increase the message count
            connected.writer.WritePackedInt32(channel);
            connected.writer.WriteBytesAndSizeSegment(data);
            connected.MessageCount.Release();
        }
    }
}
