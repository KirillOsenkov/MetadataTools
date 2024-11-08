# MetadataTools

Included features and dotnet global tools:
 * **BinaryCompatChecker** - (https://nuget.org/packages/checkbinarycompat) a tool that given a set of managed assemblies will detect binary incompatibilities, such as types missing from referenced assemblies, missing methods, etc. It will also check .exe.config files (app.config) and validate all binding redirects, and report missing binding redirects where necessary.
 * **lbi** - (https://nuget.org/packages/lbi) - a tool to list assemblies and their versions and other information, optionally grouping by assembly version. Similar to `dir /s` but also prints version, target framework, etc.
 * **RefDump** - (https://nuget.org/packages/refdump) given an assembly prints a list of all types from all referenced assemblies used by the current assembly. Finds all assemblies in a directory (recursively if needed) that reference a given one.
![image](https://user-images.githubusercontent.com/679326/119587059-bed59a80-bd82-11eb-9ed1-9ace56ae13e5.png)

 * **Pdb**
   - extract .pdb information from a .dll/.exe debug directory (Pdb Guid, age, path to .pdb)
   - download the .pdb from symbol server
   - determine if a .dll matches a .pdb
   - find a matching .pdb in a folder for a given .dll
 * **PEFile.cs** - a single .cs file to very quickly determine if a .dll or .exe represents a managed assembly.
 * **ReadAssemblyVersion** - quickly read an assembly's version
 * **LargeAddressAware** - sets the LargeAddressAware bit in a 32-bit executable to unlock 4GB memory space
 * **MVID** - Quickly read an assembly's MVID

## Reading an assembly MVID
Mono.Cecil stripped down to a single file to read assembly MVID without dependencies.

Here are the various ways to retrieve the MVID of an assembly:

## ImageReader
Just embed this file:
https://github.com/KirillOsenkov/MetadataTools/blob/master/src/PEFile/ImageReader.cs
```
var mvid = ImageReader.ReadAssemblyMvid(filePath);
```
Cold run on a warm file: 7ms

Or see this sample from Roslyn:
https://github.com/dotnet/roslyn/blob/1c98fe1fc6ef122cb3bb436aa90a0ea729fdcf37/src/Compilers/Core/MSBuildTask/MvidReader.cs#L13

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

Sample in Roslyn:
https://github.com/dotnet/roslyn/blob/1c98fe1fc6ef122cb3bb436aa90a0ea729fdcf37/src/Compilers/Core/Portable/AssemblyUtilities.cs#L84

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
