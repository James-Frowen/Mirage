using System;
using System.Net;
using Cysharp.Threading.Tasks;

namespace Mirage
{
    /// <summary>
    /// An object that can send messages
    /// </summary>
    public interface IMessageSender
    {
        void Send<T>(T message, int channelId = Channel.Reliable);

        void Send(ArraySegment<byte> segment, int channelId = Channel.Reliable);
    }

    /// <summary>
    /// An object that can receive messages
    /// </summary>
    public interface IMessageReceiver
    {
        void RegisterHandler<T>(Action<INetworkConnection, T> handler);

        void RegisterHandler<T>(Action<T> handler);

        void UnregisterHandler<T>();

        void ClearHandlers();

        /// <summary>
        /// ProcessMessages loop, should loop unitil object is closed
        /// </summary>
        /// <returns></returns>
        UniTask ProcessMessagesAsync();
    }

    /// <summary>
    /// An object that can send notify messages
    /// </summary>
    public interface INotifySender
    {
        /// <summary>
        /// Sends a message, but notify when it is delivered or lost
        /// </summary>
        /// <typeparam name="T">type of message to send</typeparam>
        /// <param name="message">message to send</param>
        /// <param name="token">a arbitrary object that the sender will receive with their notification</param>
        void SendNotify<T>(T message, object token, int channelId = Channel.Unreliable);
    }

    /// <summary>
    /// An object that can receive notify messages
    /// </summary>
    public interface INotifyReceiver
    {
        /// <summary>
        /// Raised when a message is delivered
        /// </summary>
        event Action<INetworkConnection, object> NotifyDelivered;

        /// <summary>
        /// Raised when a message is lost
        /// </summary>
        event Action<INetworkConnection, object> NotifyLost;
    }

    /// <summary>
    /// An object that can send and receive messages and notify messages
    /// </summary>
    public interface IMessageHandler : IMessageSender, IMessageReceiver, INotifySender, INotifyReceiver
    {

    }

    /// <summary>
    /// An object that can observe NetworkIdentities.
    /// this is useful for interest management
    /// </summary>
    public interface IVisibilityTracker
    {
        void AddToVisList(NetworkIdentity identity);
        void RemoveFromVisList(NetworkIdentity identity);
        void RemoveObservers();
    }

    /// <summary>
    /// An object that can own networked objects
    /// </summary>
    public interface IObjectOwner
    {
        NetworkIdentity Identity { get; set; }
        void RemoveOwnedObject(NetworkIdentity networkIdentity);
        void AddOwnedObject(NetworkIdentity networkIdentity);
        void DestroyOwnedObjects();
    }

    /// <summary>
    /// A connection to a remote endpoint.
    /// May be from the server to client or from client to server
    /// </summary>
    public interface INetworkConnection : IMessageHandler, IVisibilityTracker, IObjectOwner
    {
        bool IsReady { get; set; }
        EndPoint Address { get; }
        object AuthenticationData { get; set; }

        void Disconnect();
    }
}
