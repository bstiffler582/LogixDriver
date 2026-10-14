# 6. Errors & Extensibility

[← Concurrency & Caching](05-concurrency-and-caching.md) · [Index](README.md)

---

## Where errors come from

| Origin | Exception | Example |
|--------|-----------|---------|
| Path syntax (`TagPath.Parse`) | `ArgumentException` | `"MyUDT."`, `"Arr[x]"`, `"Arr[1"` |
| Path meaning (`ResolvePathAsync`) | `ArgumentException` | member access on an array, too many indices, indexing into a BOOL-array element, a path that is only a program name |
| Lookup (`ResolvePathAsync`, `TagList.Find`) | `KeyNotFoundException` | unknown tag, unknown program, unknown member, index out of bounds |
| Native operation (`TagChannel.RunNativeAsync`) | `NativeTagException` (`TagName`, `Status`, inner) | timeouts, NotFound, connection failures |
| Encoding a write (`WriteTagBuffer`) | `ArgumentException`, `OverflowException`, `FormatException`, `InvalidCastException` | missing structure member, wrong element count, value too large, string over capacity |
| Decoding a read (`ResolveValue`) | `NotSupportedException` | a type the driver can't represent (a size-0 placeholder `PrimitiveType`) |
| Shutdown / connection loss | `OperationCanceledException` | flushed while waiting for a slot |
| Using a disposed driver | `ObjectDisposedException` | |

Every native failure is wrapped by `TagChannel.RunNativeAsync`, so callers see **only** `NativeTagException` from the transport layer. It carries the tag's `Status` **at the time of failure**.

## What callers of `Driver` actually see

| | `ReadTagValueAsync` | `WriteTagValueAsync` | `LoadTagsAsync` | `TryConnectAsync` |
|-|---------------------|----------------------|-----------------|-------------------|
| Disconnected (before the call) | `null` | returns | **throws** `NativeTagException` (not gated) | `false` |
| Flushed or cancelled | `null` | returns | throws | — |
| `NativeTagException.IsConnectionError` | `null` + probe requested | returns + probe requested | throws | `false` |
| Other `NativeTagException` | **throws** | **throws** | throws | `false` |
| Resolution / encoding errors | **throws** | **throws** (only the offending caller) | throws | — |

What follows from this table:

