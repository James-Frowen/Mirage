using System;

namespace UnityEngine
{
    public class MirageComponent
    {
        public MirageComponent(GameObject gameObject)
        {
            this.gameObject = gameObject ?? throw new ArgumentNullException(nameof(gameObject));
        }

        public string name
        {
            get => gameObject.name;
            set => gameObject.name = value;
        }
        public GameObject gameObject { get; }
    }
    public class GameObject
    {
        public bool activeSelf => throw new NotImplementedException();

        public GameObject() : this("new gameobject") { }
        public GameObject(string name)
        {
            this.name = name ?? throw new ArgumentNullException(nameof(name));
        }

        public string name { get; set; }
    }
}
