> Verified against main @ 0638bf8e5

# 05 - Serialization

**Summary.** `NetworkWriter`/`NetworkReader` are bit-level (not byte-aligned) pooled buffers. The weaver auto-generates writers/readers for structs/classes (public instance fields only) the first time it sees them used as a SyncVar, RPC parameter, `[NetworkMessage]`, or generic `Send/Register/Write/Read<T>` argument, and it picks up any `static void X(this NetworkWriter, T)` / `static T Y(this NetworkReader)` extension pair as the serializer for `T`. Bit-packing attributes (`[BitCount]`, `[FloatPack]`, `[Vector3Pack]`, ...) on fields/parameters shrink data to exactly the bits you ask for (`[SyncVar]` members are fields on `main`). Important defaults: `int/uint/long/ulong` are **variable-length packed**, `float` is 32 bits, `Vector3` is 96 bits, `Quaternion` is a **lossy 29-bit** form.

---

## 1. NetworkWriter / NetworkReader

- `Assets/Mirage/Runtime/Serialization/NetworkWriter.cs`: unsafe, writes into a pinned `byte[]` through a `ulong*`, tracking a **bit** position. `WriteBoolean` = 1 bit; `WriteByte/UInt16/UInt32/UInt64/Single/Double` fixed widths; `Write(ulong value, int bits)` arbitrary 0-64 bits (masks the value); `WriteAtPosition` back-patches; `MoveBitPosition`, `PadToByte`; `CopyFromWriter/CopyFromPointer` copy bit ranges (used to duplicate owner->observers payloads, `NetworkIdentity.cs:787-788`) (`NetworkWriter.cs:206-316, 330-485`).
- Byte-oriented writes (`WriteBytes`, `WriteSpanRaw`, `PadAndCopy<T>`) first **pad to the next byte boundary** (`:366-411`). So `ArraySegment<byte>` fields (e.g. `RpcMessage.Payload`, `UpdateVarsMessage.Payload`) cost padding + a packed length.
- Auto-resize: buffers double up to **512 KiB** (`MAX_BUFFER_SIZE`, `:43, 109-134`) and log a warning on each resize; pooled writers start at the socket MTU (1232 for UDP; static default 1300) (`NetworkWriterPool.cs:18-44`, `NetworkServer.cs:348`).
- `NetworkReader` (`NetworkReader.cs`): same bit layout; `Reset(array, offset, length)` pins the array with a `GCHandle` (`:123-147`); reads past the end throw `EndOfStreamException` (`:188-191`); `CanReadBytes/CanReadBits` for safe loops. A deserialization exception on the server costs 50 error tokens, on the client (`DisconnectOnException`) disconnects (file 03).
- `MirageNetworkReader` adds an `IObjectLocator` so `NetworkIdentity`/`GameObject`/`NetworkBehaviour` values resolve (`NetworkReaderPool.cs:72-85`; `MirageTypesExtensions.cs:55-113`). Readers handed to your handlers/hooks are valid only for that call.

## 2. Default encodings (what a plain `struct` costs)

Derived from which writer the weaver registers per type (`Assets/Mirage/Weaver/Processors/ReaderWriterProcessor.cs:463-525, 557-569`: any non-generic static extension `void M(this NetworkWriter, T)` registers `T`; return-typed `T M(this NetworkReader)` registers the reader). Only candidates for `int`-family in the package are the `PackedExtensions` (`Assets/Mirage/Runtime/Serialization/Packers/PackedExtensions.cs:13-33, 80-105`). **The docs say "normally an integer will be serialized as 32 bits" (`doc/docs/guides/bit-packing/index.md:5`) but the code path says otherwise; I did not run it in-engine - verify with `writer.Write<int>(1); writer.BitPosition`.**

