# CheckBinaryCompat

`checkbinarycompat` is a dotnet global tool that inspects a set of .NET assemblies and finds potential compatibility issues.
It can run after a build, inspect the output, and generate a report or validate against a baseline.

It follows each assembly reference in each .dll. For each reference it checks that all the APIs expected by the referencing .dll are actually present in the referenced .dll. This is to detect binary mismatches at build time. This prevents runtime exceptions such as `MissingMethodException`, `ReflectionTypeLoadException`, etc.

In addition it also looks at the app.config file(s) if specified and does additional checking, such as verifying that the binding redirect ranges are consistent and the .dll version being redirected to is actually present.

It can also determine a closure of all transitive assembly references from a given set of "roots", and list assemblies not referenced in the closure (potentially unused).

## Install

```
dotnet tool update -g checkbinarycompat
```

`dotnet tool update` installs if missing, upgrades to latest if present.

## Sample use:

Run the tool in your output directory (such as bin\Debug\net472). You can specify `-s` to find all files recursively. Default is current directory only (non-recursive). You can specify one or more directories to look in instead of the current directory.
```
checkbinarycompat
```

It will output a file `BinaryCompatReport.txt` with the diagnostics it found. You can exclude files and directories to tune and trim the report file.
Pass `-l` to also output `BinaryCompatReport.Assemblies.txt` with the list of all assemblies analyzed, their versions and target frameworks.

## Source code:
https://github.com/KirillOsenkov/MetadataTools/tree/main/src/BinaryCompatChecker

## NuGet:
https://www.nuget.org/packages/checkbinarycompat

## Command line arguments

<details>
<summary>Full verbatim help text from <code>checkbinarycompat -?</code> (click to expand)</summary>

```
Usage: checkbinarycompat <file-spec>* <option>* @<response-file>*

Inspects assemblies and app.config files in a directory and reports potential issues.
Writes a report of possible issues to BinaryCompatReport.txt (sorted alphabetically).
File name/path can be customized using -out:<reportPath.txt>
If the file exists, it is used as a baseline.
If the report matches the baseline, it succeeds and returns exit code 0.
If the report doesn't match the baseline, the report is written and exit code is 1.
You can specify a separate baseline file using -baseline:<baselinePath.txt>
This way you can compare with the baseline and write the report to a different file.

File specs may be specified more than once. Each file spec is one of the following:

    * absolute directory path
    * directory relative to current directory
    * may include ** to indicate recursive subtree
    * may optionally end with:
        - a file name (a.dll)
        - a pattern such as *.dll
        - semicolon-separated patterns such as *.dll;*.exe;*.exe.config

When no file-specs are specified, uses the current directory
non-recursively. Pass -s for recursion.
When no patterns are specified, uses *.dll;*.exe;*.exe.config.

If you only specify -closure:app.exe without specifying other files or patterns,
it will check the app.exe.config (if exists) and the transitive closure of all
references of app.exe, but not the other files in the directory. You can specify
several -closure: arguments or several semicolon-separated files
such as -closure:app.exe;Dependency1.dll in order to give a hint that
Dependency1.dll is a runtime dependency and should be included in the closure.
This mode is useful when there are multiple executables in the directory
and you want to check the dependencies of one of them and the corresponding
.exe.config file.

Options:
    All options with parameters (other than -out: and -ignore*) may be specified more than once.

    !<exclude-pattern>          Exclude a relative path or file pattern from analysis.
    -l                          Output list of visited assemblies to BinaryCompatReport.Assemblies.txt
    -s                          Recursive (visit specified directories recursively). Default is non-recursive.
    -closure:<file.dll>         Path to a root assembly of a closure (to report unused references).
    -resolve:<directory>        Additional directory to resolve reference assemblies from.
    -p:<pattern>                Semicolon-separated file pattern(s) such as *.dll;*.exe.
    -baseline:<baseline.txt>    Optional, read <baseline.txt> instead of BinaryCompatReport.txt.
    -out:<report.txt>           Write report to <report.txt> instead of BinaryCompatReport.txt.
    -baselineDirectory:<dir>    Directory to resolve baselines in if the baseline is a relative path.
    -reportDirectory:<dir>      Directory to write report files to if -out: is a relative path.
    -customFailurePrompt:<text> Custom text to display when analysis fails (e.g. a link to wiki/readme).

    -ignoreVersionMismatch      Do not report assembly version mismatches.
                                Optionally restrict to specific config files (semicolon-separated),
                                e.g. -ignoreVersionMismatch:B.exe.config
    -ignoreFacade               Do not report facade assemblies.
    -ignoreMissingAssemblies    Do not report missing or unresolved assemblies.
    -ignoreFrameworkAssemblies  Do not analyze .NET Framework assemblies.
    -ignoreMissingTypes         Do not report missing types.
    -ignoreMissingMembers       Do not report missing members.
    -ignoreInterfaces           Do not report missing interface implementations.
    -ignoreUnreferenced         Do not report unreferenced assemblies when in closure mode.
    -ignoreSourceVersions       Strip source assembly versions before baseline comparison.
                                Useful when the same baseline is used across builds with different versions.
    -ignoreExtraBaselineEntries Only NEW issues (in the report but not in the baseline) cause failure.
                                Entries in the baseline but not in the report are ignored.
    -doNotResolveFromGAC        Do not resolve assemblies from GAC.
    -doNotResolveFromNetCore    Do not resolve assemblies from .NET runtime directories.
    -ivt                        Report internal API surface area consumed via InternalsVisibleTo.
    -embeddedInteropTypes       Report embedded interop types.
    -intPtrCtors                Report IntPtr constructors (Mono).

    If any of the below three output options are specified, all other output is suppressed.
    Use this to limit the output to only the information required.
    You can use any combination of these options:

    -outputExpectedWarnings     Only output old warnings that were in the baseline but no longer observed.
    -outputNewWarnings          Only output new warnings that weren't in the baseline.
    -outputSummary              Output the summary comparing the report and the baseline.

    @response.rsp               Response file containing additional command-line arguments, one per line.
    -?                          Display help.
```

