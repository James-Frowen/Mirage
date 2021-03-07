using System;

namespace UnityEngine.Events
{
    public class UnityEvent
    {
        public void AddListener(Action listener)
        {
            throw new NotImplementedException();
        }

        public void Invoke()
        {
            throw new NotImplementedException();
        }
    }
    public class UnityEvent<T0>
    {
        public void AddListener(Action<T0> listener)
        {
            throw new NotImplementedException();
        }
        public void Invoke(T0 arg0)
        {
            throw new NotImplementedException();
        }
    }
    public class UnityEvent<T0, T1>
    {
        public void AddListener(Action<T0, T1> listener)
        {
            throw new NotImplementedException();
        }
        public void Invoke(T0 arg0, T1 arg1)
        {
            throw new NotImplementedException();
        }
    }
}
