# LogixDriver

A .NET library for communicating with Allen-Bradley/Rockwell ControlLogix PLCs over EtherNet/IP. Built on top of [libplctag](https://github.com/libplctag/libplctag.NET), it provides a simple interface for connecting to a controller, browsing its tag database, and reading or writing tag values — including UDTs and arrays.

## Installation

- Download the latest [release](https://github.com/bstiffler582/LogixDriver/releases)
- Put it in a local nuget package source
- Add to your project

```
dotnet add package LogixDriver
```

## Usage

### Browse the controller tag database

`LoadTagsAsync` reads the controller's tag list and fully resolves the data types of the tags you name, so they can be browsed. A program name loads all of that program's tags; no filter resolves everything.

```csharp
using Logix.Driver;
using Logix.Tags;

var target = new Target("MyPLC", "192.168.1.10", "1,0");

using var driver = Driver.Create(target);

bool isConnected = await driver.TryConnectAsync();
// await driver.LoadTagsAsync(); // resolves every tag and program

// resolve two programs for browsing
await driver.LoadTagsAsync([ "Program:HMI_A", "Program:HMI_B" ]);

// the controller's top level, in controller order: controller tags and programs
foreach (var node in driver.Tags.GetLoadedTags())
{
    switch (node)
    {
        case TagInfo tag:
            Print(tag);
            break;
        case ProgramInfo { IsLoaded: true } program:
            foreach (var tag in program.Tags!)
                Print(tag);
            break;
        case ProgramInfo program:
            Console.WriteLine($"{program.Name}  (not loaded)");
            break;
    }
}

static void Print(TagInfo tag)
{
    Console.WriteLine($"{tag.Path}  [{tag.Type}]");

    // walk a resolved type; ElementType is null where the type hasn't been loaded
    if (tag.Type.ElementType is StructType structType)
        foreach (var member in structType.Members)
            Console.WriteLine($"{tag.Path}.{member.Name}  [{member.Type}]");
}
```

Data types are resolved once per type, not per tag: every tag and member of a given UDT shares one `StructType`, read from the controller once. A `TypeRef` whose `ElementType` is still null belongs to a type that hasn't been read yet.

For large programs, or code with deeply nested types, resolving *everything* takes one read per distinct type. A filter limits that to what you need:

```csharp
await driver.LoadTagsAsync([ "Program:HMI_A" ]);
// lists all controller tags and programs (names and type ids only)
// resolves every type used by Program:HMI_A
```
```csharp
await driver.LoadTagsAsync([ "Program:HMI_A.MyHmiUdt" ]);
// lists all controller tags and programs, and Program:HMI_A's tags
// resolves only the types MyHmiUdt contains
```

Tags can be read and written with no preceding `LoadTags`/`LoadTagsAsync`. A path resolves on first use, reading only the types along it (plus the target's own types), and the result is cached.

### Read/write a tag values

```csharp
using System.Text.Json;
using Logix.Driver;

// Define the target controller (name, gateway IP, backplane path)
var target = new Target("MyPLC", "192.168.1.10", "1,0");

var driver = Driver.Create(target);
driver.ConnectionStateChanged += (_, e) => Console.WriteLine("Connected:" + e.IsConnected);

if (await driver.TryConnectAsync())
{
    // display controller model and version
    Console.WriteLine(driver.ControllerInfo);

    // read tag value
    // returns Dictionary<string, object> for complex types
    var value = await driver.ReadTagValueAsync("MyUdtInstance");
    Console.WriteLine(JsonSerializer.Serialize(value));
    // { "bTest": false, "fTest": 42.0, "arrTest": [0, 1, 2, 3, 4] }
    Console.WriteLine(driver.ReadTagValue("MyUdtInstance.fTest"));
    // 42.0

    // write structure member
    await driver.WriteTagValueAsync("MyUdtInstance.fTest", 3.141);
    
    // write whole structure
    var value = new Dictionary<string, object>();
    value.Add("bTest", true);
    value.Add("fTest", 12.34);
    value.Add("arrTest", new int[] { 4, 3, 2, 1, 0 });
    await driver.WriteTagValueAsync("MyUdtInstance", value);
}
```

By default, complex types are resolved to `Dictionary<string, object>` for UDTs and `List<object>` for arrays. It is possible to create your own value resolver by inheriting from the `TagResolverBase` class and injecting it into the `Driver.Create` method.