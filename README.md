# MetadataTools

A collection of small command-line tools for inspecting .NET assemblies on disk: their versions, references, signatures, debug info, MVIDs, and binary compatibility. Most are published as dotnet global tools.

> For LLM agents: see [`llms.txt`](llms.txt) for a dense, token-conscious reference that teaches an agent the whole toolset in one file.

## Dotnet global tools

| Tool | NuGet | Purpose |
|------|-------|---------|
| [**checkbinarycompat**](src/BinaryCompatChecker/readme.md) | [checkbinarycompat](https://nuget.org/packages/checkbinarycompat) | Detect binary incompatibilities across a set of assemblies: missing types/members/assemblies, version mismatches, facade/interop issues, broken `app.config` binding redirects. Compares against a baseline `.txt` and returns a non-zero exit code on change &mdash; designed to plug into CI builds. |
| [**lbi**](src/lbi/readme.md) | [lbi](https://nuget.org/packages/lbi) | List binary info for assemblies in a tree: AssemblyName, AssemblyVersion, FileVersion, InformationalVersion, TargetFramework, platform, MVID, SHA1, strong-name signing, Authenticode signature, file size. Default output groups identical files by AssemblyName then SHA1; `-l` switches to a flat directory-listing format. |
| [**refdump**](src/RefDump/readme.md) | [refdump](https://nuget.org/packages/refdump) | Print all assembly references for one or more `.dll`/`.exe`. Optionally list types/members consumed per reference, filter to a specific reference name (e.g. find every consumer of `Newtonsoft.Json`), or emit a Graphviz reference graph. |
| **pdb** | [pdb](https://nuget.org/packages/pdb) | Print `.pdb` info from a `.dll`'s debug directory (Pdb Guid / Age / path / Stamp, embedded-pdb flag, SourceLink). Verify a `.dll` matches a `.pdb`. Download `.pdb`s from a symbol server. Find the matching `.pdb` for a `.dll` in a directory tree. Handles portable and native pdbs. |

Install / update any of them (`dotnet tool update -g` installs if missing, upgrades to latest if present):

```
dotnet tool update -g checkbinarycompat
dotnet tool update -g lbi
dotnet tool update -g refdump
dotnet tool update -g pdb
```

![image](https://user-images.githubusercontent.com/679326/119587059-bed59a80-bd82-11eb-9ed1-9ace56ae13e5.png)

## Other utilities (in this repo)

 * **FakeSign** &mdash; toggle the `StrongNameSigned` bit in the COR header of a managed assembly. `FakeSign foo.dll` marks a delay-signed assembly as signed so the runtime will load it without a real key; `FakeSign foo.dll -u` un-signs; add `-f` to force when the assembly is already in the requested state.
 * **Corflags** &mdash; clear the `Prefers32Bit` / `Requires32Bit` flags in the COR header: `corflags foo.exe /32BITPREF-`.
 * **DumpAssembly** &mdash; dump types/methods of an assembly using Mono.Cecil.
 * **DumpModules** &mdash; list all managed modules loaded in a process dump.
 * **mvid** &mdash; print the MVID of an assembly using a minimal embedded PE reader (~7 ms cold).
 * **SRMReader** &mdash; sample usage of `System.Reflection.Metadata`.
 * **PEFile** / **PETree** &mdash; sample PE-format inspection.
 * **PEFile.cs**, **ImageReader.cs** &mdash; single-file helpers that can be dropped into any project to quickly determine if a file is a managed assembly, or to read its MVID, with no dependencies.
 * **LargeAddressAware** &mdash; set the `LargeAddressAware` bit in a 32-bit executable to unlock the 4 GB user-mode address space.
 * **ReadAssemblyVersion** &mdash; quickly read an assembly's version.

## Common workflows

| Question | Command |
|----------|---------|
| What versions of `XYZ.dll` exist under this directory tree? | `lbi XYZ.dll` |
| Which assemblies in this folder reference `Newtonsoft.Json`, and what types/members do they use? | `refdump *.dll -s -a:Newtonsoft.Json -t -m` (drop `-t -m` for just the consumer list) |
| What types from `System.Collections.Immutable` does `Foo.dll` use? | `refdump Foo.dll -t -a:System.Collections.Immutable` |
| Does my `bin\Debug\` directory have binding redirect issues or missing types/members across its references? | `cd bin\Debug && checkbinarycompat` |
| Is the report from the last `checkbinarycompat` run different from the checked-in baseline? | `checkbinarycompat` returns exit code `1` if any line differs &mdash; wire it into CI. |
| Validate many startup binaries that share one output directory | `checkbinarycompat config.json` &mdash; see [batch mode](src/BinaryCompatChecker/readme.md#worked-example-multi-closure-on-one-output-directory). |
| Find unreferenced assemblies in a directory | `refdump *.dll -g` then read `AssemblyGraph.txt` (or open in MSBuild Structured Log Viewer's graph viewer). |
| **Where did `X.dll` get copied from? Which NuGet package shipped it?** | Build with `msbuild /bl` (or capture the CI binlog), then ask `binlogmcp`: `$copy <full path to X.dll>` and `$nuget X.dll`. See [Binlogs](#binlogs--how-did-this-dll-get-here-questions) below. |
| **What's the transitive NuGet dependency chain that brought in version `A` of `Foo`?** | Binlog &mdash; ask `binlogmcp`: `$nuget Foo A` (or `$nuget project(MyApp.csproj) Foo`). |
| Does this `.dll` have a matching `.pdb` on this symbol server? | `pdb Foo.dll http://symbol.server/` |
| What's the MVID of this assembly? | `mvid foo.dll` (or `lbi foo.dll -mvid`) |
| Is this assembly strong-name signed? | `lbi foo.dll -sn` |

## Binlogs &mdash; "how did this dll get here?" questions

The tools above answer **what** is on disk. They cannot answer **how** it got there &mdash; which `Copy` task produced this file, which NuGet package shipped it, what transitive dependency chain pulled in version `13.0.3` instead of `12.0.0`. Those questions require an MSBuild binary log (`.binlog`):

* Capture one by adding `/bl` to your build: `msbuild MySolution.sln /bl` or `dotnet build MySolution.sln /bl`. It costs negligible build time and writes `msbuild.binlog` next to the build.
* Inspect interactively with the MSBuild Structured Log Viewer ([msbuildlog.com](https://msbuildlog.com), [github.com/KirillOsenkov/MSBuildStructuredLog](https://github.com/KirillOsenkov/MSBuildStructuredLog)).
* From an LLM agent, use the [`binlogmcp`](https://github.com/KirillOsenkov/MSBuildStructuredLog) MCP server (must be configured in your agent host). The two killer queries:
  * **`$copy <full path to X.dll>`** &mdash; explains where every copy of `X.dll` came from (producing task, project, NuGet source, content item, project reference).
  * **`$nuget <package> [version]`** &mdash; full NuGet dependency graph; traces which `<PackageReference>` or transitive chain forced a specific version.

Typical use: `lbi`/`refdump`/`checkbinarycompat` identify *what* is wrong on disk; the binlog explains *why* the build put it there. See [`llms.txt`](llms.txt#binlogs) for the full playbook.

---

# Library API: reading an assembly MVID from your own code

> The sections below document **C# library APIs** for retrieving an assembly's MVID programmatically, not the CLI tools above. They're aimed at developers building their own tools. If you just want to print MVIDs on the command line, use `lbi -mvid` and stop here.

Mono.Cecil stripped down to a single file to read an assembly's MVID without dependencies. Here are the various ways to retrieve the MVID of an assembly:

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
