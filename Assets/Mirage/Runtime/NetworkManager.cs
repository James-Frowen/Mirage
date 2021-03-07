using UnityEngine;
using UnityEngine.Serialization;

namespace Mirage
{

    
    [HelpURL("https://miragenet.github.io/Mirage/Articles/Guides/Communications/NetworkManager.html")]
    [RequireComponent(typeof(NetworkServer))]
    [RequireComponent(typeof(NetworkClient))]
    
    public class NetworkManager : MonoBehaviour
    {
        
        public NetworkServer Server;
        
        public NetworkClient Client;
        
        public NetworkSceneManager SceneManager;
        
        public ServerObjectManager ServerObjectManager;
        
        public ClientObjectManager ClientObjectManager;

        /// <summary>
        /// True if the server or client is started and running
        /// <para>This is set True in StartServer / StartClient, and set False in StopServer / StopClient</para>
        /// </summary>
        public bool IsNetworkActive => Server.Active || Client.Active;
    }
}
