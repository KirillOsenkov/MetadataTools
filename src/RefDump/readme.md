# RefDump

`refdump` is a dotnet global tool that prints the assembly references of one or more managed `.dll`/`.exe` files. It can drill into the actual types and members consumed per reference, filter to a specific referenced assembly (e.g. "show me every consumer of `Newtonsoft.Json` in this tree"), and emit a Graphviz reference graph of the input set.

## Install

```
dotnet tool update -g refdump
```

`dotnet tool update` installs if missing, upgrades to latest if present.

## Source

https://github.com/KirillOsenkov/MetadataTools/tree/main/src/RefDump

## NuGet

https://www.nuget.org/packages/refdump

## Usage

```
refdump <file-or-pattern> [-a:<refname>] [-t] [-m] [-s] [-g] [output.xml]
```

The input can be:

* a single `.dll` / `.exe` (`refdump Foo.dll`)
* a file mask (`refdump *.dll`)
* a mask with a directory (`refdump path\to\*.dll`)

If no arguments are passed, prints usage.

### Options

| Option | Meaning |
|--------|---------|
| `-a:<refname>` | Narrow output to references whose assembly name contains `<refname>` (substring match). Files with no matching reference are skipped. |
| `-t` | For each reference, list every type used from that reference. |
| `-m` | For each reference, list every member (method/field/property) used from that reference. Implies `-t`. |
| `-s` | When the input is a file mask such as `*.dll`, recurse into all subdirectories. |
| `-g` | Generate `AssemblyGraph.txt` in the current directory containing a Graphviz `digraph` of edges *between assemblies in the input set*. See [Opening the graph](#opening-the-graph) below. |
| `output.xml` | If an argument ends with `.xml` (and is not a switch), write a detailed XML report to that file in addition to console output. |
| `-?`, `-h`, `/?`, `/h` | Print usage. |

All switches also accept the `/` prefix (`/t`, `/m`, `/a:Foo`, etc.).

## The workhorse pattern

The single most useful invocation, the one to reach for first:

```
refdump *.dll -s -a:<TargetAssembly> -t -m
```

Walks cwd recursively, opens every `.dll`, narrows to references whose name contains `<TargetAssembly>`, and for each matching consumer prints the exact types and members consumed. Files with no matching reference are silently skipped, so the output stays dense.

It answers in one shot:

* **Who references `<TargetAssembly>`?** Each match prints the referencing assembly path and the exact `AssemblyReference.FullName` (including the version it was compiled against). The referenced assembly name is color-highlighted on the console.
* **What does each consumer actually use?** `-t` lists types, `-m` adds members (methods, fields, properties).

Variations on this pattern:

```
refdump *.dll -s -a:Newtonsoft.Json -t -m      THE workhorse: consumers of Newtonsoft.Json + what they use.
refdump *.dll -s -a:Newtonsoft.Json            Same, drop -t -m for just the consumer list.
refdump Foo.dll -a:Newtonsoft.Json -t -m       Drill into one file: what does Foo.dll use from Newtonsoft.Json?
refdump Foo.dll                                 List every reference of one assembly (default output).
refdump *.dll -s                               Every reference of every dll under cwd (no filter — high volume).
```

Everything else in this readme is a small variation on this pattern.

## Other examples

Show types used from `System.Collections.Immutable` by `Foo.dll`:

```
refdump Foo.dll -t -a:System.Collections.Immutable
```

Build a reference graph of the assemblies in `bin\Debug\` (only edges between assemblies in the input set are emitted):

```
cd bin\Debug
refdump *.dll -g
```

This writes `AssemblyGraph.txt` in standard Graphviz DOT format:

```
digraph G {
  "Microsoft.CodeAnalysis.CSharp" -> "Microsoft.CodeAnalysis"
  "Microsoft.CodeAnalysis.VisualBasic" -> "Microsoft.CodeAnalysis"
  ...
}
```

Edges connect only assemblies that are *both in the input set*. References to BCL / external assemblies don't appear in the graph &mdash; this is intentional so a graph of "my product" stays focused on internal coupling.

Emit a detailed XML report alongside the console output:

```
refdump Foo.dll -t -m report.xml
```

### Opening the graph

* **MSBuild Structured Log Viewer** has a built-in graph viewer that opens `.txt`/`.dot` files in DOT format directly. This is the recommended way to navigate large graphs interactively (zoom, search, click-to-highlight neighbours).
* **LLMs / agents** can read `AssemblyGraph.txt` as plain text and answer common topology questions directly:
  * *Find assemblies nothing references* &mdash; nodes that appear only on the left side of `->` and never on the right.
  * *Find leaves* &mdash; nodes that appear only on the right side of `->` and never on the left.
  * *Forward closure of X* &mdash; walk edges following `X -> ...`.
  * *Reverse closure (who references X)* &mdash; walk edges following `... -> X`.
* **Graphviz CLI** &mdash; render to SVG: `dot -Tsvg AssemblyGraph.txt -o graph.svg`.

### Finding unreferenced assemblies in a directory

```
cd bin\Debug
refdump *.dll -g
```

Read `AssemblyGraph.txt` (text or in the MSBuild Structured Log Viewer). Names that appear only on the **left** side of `->` (or not at all) and never on the **right** are not referenced by anything else in the input set &mdash; candidate dead code. Cross-check with `checkbinarycompat -closure:<startup>.exe` which lists `Unreferenced assembly: <path>` for files not reachable from a given startup binary.

### When you need to know *where the dll came from* (NuGet / Copy task / project reference)

`refdump` answers "who references what" at the metadata level. It does **not** know about your build &mdash; which NuGet package shipped a given `.dll`, which `Copy` task put it in `bin\Debug`, or what transitive dependency chain pulled it in. Those questions need an MSBuild binlog:

* Build with `msbuild /bl` (or `dotnet build /bl`) to get `msbuild.binlog`.
* Use the [MSBuild Structured Log Viewer](https://msbuildlog.com) or the [`binlogmcp`](https://github.com/KirillOsenkov/MSBuildStructuredLog) MCP server.
* Critical queries: `$copy <full path to X.dll>` (explains every copy of `X.dll`), `$nuget <package>` (NuGet dependency graph, version-aware).

Typical combined investigation: `refdump *.dll -s -a:Foo -t -m` (what's referenced and used on disk) followed by `binlogmcp`'s `$nuget Foo` query (what package shipped each copy of `Foo` and what dependency chain forced the resolved version).
