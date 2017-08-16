# MetadataTools

Included features:
 * BinaryCompatChecker - a tool that given a set of managed assemblies will detect binary incompatibilities, such as types missing from referenced assemblies, missing methods, etc.
 * IsManagedAssembly.cs - a single .cs file to very quickly determine if a .dll or .exe represents a managed assembly.
 * ReadAssemblyVersion - quickly read an assembly's version
 * LargeAddressAware - sets the LargeAddressAware bit in a 32-bit executable to unlock 4GB memory space
 * Quickly read an assembly's MVID

## Reading an assembly MVID
Mono.Cecil stripped down to a single file to read assembly MVID without dependencies.

Here are the various ways to retrieve the MVID of an assembly:

## ImageReader
Just embed this file:
https://github.com/KirillOsenkov/MetadataTools/blob/master/PEFile/ImageReader.cs
```
var mvid = ImageReader.ReadAssemblyMvid(filePath);
```
Cold run on a warm file: 7ms

## Mono.Cecil
https://www.nuget.org/packages/Mono.Cecil
```
var module = Mono.Cecil.ModuleDefinition.ReadModule(filePath);
var mvid = module.Mvid;
```
Cold run on a warm file: 16ms

## Reflection
This has the downside that it loads the assembly:
```
var assemblyName = AssemblyName.GetAssemblyName(filePath);
var assembly = Assembly.Load(assemblyName);
var mvid = assembly.ManifestModule.ModuleVersionId;
```
Cold run on a warm file: 33ms

## System.Reflection.Metadata
https://www.nuget.org/packages/System.Reflection.Metadata
```
using (var stream = File.OpenRead(filePath))
{
    PEReader reader = new PEReader(stream);
    var metadataReader = reader.GetMetadataReader();
    var mvidHandle = metadataReader.GetModuleDefinition().Mvid;
    var mvid = metadataReader.GetGuid(mvidHandle);
}
```
Cold run on a warm file: 76ms

BenchmarkDotNet paints quite a different performance picture:

```
               Method |        Mean |    StdDev |
--------------------- |------------ |---------- |
  ReadUsingReflection | 208.2023 us | 0.4036 us |
         ReadUsingSRM |  98.9620 us | 0.3674 us |
       ReadUsingCecil | 153.1947 us | 0.3085 us |
 ReadUsingImageReader | 147.0230 us | 1.2618 us |

```