| Type | Encoding | Source |
|---|---|---|
| `bool` | 1 bit | `SystemTypesExtensions.cs:57` |
| `byte`/`sbyte` | 8 bits | `:51-53` |
| `short`/`ushort`/`char` | 16 bits fixed | `:55, 59-64` |
| `int`/`long` | **zigzag + var-length packed** (1 byte for values -120..120; 2 for <= 2287 zigzagged...; up to 9) | `PackedExtensions.cs:13, 27, 33-75` |
| `uint`/`ulong` | **var-length packed**: 1 byte <= 240, 2 <= 2287, 3 <= 67823, then 1 + n bytes | `PackedExtensions.cs:20, 33-75` |
| `float` / `double` | raw 32 / 64 bits | `SystemTypesExtensions.cs:66-81` |
| `decimal`, `Guid` | 128 bits | `:82-99` |
| enum | writer of underlying type (so `enum : int` is packed; `: byte` is 8 bits) | `Weaver/Serialization/Writers.cs:49-63` |
| `Vector2/3/4`, `Color`, `Rect`, `Plane`, `Ray`, `Matrix4x4` | raw floats (Vector3 = 96 bits) | `UnityTypesExtensions.cs:7-95` |
| `Vector2Int/Vector3Int` | packed ints | `:28-39` |
| `Quaternion` | **lossy "smallest three", 9 bits/element + 2 = 29 bits** (`QuaternionPacker.Default9`) | `CompressedExtensions.cs:14-17`, test `Assets/Tests/Runtime/Serialization/CompressionExtensionTests.cs:33`. The comment "we do NOT compress rotation" in `NetworkTransformBase.cs:97-100` is stale. |
| `string` | UTF-8, length-prefixed, max 1300 bytes (`MaxStringLength`); optional `StringStore` interning | `StringExtensions.cs:9-56` |
| arrays / `List<T>` / `ArraySegment<T>` / `Dictionary` | packed `count + 1` (0 = null), then elements through `Writer<T>` | `CollectionExtensions.cs:82-176, 534-547` |
| `ArraySegment<byte>` | packed `count + 1`, pad to byte, raw bytes | `:68-71` |
| `T?` (nullable struct) | 1 bit + value | `SystemTypesExtensions.cs:101-110` |
| `NetworkIdentity`/`GameObject` | packed netId (0 = null) | `MirageTypesExtensions.cs:11-41` |
| `NetworkBehaviour` | packed netId + 1 byte component index | `:20-29` |
| struct / class | each public instance field in declaration order (base class fields first); class adds a leading bool "not null" | `Weaver/Serialization/Writers.cs:100-162` |

Message header: 16-bit id (fixed), then fields. 

## 3. Weaver-generated serializers: when and what

