# Lbi (ListBinaryInfo)

`lbi` (list binary info) is a dotnet global tool that prints information about managed and unmanaged binaries in a directory tree. Think of it as `dir /s` for assemblies, with assembly-specific columns: AssemblyName, AssemblyVersion, FileVersion, InformationalVersion, TargetFramework, platform, MVID, SHA1, strong-name signing, Authenticode signature, file size.

## Install

```
dotnet tool update -g lbi
```

`dotnet tool update` installs if missing, upgrades to latest if present.

## Source

https://github.com/KirillOsenkov/MetadataTools/tree/main/src/lbi

## NuGet

https://www.nuget.org/packages/lbi

## Two output modes

`lbi` has two very different output modes:

* **Default (grouped)** &mdash; files are grouped by `AssemblyFullName`, then by `SHA1`. For each `SHA1` group, `lbi` prints the requested flag values once (because identical bytes have identical metadata) followed by every file path with that hash. This is the right mode for "I have 30 copies of `Newtonsoft.Json.dll` scattered across this tree, which ones are actually identical and which differ?". Default pattern: `*.dll;*.exe`.
* **Flat (`-l`)** &mdash; one file per row, like `dir /s`. Output is a comma-separated row whose columns depend on the requested flags. Default pattern in this mode: `*` (all files). Optionally write to a file: `-l:out.txt`.

## Usage

```
lbi [<pattern>]
    [-l[:<out.txt>]]
    [-d:<path>]*
    [-ed:<path>]*
    [-ef:<substring>]*
    [-nr]
    [-mo]
    [-sn]
    [-snv]
    [-ac]
    [-mvid]
    [-p]
    [-v]
    [-fv]
    [-fs]
    [-iv]
    [-tf]
    [@response.rsp]
```

### File-spec options

| Option | Meaning |
|--------|---------|
| `<pattern>` | File pattern, e.g. `*.dll` or `Foo*.exe`. Default `*.dll;*.exe`, or `*` when `-l` is set. |
| `-d:<path>` | Root directory to scan. Defaults to current directory. May be specified more than once to scan multiple directories. |
| `-ed:<path>` | Exclude a directory from the scan. May be specified more than once. |
| `-ef:<substring>` | Exclude files whose path contains this substring. May be specified more than once. |
| `-nr` | Non-recursive (current directory only). Recursive by default. |
| `-mo` | Managed-only: filter out files that are not managed assemblies. |
| `-l[:<out.txt>]` | Flat output mode (see above). Optionally write to a file. |
| `@response.rsp` | Read additional args from a response file, one arg per line. |

### Per-file info to print

These flags control which columns are collected and printed. None are on by default in flat mode; in grouped mode `AssemblyFullName` and `SHA1` are always printed and other flags are added per-group.

| Option | Meaning |
|--------|---------|
| `-v` | AssemblyVersion (`[assembly: AssemblyVersion]`). |
| `-fv` | FileVersion (Win32 file-version resource). |
| `-iv` | InformationalVersion (`[assembly: AssemblyInformationalVersion]`). |
| `-tf` | TargetFramework (`[assembly: TargetFramework]`). |
| `-p` | Platform / architecture (e.g. `AnyCPU`, `x86`, `x64`). |
| `-mvid` | Module Version Id (GUID). |
| `-fs` | File size in bytes. |
| `-sn` | Strong-name signing flag (`signed` / `delay-signed` / `public-signed`). |
| `-snv` | Validate the strong name using `sn.exe -vf` (Windows-only, slow &mdash; shells out per file). |
| `-ac` | Authenticode signature information (Windows-only). |

## Examples

```
lbi                                              # Group every .dll/.exe under cwd by assembly name + SHA1.
lbi foo.dll                                      # Group every file named foo.dll under cwd.
lbi *.exe -nr                                    # Group .exe files in cwd only (no recursion).
lbi *.dll -mo -v -fv -tf -mvid                   # Managed .dll only, show AssemblyVersion, FileVersion, TargetFramework, MVID.
lbi -sn -snv                                     # Show strong-name status + run sn.exe -vf to validate (Windows, slow).
lbi -d:sub\dir1 -d:sub\dir2 -ed:sub\dir2\obj -l:out.txt
                                                 # Flat listing of two dirs, exclude sub\dir2\obj, write to out.txt.
```

## Output format

In the **default grouped mode**, output looks like:

```
Newtonsoft.Json, Version=13.0.0.0, Culture=neutral, PublicKeyToken=30ad4fe6b2a6aeed
    SHA1: a1b2c3...  708976  net6.0
        bin\Debug\net6.0\Newtonsoft.Json.dll
        bin\Release\net6.0\Newtonsoft.Json.dll
    SHA1: d4e5f6...  709000  net8.0
        packages\Newtonsoft.Json.13.0.1\lib\net6.0\Newtonsoft.Json.dll
```

(The fields after `SHA1:` depend on which `-v`/`-fv`/`-tf`/etc. switches you requested.)

In **flat mode (`-l`)**, output is CSV-ish with one row per file:

```
relativePath[,fileSize][,FileVersion][,Version][,TargetFramework][,Signed][,Platform][,InfoVersion][,Mvid][,Authenticode]
```

Only columns whose flag was requested are emitted.

