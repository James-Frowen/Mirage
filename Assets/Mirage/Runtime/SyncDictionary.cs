using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;

namespace Mirage
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public class SyncIDictionary<TKey, TValue> : IDictionary<TKey, TValue>, ISyncObject, IReadOnlyDictionary<TKey, TValue>
    {
        protected readonly IDictionary<TKey, TValue> objects;

        public int Count => objects.Count;
        public bool IsReadOnly { get; private set; }

        internal int ChangeCount => changes.Count;


        /// <summary>
        /// Raised when an element is added to the dictionary.
        /// Receives the key and value of the new item
        /// </summary>
        public event Action<TKey, TValue> OnInsert;

        /// <summary>
        /// Raised when the dictionary is cleared
        /// </summary>
        public event Action OnClear;

        /// <summary>
        /// Raised when an item is removed from the dictionary
        /// receives the key and value of the old item
        /// </summary>
        public event Action<TKey, TValue> OnRemove;

        /// <summary>
        /// Raised when an item is changed in a dictionary
        /// Receives key, the old value and the new value
        /// </summary>
        public event Action<TKey, TValue, TValue> OnSet;

        /// <summary>
        /// Raised after the dictionary has been updated
        /// Note that if there are multiple changes
        /// this event is only raised once.
        /// </summary>
        public event Action OnChange;

        private enum Operation : byte
        {
            OP_ADD,
            OP_CLEAR,
            OP_REMOVE,
            OP_SET
        }

        struct Change
        {
            internal Operation operation;
            internal TKey key;
            internal TValue item;
        }

        readonly List<Change> changes = new List<Change>();
        // how many changes we need to ignore
        // this is needed because when we initialize the list,
        // we might later receive changes that have already been applied
        // so we need to skip them
        int changesAhead;

        public void Reset()
        {
            IsReadOnly = false;
            changes.Clear();
            changesAhead = 0;
            objects.Clear();
        }

        public bool IsDirty => changes.Count > 0;

        public ICollection<TKey> Keys => objects.Keys;

        public ICollection<TValue> Values => objects.Values;

        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => objects.Keys;

        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => objects.Values;

        // throw away all the changes
        // this should be called after a successfull sync
        public void Flush() => changes.Clear();

        public SyncIDictionary(IDictionary<TKey, TValue> objects)
        {
            this.objects = objects;
        }

        void AddOperation(Operation op, TKey key, TValue item)
        {
            if (IsReadOnly)
            {
                throw new InvalidOperationException("SyncDictionaries can only be modified by the server");
            }

            var change = new Change
            {
                operation = op,
                key = key,
                item = item
            };

            changes.Add(change);

            OnChange?.Invoke();
        }

        public void OnSerializeAll(NetworkWriter writer)
        {
            // if init,  write the full list content
            writer.WritePackedUInt32((uint)objects.Count);

            foreach (KeyValuePair<TKey, TValue> syncItem in objects)
            {
                writer.Write(syncItem.Key);
                writer.Write(syncItem.Value);
            }

            // all changes have been applied already
            // thus the client will need to skip all the pending changes
            // or they would be applied again.
            // So we write how many changes are pending
            writer.WritePackedUInt32((uint)changes.Count);
        }

        public void OnSerializeDelta(NetworkWriter writer)
        {
            // write all the queued up changes
            writer.WritePackedUInt32((uint)changes.Count);

            for (int i = 0; i < changes.Count; i++)
            {
                Change change = changes[i];
                writer.WriteByte((byte)change.operation);

                switch (change.operation)
                {
                    case Operation.OP_ADD:
                    case Operation.OP_REMOVE:
                    case Operation.OP_SET:
                        writer.Write(change.key);
                        writer.Write(change.item);
                        break;
                    case Operation.OP_CLEAR:
                        break;
                }
            }
        }

        public void OnDeserializeAll(NetworkReader reader)
        {
            // This list can now only be modified by synchronization
            IsReadOnly = true;

            // if init,  write the full list content
            int count = (int)reader.ReadPackedUInt32();

            objects.Clear();
            changes.Clear();
            OnClear?.Invoke();

            for (int i = 0; i < count; i++)
            {
                TKey key = reader.Read<TKey>();
                TValue obj = reader.Read<TValue>();
                objects.Add(key, obj);
                OnInsert?.Invoke(key, obj);
            }

            // We will need to skip all these changes
            // the next time the list is synchronized
            // because they have already been applied
            changesAhead = (int)reader.ReadPackedUInt32();
            OnChange?.Invoke();
        }

        public void OnDeserializeDelta(NetworkReader reader)
        {
            // This list can now only be modified by synchronization
            IsReadOnly = true;
            bool raiseOnChange = false;

            int changesCount = (int)reader.ReadPackedUInt32();

            for (int i = 0; i < changesCount; i++)
            {
                var operation = (Operation)reader.ReadByte();

                // apply the operation only if it is a new change
                // that we have not applied yet
                bool apply = changesAhead == 0;

                switch (operation)
                {
                    case Operation.OP_ADD:
                        DeserializeAdd(reader, apply);
                        break;

                    case Operation.OP_SET:
                        DeserializeSet(reader, apply);
                        break;

                    case Operation.OP_CLEAR:
                        DeserializeClear(apply);
                        break;

                    case Operation.OP_REMOVE:
                        DeserializeRemove(reader, apply);
                        break;
                }

                if (apply)
                {
                    raiseOnChange = true;
                }
                // we just skipped this change
                else
                {
                    changesAhead--;
                }
            }

            if (raiseOnChange)
            {
                OnChange?.Invoke();
            }
        }

        private void DeserializeAdd(NetworkReader reader, bool apply)
        {
            TKey key = reader.Read<TKey>();
            TValue item = reader.Read<TValue>();
            if (apply)
            {
                objects[key] = item;
                OnInsert?.Invoke(key, item);
            }
        }

        private void DeserializeSet(NetworkReader reader, bool apply)
        {
            TKey key = reader.Read<TKey>();
            TValue item = reader.Read<TValue>();
            if (apply)
            {
                TValue oldItem = objects[key];
                objects[key] = item;
                OnSet?.Invoke(key, oldItem, item);
            }
        }

        private void DeserializeClear(bool apply)
        {
            if (apply)
            {
                objects.Clear();
                OnClear?.Invoke();
            }
        }

        private void DeserializeRemove(NetworkReader reader, bool apply)
        {
            TKey key = reader.Read<TKey>();
            TValue item = reader.Read<TValue>();
            if (apply)
            {
                objects.Remove(key);
                OnRemove?.Invoke(key, item);
            }
        }

        public void Clear()
        {
            objects.Clear();
            OnClear?.Invoke();
            AddOperation(Operation.OP_CLEAR, default, default);
        }

        public bool ContainsKey(TKey key) => objects.ContainsKey(key);

        public bool Remove(TKey key)
        {
            if (objects.TryGetValue(key, out TValue item) && objects.Remove(key))
            {
                OnRemove?.Invoke(key, item);
                AddOperation(Operation.OP_REMOVE, key, item);
                return true;
            }
            return false;
        }

        public TValue this[TKey i]
        {
            get => objects[i];
            set
            {
                if (ContainsKey(i))
                {
                    TValue oldItem = objects[i];
                    objects[i] = value;
                    OnSet?.Invoke(i, oldItem, value);
                    AddOperation(Operation.OP_SET, i, value);
                }
                else
                {
                    objects[i] = value;
                    OnInsert?.Invoke(i, value);
                    AddOperation(Operation.OP_ADD, i, value);
                }
            }
        }

        public bool TryGetValue(TKey key, out TValue value) => objects.TryGetValue(key, out value);

        public void Add(TKey key, TValue value)
        {
            objects.Add(key, value);
            OnInsert?.Invoke(key, value);
            AddOperation(Operation.OP_ADD, key, value);
        }

        public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return TryGetValue(item.Key, out TValue val) && EqualityComparer<TValue>.Default.Equals(val, item.Value);
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            if (arrayIndex < 0 || arrayIndex > array.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(arrayIndex), "Array Index Out of Range");
            }
            if (array.Length - arrayIndex < Count)
            {
                throw new ArgumentException("The number of items in the SyncDictionary is greater than the available space from " + nameof(arrayIndex) + " to the end of the destination array");
            }

            int i = arrayIndex;
            foreach (KeyValuePair<TKey, TValue> item in objects)
            {
                array[i] = item;
                i++;
            }
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            bool result = objects.Remove(item.Key);
            if (result)
            {
                OnRemove?.Invoke(item.Key, item.Value);
                AddOperation(Operation.OP_REMOVE, item.Key, item.Value);
            }
            return result;
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => objects.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => objects.GetEnumerator();
    }

    public class SyncDictionary<TKey, TValue> : SyncIDictionary<TKey, TValue>
    {
        public SyncDictionary() : base(new Dictionary<TKey, TValue>())
        {
        }

        public SyncDictionary(IEqualityComparer<TKey> eq) : base(new Dictionary<TKey, TValue>(eq))
        {
        }

        public new Dictionary<TKey, TValue>.ValueCollection Values => ((Dictionary<TKey, TValue>)objects).Values;

        public new Dictionary<TKey, TValue>.KeyCollection Keys => ((Dictionary<TKey, TValue>)objects).Keys;

        public new Dictionary<TKey, TValue>.Enumerator GetEnumerator() => ((Dictionary<TKey, TValue>)objects).GetEnumerator();

    }
}