- **Triggers** (`ReaderWriterProcessor.cs:176-320`): (1) types with `[NetworkMessage]`; (2) concrete generic arguments of `MessagePacker.Pack/GetId/Unpack`, `IMessageSender.Send`, `IMessageReceiver.RegisterHandler/UnregisterHandler`, `NetworkPlayer.Send`, `MessageHandler.*`, `NetworkClient.Send`, `NetworkServer.SendToAll/SendToMany`; (3) concrete `T` of `writer.Write<T>()`/`reader.Read<T>()` and `Writer<T>`/`Reader<T>` static usage; (4) RPC parameters/return types; (5) SyncVars and SyncObject element types.
- **Not seen** if `T` is only known through your own generic helper (`void Foo<T>() => writer.Write<T>(...)`): put `[NetworkMessage]` on the struct (or reference it concretely once) to force generation. At runtime a missing serializer throws `KeyNotFoundException` ("No writer found for ...") (`GenericTypesSerializationExtensions.cs:64-67, 98-101`).
- **Fields serialized:** public, non-static, non-`[NonSerialized]`, non-`[WeaverIgnore]` fields of the type and its base types; **properties, private/protected/internal fields are ignored** (`Weaver/Mirage.CecilExtensions/Extensions/TypeExtensions.cs:226-251`). Interfaces, abstract types, open generics, `Component`-derived types (apart from `NetworkBehaviour`) and the bare `UnityEngine.Object` / `ScriptableObject` types cannot be generated (`SerializeFunctionBase.cs:228-259`); user `ScriptableObject` subclasses *are* generated, the reader calls `ScriptableObject.CreateInstance` (`Weaver/Serialization/Readers.cs:218-222`). Class types get a leading `bool` "not null" (`Writers.cs:106-107`) and allocate on read; `main` gives **no** weaver warning for class-typed SyncVars/RPC params/message fields (the `[WeaverSafeClass]` warning exists only on `master`).
- Generated functions are stored in `Writer<T>.Write` / `Reader<T>.Read` delegates by a `RuntimeInitializeOnLoadMethod(BeforeSceneLoad)` initializer (`ReaderWriterProcessor.cs:339-367`), so serialization is available before any scene loads.
- `[WeaverIgnore]` on a whole assembly is **not** supported on `main`: `WeaverIgnoreAttribute` targets only `Method | Field` (`WeaverAttributes.cs:11-12`) and `Weaver.Process` has no assembly check (`Weaver.cs:40-48`); that skip is a `master` addition. Weaving is an `ILPostProcessor` running on assemblies that reference `Mirage` (`MirageILPostProcessor.cs:10-28`).

### Custom writers/readers

```csharp
public static class PlayerInputSerializers   // any static class in a weaved assembly
{
    public static void WritePlayerInput(this NetworkWriter w, PlayerInput v)
    {
        w.Write(v.Tick, 16);               // exact bit width
        w.Write((ulong)(v.MoveX + 100), 8);
        // ...
    }
    public static PlayerInput ReadPlayerInput(this NetworkReader r)
    {
        var v = new PlayerInput { Tick = (ushort)r.Read(16), MoveX = (int)r.Read(8) - 100 };
        return v;
    }
}
```

Rules: non-generic, `public static`, first parameter `NetworkWriter` / `NetworkReader`, return `void` / the type; name is irrelevant; `[WeaverIgnore]` opts out; two registrations for the same type log a weaver warning and the later wins (`SerializeFunctionBase.cs:47-70`). Generic collection helpers use `[WeaverSerializeCollection]` (`CollectionExtensions.cs:81`). `[WeaverWriteAsGeneric]` on a type tells the weaver to route through `Writer<T>.Write` that you assign yourself (`WeaverAttributes.cs:25-26`, `SerializeFunctionBase.cs:141-142`).

## 4. Bit-packing attributes

All target `Field | Parameter` on `main` (`Property` is a valid target only on `master`) (`Assets/Mirage/Runtime/Serialization/WeaverAttributes.cs:47-156`), so they work on struct fields, `[SyncVar]` fields (read straight off the field, `ValueSerializerFinder.cs:13-16`), and RPC parameters (`ValueSerializerFinder.cs:39-43`). One int-packing attribute per member (`[BitCount]`, `[VarInt]`, `[VarIntBlocks]`, `[BitCountFromRange]` are mutually exclusive; `ValueSerializerFinder.cs:81-133`).

