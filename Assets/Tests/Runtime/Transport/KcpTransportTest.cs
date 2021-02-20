using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;
using Cysharp.Threading.Tasks;
using System;

using UnityEngine;
using Random = UnityEngine.Random;
using System.Linq;
using Mirage.KCP;
using System.Collections.Generic;
using NSubstitute;

namespace Mirage.Tests
{
    public class KcpTransportTest
    {
        public ushort port = 7896;

        KcpTransport transport;
        KcpConnection clientConnection;
        KcpConnection serverConnection;

        Uri testUri;

        UniTask listenTask;

        byte[] data;

        Queue<(byte[] data, int channel)> clientMessages;
        Queue<(byte[] data, int channel)> serverMessages;

        [UnitySetUp]
        public IEnumerator Setup() => UniTask.ToCoroutine(async () =>
        {
            // each test goes in a different port
            // that way the transports can take some time to cleanup
            // without interfering with each other.
            port++;

            var transportGo = new GameObject("kcpTransport", typeof(KcpTransport));

            transport = transportGo.GetComponent<KcpTransport>();

            transport.Port = port;
            // speed this up
            transport.HashCashBits = 3;
       
            transport.Connected.AddListener(connection => serverConnection = (KcpConnection)connection);

            listenTask = transport.ListenAsync();

            var uriBuilder = new UriBuilder
            {
                Host = "localhost",
                Scheme = "kcp",
                Port = port
            };

            testUri = uriBuilder.Uri;

            clientConnection = (KcpConnection)await transport.ConnectAsync(uriBuilder.Uri);

            await UniTask.WaitUntil(() => serverConnection != null);

            // for our tests,  lower the timeout to just 0.1s
            // so that the tests run quickly.
            serverConnection.Timeout = 500;
            clientConnection.Timeout = 500;

            clientMessages = new Queue<(byte[], int)>();
            serverMessages = new Queue<(byte[], int)>();

            clientConnection.MessageReceived += (data, channel) =>
            {
                clientMessages.Enqueue((data.ToArray(), channel));
            };
            serverConnection.MessageReceived += (data, channel) =>
            {
                serverMessages.Enqueue((data.ToArray(), channel));
            };

            data = new byte[Random.Range(10, 255)];
            for (int i=0; i< data.Length; i++)
                data[i] = (byte)Random.Range(1, 255);
        });

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            clientConnection?.Disconnect();
            serverConnection?.Disconnect();
            transport.Disconnect();

            await listenTask;
            UnityEngine.Object.Destroy(transport.gameObject);
            // wait a frame so object will be destroyed
        });

        // A Test behaves as an ordinary method
        [Test]
        public void Connect()
        {
            Assert.That(clientConnection, Is.Not.Null);
            Assert.That(serverConnection, Is.Not.Null);
        }

        [Test]
        public void SendDataFromClient()
        {
            clientConnection.Send(new ArraySegment<byte>(data));
            transport.Poll();
            Assert.That(serverMessages.Dequeue().data, Is.EquivalentTo(data));
        }

        [Test]
        public void SendDataFromServer()
        {
            serverConnection.Send(new ArraySegment<byte>(data));
            transport.Poll();
            Assert.That(clientMessages.Dequeue().data, Is.EquivalentTo(data));
        }

        [Test]
        public void ReceivedBytes()
        {
            long received = transport.ReceivedBytes;
            Assert.That(received, Is.GreaterThan(0), "Must have received some bytes to establish the connection");

            clientConnection.Send(new ArraySegment<byte>(data));
            transport.Poll();
            Assert.That(transport.ReceivedBytes, Is.GreaterThan(received + data.Length), "Client sent data,  we should have received");

        }

        [Test]
        public void SentBytes()
        {
            long sent = transport.SentBytes;
            Assert.That(sent, Is.GreaterThan(0), "Must have received some bytes to establish the connection");

            serverConnection.Send(new ArraySegment<byte>(data));
            transport.Poll();
            Assert.That(transport.SentBytes, Is.GreaterThan(sent + data.Length), "Client sent data,  we should have received");

        }

        [Test]
        public void SendUnreliableDataFromServer()
        {
            serverConnection.Send(new ArraySegment<byte>(data), Channel.Unreliable);
            transport.Poll();
            Assert.That(clientMessages.Dequeue().channel, Is.EqualTo(Channel.Unreliable));
        }

        [Test]
        public void SendUnreliableDataFromClient()
        {
            clientConnection.Send(new ArraySegment<byte>(data), Channel.Unreliable);
            transport.Poll();
            Assert.That(serverMessages.Dequeue().channel, Is.EqualTo(Channel.Unreliable));
        }


        [UnityTest]
        public IEnumerator DisconnectServerFromIdle() => UniTask.ToCoroutine(async () =>
        {
            Action disconnectMock = Substitute.For<Action>();

            serverConnection.Disconnected += disconnectMock;

            await UniTask.Delay(1000);
            transport.Poll();
            disconnectMock.Received().Invoke();

        });

        [UnityTest]
        public IEnumerator DisconnectClientFromIdle() => UniTask.ToCoroutine(async () =>
        {
            Action disconnectMock = Substitute.For<Action>();

            clientConnection.Disconnected += disconnectMock;

            await UniTask.Delay(1000);
            transport.Poll();
            disconnectMock.Received().Invoke();
        });

        [Test]
        public void TestServerUri()
        {
            Uri serverUri = transport.ServerUri().First();

            Assert.That(serverUri.Port, Is.EqualTo(port));
            Assert.That(serverUri.Scheme, Is.EqualTo(testUri.Scheme));
        }

        [Test]
        public void IsSupportedTest()
        {
            Assert.That(transport.Supported, Is.True);
        }

        [Test]
        public void ConnectionsDontLeak()
        {
            serverConnection.Disconnect();

            transport.Poll();

            Assert.That(transport.connections, Is.Empty);
        }
    }
}
