using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections;
using System.IO;
using System.Net;
using NSubstitute;
using NUnit.Framework;

namespace Mirage
{
    public class AsyncPipeConnectionTest
    {

        IConnection c1;
        IConnection c2;

        [SetUp]
        public void Setup()
        {
            (c1, c2) = PipeConnection.CreatePipe();
        }

        private static void SendData(IConnection c, byte[] data)
        {
            c.Send(new ArraySegment<byte>(data));
        }


        private static List<byte[]> ReceiveAll(IConnection connection)
        {
            var packets = new List<byte[]>();

            void ReceiveHandler(ArraySegment<byte> data, int channel)
            {
                packets.Add(data.ToArray());
            }

            //connection.MessageReceived += ReceiveHandler;
            connection.Poll();
            //connection.MessageReceived -= ReceiveHandler;

            return packets;
        }

        [Test]
        public void TestSendAndReceive()
        {
            SendData(c1, new byte[] { 1, 2, 3, 4 });

            List<byte[]> received = ReceiveAll(c2);
            Assert.That(received[0], Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        }

        [Test]
        public void TestSendAndReceiveMultiple()
        {
            SendData(c1, new byte[] { 1, 2, 3, 4 });
            SendData(c1, new byte[] { 5, 6, 7, 8 });

            List<byte[]> received = ReceiveAll(c2);
            Assert.That(received[0], Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            Assert.That(received[1], Is.EqualTo(new byte[] { 5, 6, 7, 8 }));
        }

        [Test]
        public void TestDisconnectC1()
        {
            Action disconnectMock = Substitute.For<Action>();

            //c1.Disconnected += disconnectMock;
            // disconnecting c1 should disconnect c1
            c1.Disconnect();

            disconnectMock.Received().Invoke();
        }

        [Test]
        public void TestDisconnectC2()
        {
            Action disconnectMock = Substitute.For<Action>();

            //c2.Disconnected += disconnectMock;
            // disconnecting c1 should disconnect c2
            c1.Disconnect();

            disconnectMock.Received().Invoke();
        }

        [Test]
        public void TestAddressC1()
        {
            Assert.That(c1.GetEndPointAddress, Is.EqualTo(new IPEndPoint(IPAddress.Loopback, 0)));
        }

        [Test]
        public void TestAddressC2()
        {
            Assert.That(c2.GetEndPointAddress, Is.EqualTo(new IPEndPoint(IPAddress.Loopback, 0)));
        }

    }
}