| Attribute | Applies to | Result | Notes (source) |
|---|---|---|---|
| `[BitCount(n)]` | `byte, short, ushort, int, uint, long, ulong` or enum of those (**not `sbyte`**) | exactly `n` bits | `n` in 1..type size. **Truncates; sign is lost** unless combined with `[ZigZagEncode]` (`BitCountFinder.cs:9-26`, `BitPackHelper.cs:70-88`) |
| `[BitCountFromRange(min, max)]` | `byte, short, ushort, int, uint` or enum of those (**not `sbyte`/`long`/`ulong`**) | `floor(log2(max-min)) + 1` bits, value stored as `v - min` | min/max are `int`; must fit the type (`BitCountFromRangeFinder.cs:9-35`, `BitPackHelper.cs:11-68`) |
| `[ZigZagEncode]` | signed int + `[BitCount]` | maps small negatives to small codes | invalid with unsigned or without BitCount (`ZigZagFinder.cs:10-45`) |
| `[VarInt(small, medium[, large, throwIfOver])]` | integer | 3 size classes, shorter code for small values | `FromBitCount.bN` helpers (`WeaverAttributes.cs:129-237`, `VariableIntPacker.cs:43-71`) |
| `[VarIntBlocks(blockSize)]` | integer | `blockSize` bits per block + continuation bit | (`VarIntBlocksPacker.cs`) |
| `[FloatPack(max, precision)]` or `(max, bitCount)` | `float` | signed, clamped to +-max, bits = `floor(log2(2*max/precision)) + 1` (1..30) | rounds to nearest (`FloatPacker.cs:55, 98-103, 128-138`; `BitHelper.cs:58-62`; `FloatPackerFinder.cs:21-50`) |
| `[Vector2Pack]` / `[Vector3Pack(xMax,yMax,zMax, precision \| bitCount ...)]` | `Vector2/3` | independent `FloatPacker` per axis | (`WeaverAttributes.cs:94-113`, `Vector3Packer.cs:35-59`) |
| `[QuaternionPack(bitPerElement = 9)]` | `Quaternion` | `3*bits + 2` (largest component dropped) | (`WeaverAttributes.cs:118-121`, `QuaternionPacker.cs:68-75`) |

Run-time packers for manual use: `FloatPacker`, `Vector2Packer`, `Vector3Packer`, `QuaternionPacker` (`Default9`, `Default10`), `VarIntPacker`, `VarIntBlocksPacker`, `AnglePacker`, `VarFloatPacker`, `VarVector3Packer`, `VarDoublePacker` in `Assets/Mirage/Runtime/Serialization/Packers/`. Packer instances for attributes are `static` fields created in the declaring type's static constructor by the weaver (`FloatPackerFinder.cs:64-92`).

Caveats:
- `FloatPack`/`Vector3Pack` clamp silently; `BitCount` truncates silently. Out-of-range data is clamped/aliased, not an error.
- Quantization is lossy and **both sides see the quantized value only after a round trip**. If client prediction uses full-precision values and the server's state is quantized, compare/reconcile with a tolerance or quantize predicted state the same way (`FloatPacker.Pack` -> `Unpack`).
- Packers use `float` math (`value * multiplier`, `Mathf.RoundToInt`); results are deterministic per platform but cross-platform bit-exactness is not guaranteed (Inferred).
- Bit-packed fields are not byte aligned; only `ArraySegment`/raw-byte members re-align.
- `[MaxLength(n)]` (**main only**, `CustomAttributes.cs:296-310`) caps a string or collection (array, `List<T>`, `ArraySegment<T>`, `Dictionary`, span) on both write and read: exceeding it throws `SerializationLimitException`, which `MessageHandler` and `RpcHandler` charge at error cost 100 (`MessageHandler.cs:149-152, 165-168`; `RpcHandler.cs:185-191`). It cannot be combined with another packing attribute on the same member (`ValueSerializerFinder.cs:124-130`). Useful on a `PlayerInput[]` RPC parameter so a client cannot make the server allocate a huge array.

## 5. Pooling

- `NetworkWriterPool.GetWriter()` -> `PooledNetworkWriter`; call `.Release()` or `using` (`IDisposable`) to return it. Pool defaults: start 5, max 100 (`NetworkWriterPool.cs:32-53`). `ToArraySegment()` points into the pooled buffer: **valid only until the writer is released/reused**; all Mirage senders copy into connection batch buffers synchronously, so releasing right after `Send` is safe (`NetworkPlayer.cs:246-255`).
- Weaver RPC stubs take a pooled writer and release it after sending (`ServerRpcProcessor.cs:67-90`).
- `NetworkReaderPool.GetReader(segment, locator)` -> `PooledNetworkReader`; dispose to return (`NetworkReaderPool.cs:14-66, 90-125`). Each `Reset` allocates a `GCHandle` to pin the array (`NetworkReader.cs:145`) - cheap but not free per message.
- Payload `ArraySegment`s received in `RpcMessage`/`UpdateVarsMessage`/`SpawnMessage` alias the receive buffer; copy if you keep them past the handler (Mirage copies for async spawn, `ClientObjectManager.cs:667-676`).
- Classes in messages allocate per read; use structs.

