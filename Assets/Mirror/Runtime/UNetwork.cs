using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Mirror
{
    // Handles requests to spawn objects on the client
    public delegate GameObject SpawnDelegate(Vector3 position, Guid assetId);

    public delegate GameObject SpawnHandlerDelegate(SpawnMessage msg);

    // Handles requests to unspawn objects on the client
    public delegate void UnSpawnDelegate(GameObject spawned);

    // invoke type for Rpc
    public enum MirrorInvokeType
    {
        ServerRpc,
        ClientRpc
    }

    public enum Version
    {
        Current = 1
    }

    // -- helpers for float conversion without allocations --
    [StructLayout(LayoutKind.Explicit)]
    internal struct UIntFloat
    {
        [FieldOffset(0)]
        public float floatValue;

        [FieldOffset(0)]
        public uint intValue;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct UIntDouble
    {
        [FieldOffset(0)]
        public double doubleValue;

        [FieldOffset(0)]
        public ulong longValue;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct UIntDecimal
    {
        [FieldOffset(0)]
        public ulong longValue1;

        [FieldOffset(8)]
        public ulong longValue2;

        [FieldOffset(0)]
        public decimal decimalValue;
    }
}