- **`null` from a read means "no value right now"** (disconnected or connection trouble). Invalid paths always throw, so they can't be mistaken for a temporary outage.
- **Writes don't report connection failures.** If you need to confirm a write, check `IsConnected`, subscribe to `ConnectionStateChanged`, or read the value back.
- **`LoadTagsAsync` isn't gated by `IsConnected`**, and it doesn't trigger `RequestProbe`. Call it after a successful `TryConnectAsync` and handle its exceptions.
- **Failed metadata reads are never cached** ([`AsyncCache`](05-concurrency-and-caching.md#asynccachetkey-tvalue)). A resolve that failed during an outage is simply retried on the next call.

### `NativeTagException.IsConnectionError`

```csharp
Status is ErrorBadConnection or ErrorTimeout or ErrorWinsock or Pending
|| InnerException?.Message == "ErrorTimeout"
```

The second condition exists because libplctag has been observed throwing a timeout while the tag's status reports something unrelated.

A connection error doesn't set `IsConnected = false` directly. It asks the monitor to probe, and the probe decides; see [Connection Monitoring](04-connection-monitoring.md#requestprobe).

---

## Custom value resolvers

`DefaultTagValueResolver` produces plain CLR objects: primitives, `string`, `List<object>` and `Dictionary<string, object>`. A host application with its own value model can plug in a different representation, as the TcHmi extension does with its own value type:

```csharp
var driver = Driver.Create(target, new MyValueResolver());
```

### Contract

[`ITagValueResolver`](../LogixDriver/Tags/Value/ITagValueResolver.cs):

```csharp
object ResolveValue(byte[] buffer, TypeRef type, int offset = 0, int bitOffset = 0);
void WriteTagBuffer(byte[] buffer, TypeRef type, object value, int offset = 0, int bitOffset = 0);
```

- `offset` is the value's byte offset within the buffer. `bitOffset` is the bit within that byte, and is only non-zero for BOOLs (structure members and bit-array elements). It can be **8 or more** for BOOL-array elements; the built-in helpers handle that.
- The driver always calls with `offset = 0` and `bitOffset = ResolvedTag.BitOffset`. Recursion, with member offsets and array element positions, is up to the resolver.
- **`ResolveValue` must not modify `buffer`.** It is shared with every caller that joined the same read.
- `WriteTagBuffer` receives the handle's current buffer. Write only the bytes the value covers; everything else must keep its value.
- `type` always has its closure resolved, so every `ElementType` along the way is non-null.

### `TagValueResolverBase<T>`

[`TagValueResolver.cs`](../LogixDriver/Tags/Value/TagValueResolver.cs) gives you a typed base and the hard parts:

| Member | Use |
|--------|-----|
| `abstract T ResolveValue(...)` / `abstract void WriteTagBuffer(..., T value, ...)` | your implementation |
| `Elements(array, offset, bitOffset)` | `(TypeRef, Offset, BitOffset)` for each index of the first dimension. Handles strides, multi-dim nesting and bit packing. |
| `Resolved(type)` | `type.ElementType`, or throws if it hasn't been resolved |
| `ReadPrimitive` / `WritePrimitive` | every supported atomic type, little-endian, BOOL bit handling, checked `Convert` |
| `ReadString` / `WriteString` | `LEN` + Latin-1 `DATA`, with clamping and capacity checks |

The non-generic `ITagValueResolver` members are implemented explicitly and forward to the typed ones. Writes cast the incoming `object` to `T`, so `driver.WriteTagValueAsync(path, value)` must be given a `T`, or it throws `InvalidCastException`.

### Skeleton

`MyValue` stands for the host's value type.

```csharp
public sealed class MyValueResolver : TagValueResolverBase<MyValue>
{
    public override MyValue ResolveValue(byte[] buffer, TypeRef type, int offset = 0, int bitOffset = 0)
    {
        if (type.IsArray)
            return MyValue.Array(Elements(type, offset, bitOffset)
                .Select(e => ResolveValue(buffer, e.Type, e.Offset, e.BitOffset)));

        return Resolved(type) switch
        {
            PrimitiveType p => MyValue.Scalar(ReadPrimitive(buffer, p, offset, bitOffset)),
            StringType s    => MyValue.Scalar(ReadString(buffer, s, offset)),
            StructType st   => MyValue.Object(st.Members.ToDictionary(
                                   m => m.Name,
                                   m => ResolveValue(buffer, m.Type, offset + m.Offset, m.BitOffset))),
            var other       => throw new NotSupportedException($"Type {other.Name} can't be read."),
        };
    }

    public override void WriteTagBuffer(byte[] buffer, TypeRef type, MyValue value, int offset = 0, int bitOffset = 0)
    {
        if (type.IsArray)
        {
            var items = value.AsArray();
            if (items.Count != type.Dims[0])
                throw new ArgumentException($"Expected {type.Dims[0]} elements.");
            var i = 0;
            foreach (var e in Elements(type, offset, bitOffset))
                WriteTagBuffer(buffer, e.Type, items[i++], e.Offset, e.BitOffset);
            return;
        }

        switch (Resolved(type))
        {
            case PrimitiveType p: WritePrimitive(buffer, p, value.AsScalar(), offset, bitOffset); break;
            case StringType s:    WriteString(buffer, s, value.AsString(), offset); break;
            case StructType st:
                foreach (var m in st.Members)
                    WriteTagBuffer(buffer, m.Type, value.Member(m.Name), offset + m.Offset, m.BitOffset);
                break;
            case var other: throw new NotSupportedException($"Type {other.Name} can't be written.");
        }
    }
}
```

When writing a structure, either require every member, as the default resolver does, or deliberately leave missing members as they are in the buffer. Be aware that the buffer holds whatever the handle last read, which may be stale. See [Writing → whole structure](03-writing-tags.md#a-whole-structure-myudt).

---

## Walking types for browsing

Everything a browser UI needs is on the public model:

```csharp
void Walk(string path, TypeRef type)
{
    Console.WriteLine($"{path} : {type}");          // "Recipes : Recipe[10]"
    if (type.IsArray || type.ElementType is not StructType st)
        return;                                      // ElementType null → not loaded yet
    foreach (var m in st.Members)
        Walk($"{path}.{m.Name}", m.Type);
}

await driver.LoadTagsAsync(["Program:HMI"]);
foreach (var node in driver.Tags.GetLoadedTags())
    if (node is TagInfo tag) Walk(tag.Path, tag.Type);
```

- `TypeRef.ToString()` renders `Name[dims]`, falling back to the code name (`Struct(0x123)`) before resolution.
- BOOL arrays show their size in bits (`BOOL[64]`), not words.
- Hidden BOOL host members never appear.
- `StringType` has no members to walk. Treat it as a leaf.

---

## Test seams

The transport is behind two interfaces, so everything above libplctag can be tested without a controller:

| Seam | Production | Tests |
|------|------------|-------|
| `ITagFactory.Create(name, count)` → `INativeTag` | `TagFactory` → `LibPlcTag` | `FakeNativeTag` (`Fakes/`): in-memory data, configurable delay, failure and status, operation counters. Created by a private `FakeTagFactory` in `DriverTests`. |
| `IRawTagReader.ReadRawAsync` | `TagChannel` | `FakePlc`: canned `@tags` / `@udt` responses, counts metadata reads |

`Driver.Create(Target, ITagFactory, ITagValueResolver?)` is `internal` and visible to `LogixDriver.Tests` through `InternalsVisibleTo`. `DriverTests` runs the real driver, channel, sessions and directory over fake tags.

[`SampleController`](../LogixDriver.Tests/Fakes/SampleController.cs) builds a small controller that covers the tricky cases: a STRING, a BOOL array reported in words, arrays of UDTs, a 2-D UDT array, a program scope, hidden BOOL hosts, nested UDTs and a BOOL-array member. New behaviour should get a test against it.