## 6. Recipe: efficient `PlayerInput` and `PlayerState`

```csharp
[Flags] public enum Buttons : ushort { Jump = 1, Fire = 2, Reload = 4, Crouch = 8 }  // 16 bits

[NetworkMessage]                                   // optional if used as RPC param / SyncVar / Send<T>
public struct PlayerInput : IEquatable<PlayerInput>
{
    public ushort Tick;                            // 16 bits, wraps (~18 min @ 60 Hz): compare with modular math
    [BitCountFromRange(-100, 100)] public int MoveX;  // 8 bits  (store sticks as ints)
    [BitCountFromRange(-100, 100)] public int MoveY;  // 8 bits
    [FloatPack(180f, 0.1f)] public float Yaw;      // 12 bits  (floor(log2(3600))+1)
    [FloatPack(90f, 0.1f)]  public float Pitch;    // 11 bits  (floor(log2(1800))+1)
    public Buttons Buttons;                        // 16 bits
    // total 71 bits ~ 9 bytes  (default encoding would be ~21 bytes)
    public bool Equals(PlayerInput o) => Tick == o.Tick /* && ... */;
}

public struct PlayerState
{
    public ushort Tick;                            // last processed input tick
    [Vector3Pack(512f, 64f, 512f, 0.005f)] public Vector3 Position;  // 18+15+18 = 51 bits
    [Vector3Pack(20f, 20f, 20f, 0.01f)]    public Vector3 Velocity;  // 12*3 = 36 bits
    [FloatPack(180f, 0.1f)] public float Yaw;      // 12 bits
    [BitCount(8)] public byte Flags;               // grounded, crouched, ...
    // total ~ 134 bits ~ 17 bytes  (default encoding: ~35 bytes)
}
```

(Bit counts follow `BitHelper.BitCount(max, precision, signed)` = `floor(log2(2*max/precision)) + 1`, `BitHelper.cs:58-62`, and `BitCountFromRange` = bits needed for `max-min`, `BitCountFromRangeFinder.cs:28`; computed by hand, not by running the weaver.)

For client->server input bundles, an array parameter is legal and cheap: `[ServerRpc(channel = Channel.Unreliable)] void SendInputs(PlayerInput[] lastThree)` costs `1 (count) + 3 * 9` bytes. Arrays of structs go through `WriteArray<T>` (`CollectionExtensions.cs:103-121`). Redundant inputs in one unreliable packet are the usual substitute for reliability.

Further reduction ideas that need user code (not provided): delta vs. previous input/state, per-field dirty masks, tick as delta from an acked tick, shared quantization config across structs.

## 7. Gotchas

1. Default `int`-family encodings are variable-length: a `uint tick` is 1-5 bytes, a negative `int` is zigzagged. Use `ushort`/`[BitCount]` for fixed width.
2. Default `Quaternion` is lossy; default `Vector3` is 12 bytes.
3. Properties and private fields in structs are **not serialized** - a silent source of desync.
4. `struct` SyncVars: implement `IEquatable<T>` (default equality is reflection-based and boxes).
5. Any change to field order/attributes must match on both sides; build mismatch -> barrier/`EndOfStream` exceptions (`NetworkIdentity.cs:948-960`).
6. Strings are capped at 1300 bytes by default (`StringExtensions.cs:9-28`).
7. Out-of-range packed values are clamped/aliased silently.
