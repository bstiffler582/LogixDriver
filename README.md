# LogixDriver

A .NET library for communicating with Allen-Bradley/Rockwell ControlLogix PLCs over EtherNet/IP. Built on top of [libplctag](https://github.com/libplctag/libplctag.NET), it provides a simple interface for connecting to a controller, browsing its tag database, and reading or writing tag values — including UDTs and arrays.

## Installation

Add the NuGet package to your project:

```
dotnet add package LogixDriver
```

## Usage

### Browse the controller tag database

`LoadTagsAsync` queries the controller for its full tag list. An optional filter limits loading to tags whose names start with one of the provided prefixes.

```csharp
using Logix.Driver;

var target = new Target("MyPLC", "192.168.1.10", "1,0");

using var driver = Driver.Create(target);

// await driver.LoadTagsAsync(); // loads all tag definitions

// load filtered tag definitions
await driver.LoadTagsAsync([ "Program:HMI_A", "Program:HMI_B" ]);

// output flat map of tag paths and types
foreach (var (path, definition) in driver.GetTagDefinitionsFlat())
    Console.WriteLine($"{path}  [{definition.TypeName}]");

// gets loaded root tags, hierarchical representation
var definitions = driver.GetTagDefinitions();

// access child members
var tagDef = definitions.First();
foreach (var member in tagDef.Children!)
    Console.WriteLine(member.Name);

```

For large programs, or code with deeply nested types, loading *all* tag metadata will require many successive reads. This is because all tag instance definitions are recursively "deep" resolved unless a filter is provided.

For instance:
```csharp
await driver.LoadTagsAsync([ "Program:HMI_A" ]);
// Shallow resolves all controller tag definitions
// deep resolves all defintions within Program:HMI_A
```
```csharp
await driver.LoadTagsAsync([ "Program:HMI_A.MyHmiUdt" ]);
// Shallow resolves all controller tags
// shallow resolves all tags in Program:HMI_A
// deep resolves only the MyHmiUdt definition
```


Tags can still be read/written with no preceding call to `LoadTags`/`LoadTagsAsync`. Their definitions will be progressively resolved on demand.

### Read/write a tag values

```csharp
using System.Text.Json;
using Logix.Driver;

// Define the target controller (name, gateway IP, backplane path)
var target = new Target("MyPLC", "192.168.1.10", "1,0");

var driver = Driver.Create(target);

if (driver.TryConnect())
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