</details>

The sections below explain the most-used options grouped by purpose. The full reference is the help text above.

There is also a separate, side mode to replicate binding redirects from one `app.config` to many (see [Replicate binding redirects](#replicate-binding-redirects) below):

```
checkbinarycompat -replicateBindingRedirects <source.exe.config> <destination.exe.config>+
```

## Exit codes

| Code | Meaning |
|------|---------|
| `0`  | The report matches the baseline. |
| `1`  | The report does **not** match the baseline (a new issue was found, or a previously expected one is gone). The new report is written to disk. In batch (JSON) mode, returned if any folder fails. |
| `-1` | Invalid command-line arguments. |
| `2`  | Invalid configuration file (batch mode). |

## Baseline

All the errors and inconsistencies found are logged into `BinaryCompatReport.txt` by default. You can choose a different file by passing the `-out:<file.txt>` option.

The file is checked in and at runtime the tool checks that the real `BinaryCompatReport.txt` is identical to the checked-in one. If it's not, the tool will fail and overwrite the checked-in baseline. If changing the build process or dependencies results in new errors being reported and they are all legitimate, the checked-in `BinaryCompatReport.txt` needs to be updated. The file is sorted to ease diffing.

A clean baseline is an empty file (no diagnostics).

### Baseline / report directories

For repos that run the tool from multiple working directories but want to keep all baselines and reports together (e.g. CI artifacts), use:

* `-baselineDirectory:<dir>` &mdash; if `-baseline:` is a relative path, resolve it under this directory.
* `-reportDirectory:<dir>` &mdash; if `-out:` is a relative path, write the report under this directory.

### Tolerant comparison modes

The default comparison is byte-exact. Two flags relax it:

* `-ignoreSourceVersions` &mdash; strips the `Version=` token from the *source* (reporting) assembly in each diagnostic line before comparing baseline to report. This lets a single baseline survive across builds that bump the version of the assemblies being analyzed.
* `-ignoreExtraBaselineEntries` &mdash; only *new* diagnostic lines (present in the report but missing from the baseline) cause failure. Lines that are in the baseline but not in the current report are silently accepted. Useful when the baseline covers multiple build configurations and any given run sees only a subset of the assemblies.

### Output filter flags

By default, the tool writes the full report and prints a summary. If you only want a slice, pass any combination of the three filters &mdash; everything else is suppressed:

* `-outputExpectedWarnings` &mdash; print only baseline lines no longer observed.
* `-outputNewWarnings` &mdash; print only lines new since the baseline.
* `-outputSummary` &mdash; print only the human-readable summary comparing the report and baseline.

## Closure mode

`-closure:app.exe` makes the tool operate on the transitive closure of references of `app.exe` instead of every file in the directory. If `app.exe.config` exists it is included automatically. You can pass several `-closure:` switches, or comma/semicolon-separate them (`-closure:app.exe;runtime-dep.dll`) to hint at runtime-only dependencies that aren't reachable via static references.

This is the mode to use when one folder hosts multiple unrelated executables and you only want to verify the closure of one of them.

In closure mode, files in the directory that are *not* reachable from the closure are listed as `Unreferenced assembly: <path>` in the report. Suppress with `-ignoreUnreferenced`.

## Response files

`@response.rsp` injects additional arguments from a text file (one argument per line; lines starting with `#` are comments). They can have any name and extension. Multiple `@` files can be combined. You can list multiple directories, `.dll` files and patterns that should be included or excluded from analysis. If the tool sees the same assembly in more than one file, it just picks one randomly &mdash; this is a source of non-determinism and should be avoided (exclude all but one `.dll` for each assembly name). Prepend the directory or file path with a `!` to exclude it. Please keep the file sorted for convenience.

The config file can also specify one or more `exe.config` or `dll.config` files. They will be inspected for binding redirects and versions.

## Batch (JSON) mode

Instead of analyzing a single directory, you can pass a `.json` configuration file that describes many folders to check, each with its own arguments, baseline, and report path. The tool runs all of them in parallel and reports a single pass/fail across the whole batch.

```
checkbinarycompat config.json
```

The schema is [`BinaryCompatConfig.schema.json`](BinaryCompatConfig.schema.json). A minimal example:

```json
{
    "$schema": "https://raw.githubusercontent.com/KirillOsenkov/MetadataTools/main/src/BinaryCompatChecker/BinaryCompatConfig.schema.json",
    "CustomFailurePrompt": "See https://wiki/binary-compat for instructions.",
    "FoldersToCheck": [
        {
            "Name": "MyApp x64",
            "Directory": "src\\MyApp\\bin\\Release\\net8.0\\win-x64",
            "CommandLineArguments": "-s -ignoreFrameworkAssemblies",
            "BaselinePath": "baselines\\MyApp.x64.txt",
            "ReportPath": "reports\\MyApp.x64.txt",
            "IgnoreVersionMismatch": [ "MyApp.exe.config" ],
            "Resolve": [ "..\\sharedRefs" ],
            "Exclude": [ "**\\xunit*.dll" ],
            "Closure": [ "MyApp.exe" ]
        }
    ]
}
```

Per-folder fields:

| Field | Meaning |
|-------|---------|
| `Name` | Human-readable label for the folder (used in the failure summary). |
| `Directory` | **Required.** Folder to scan. Relative paths are resolved against the config file. |
| `CommandLineArguments` | Extra command-line arguments to apply (same syntax as direct CLI). |
| `BaselinePath` | Baseline file (defaults to `BinaryCompatReport.txt` inside `Directory`). |
| `ReportPath` | Where to write the actual report. |
| `IgnoreVersionMismatch` | List of `*.config` file names to ignore for version-mismatch reporting. |
| `Resolve` | Extra directories to search for reference assemblies. |
| `Exclude` | Glob patterns to exclude from the scan. |
| `Closure` | Root assemblies for closure mode. |

Top-level fields:

* `CustomFailurePrompt` &mdash; appended to the failure message whenever the batch fails (useful for pointing developers at internal docs).

## Worked example: multi-closure on one output directory

This is the workflow used by Visual Studio-style products that drop binaries for many separate executables into a single `bin\Debug` and want a separate baseline per startup binary plus one big "everything else" check.

Three files are checked in at the repo root next to the solution:

**`UpdateBinaryCompat.cmd`** &mdash; the two-line script developers run after pulling changes that produced a baseline diff:

```cmd
msbuild

checkbinarycompat BinaryCompatConfig.json
```

The first invocation builds the solution (so `bin\Debug` is up to date). The second runs the batch and overwrites any baseline `.txt` that doesn't match.

**`BinaryCompatConfig.json`** &mdash; one config, four entries against the *same* directory:

```json
{
    "$schema": "https://raw.githubusercontent.com/KirillOsenkov/MetadataTools/main/src/BinaryCompatChecker/BinaryCompatConfig.schema.json",
    "FoldersToCheck": [
        {
            "Directory": "bin\\Debug",
            "BaselinePath": "BinaryCompatReport.txt",
            "IgnoreVersionMismatch": [
                "packager.exe.config",
                "misctools.exe.config"
            ],
            "Resolve": [
                "C:\\Program Files\\Microsoft Visual Studio\\18\\Enterprise\\MSBuild\\Current\\Bin"
            ],
            "Exclude": [ "ServiceHub", "LanguageServers", "Website.dll" ]
        },
        {
            "Directory": "bin\\Debug",
            "CommandLineArguments": "-out:Baseline-Interactive.txt -ignoreUnreferenced",
            "Closure": [ "InteractiveHost32.exe", "Microsoft.CodeAnalysis.CSharp.Scripting.dll" ]
        },
        {
            "Directory": "bin\\Debug",
            "CommandLineArguments": "-out:Baseline-Packager.txt -ignoreUnreferenced",
            "Closure": [ "packager.exe" ]
        },
        {
            "Directory": "bin\\Debug",
            "CommandLineArguments": "-out:Baseline-TestRunner-Desktop.txt -ignoreUnreferenced",
            "Closure": [ "TestRunner.Desktop.exe" ]
        }
    ]
}
```

Pattern notes:

* **Entry 1** is the wide check: it scans the whole `bin\Debug` but uses `Exclude` to skip subtrees and known noise. It points at the main `BinaryCompatReport.txt` baseline and uses `IgnoreVersionMismatch` to silence known-OK `.exe.config` mismatches. `Resolve` adds the host VS install dir so MSBuild assemblies resolve cleanly.
* **Entries 2-4** are closure-mode checks for each independent startup binary that lives in the same output directory. Each writes to its own baseline file (`-out:Baseline-<Name>.txt`) and passes `-ignoreUnreferenced` because by definition a closure can't see the assemblies that belong to the *other* startup binaries in the same folder. Adding extra files inside `Closure` (e.g. `Microsoft.CodeAnalysis.CSharp.Scripting.dll` next to `InteractiveHost32.exe`) hints at runtime-only dependencies that the static reference graph wouldn't pick up.
* All four baselines (`BinaryCompatReport.txt`, `Baseline-Interactive.txt`, `Baseline-Packager.txt`, `Baseline-TestRunner-Desktop.txt`) are checked in next to the config. A clean baseline is an empty file.

**Investigating a baseline diff** &mdash; when CI fails or `UpdateBinaryCompat.cmd` overwrites one of the baselines:

1. Read the new (failing) baseline. Each line is one diagnostic in the canonical shape documented above.
2. `git diff` the baseline file to isolate *what* changed since the last good run. A typical NuGet version bump shows up as a block of `Assembly \`Foo\` is referencing \`Bar, Version=A\` but found \`Bar, Version=B\`...` lines.
3. If the diff is legitimate (e.g. you intentionally bumped a NuGet), the script already overwrote the baseline. Just `git add` and commit.
4. If the diff is unexpected, drill into one diagnostic at a time: `refdump <ReferencingAssembly>.dll -a:<TargetAssembly>` shows the exact reference and version expected, `lbi <TargetAssembly>.dll -v` shows what's actually present.
5. **For "but which NuGet package did this?" follow-ups**, the on-disk tools cannot answer &mdash; you need an MSBuild binlog. Build with `msbuild MySolution.sln /bl` (or `dotnet build /bl`), then use the [MSBuild Structured Log Viewer](https://msbuildlog.com) or the [`binlogmcp`](https://github.com/KirillOsenkov/MSBuildStructuredLog) MCP server. Critical queries:
   * `$copy <full path to Bar.dll>` &mdash; identifies the producing copy task and the source path (often a `...\.nuget\packages\<pkg>\<ver>\lib\<tfm>\Bar.dll` path).
   * `$nuget Bar` &mdash; full NuGet graph for `Bar` across the build; shows which `<PackageReference>` (direct or transitive) forced the new version.
   * `$nuget project(MyApp.csproj) Bar` &mdash; same scoped to one project.
6. To verify your fix without rebuilding the world: re-run `checkbinarycompat BinaryCompatConfig.json` &mdash; exit code 0 means all four baselines match.

## Replicate binding redirects

Use this when you have a known-good `app.config` (e.g. from your main app) and want to copy its `<assemblyBinding>` block into many other `.config` files (test hosts, sub-apps, etc.):

```
checkbinarycompat -replicateBindingRedirects src\App\App.exe.config tests\Tests1.dll.config tests\Tests2.dll.config
```

This runs entirely separately from the analysis pipeline &mdash; no baseline, no report.

## MSBuild integration

The simplest integration runs `checkbinarycompat` after build in the output directory, guarded by a property so it can be turned on per build (see [`BinaryCompatChecker.Sample/Directory.Build.targets`](https://github.com/KirillOsenkov/MetadataTools/blob/main/src/BinaryCompatChecker.Sample/Directory.Build.targets)):

```xml
<Project>
  <Target Name="CheckBinaryCompat"
          AfterTargets="Build"
          Condition="$(CheckBinaryCompat) == true">
    <Exec Command="checkbinarycompat"
          ConsoleToMSBuild="True"
          WorkingDirectory="$(OutDir)" />
  </Target>
</Project>
```

Run with `msbuild /p:CheckBinaryCompat=true` (or wire it permanently into your CI build).

## Diagnostic message formats

Every line of the report is one diagnostic. Common shapes:

* `In assembly 'Foo, Version=1.2.3.4, ...': Failed to resolve type reference 'System.Bar' in assembly 'System'`
* `In assembly 'Foo, ...': Failed to resolve method reference 'System.String System.Bar::Baz()' in assembly 'System'`
* `Assembly 'Foo' is referencing 'Bar, Version=1.0.0.0, ...' but found 'Bar, Version=2.0.0.0, ...' at 'path\to\Bar.dll'`
* `Facade assembly: relative\path\to\Foo.dll`
* `Unreferenced assembly: relative\path\to\Foo.dll`
* `App.config: 'App.exe.config': '<assembly>' version is less than bindingRedirect range start '<oldVersionStart>'`
* `App.config: 'App.exe.config': publicKeyToken '<token>' from bindingRedirect for <name> doesn't match one from the actual assembly`

Lines are sorted alphabetically before being written, so diffs are stable.

## Output build artifacts

CI builds can publish the "real" `BinaryCompatReport.txt` as a build artifact so you can just download it and replace the checked-in copy with the real one. A few other report files may be produced alongside it:

 * `BinaryCompatReport.Assemblies.txt` &mdash; the sorted list of all assembly files and their versions that the tool used as inputs. Specify the `-l` option to generate this file.
 * `BinaryCompatReport.ivt.txt` &mdash; list of all internal APIs consumed, grouped by the exposing `.dll` then by the consuming `.dll`. Specify the `-ivt` option to generate this file.
