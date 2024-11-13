# CheckBinaryCompat

`checkbinarycompat` is a dotnet global tool that inspects a set of .NET assemblies and finds potential compatibility issues.
It can run after a build, inspect the output, and generate a report or validate against a baseline.

It follows each assembly reference in each .dll. For each reference it checks that all the APIs expected by the referencing .dll are actually present in the referenced .dll. This is to detect binary mismatches at build time. This prevents runtime exceptions such as `MissingMethodException`, `ReflectionTypeLoadException`, etc.

In addition it also looks at the app.config file(s) if specified and does additional checking, such as verifying that the binding redirect ranges are consistent and the .dll version being redirected to is actually present.

It can also determine a closure of all transitive assembly references from a given set of "roots", and list assemblies not referenced in the closure (potentially unused).

## Install:
```
dotnet tool update -g checkbinarycompat
```

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
    When no patterns are specified, uses *.dll;*.exe;*.dll.config;*.exe.config.

Options:
    All options with parameters (other than -out:) may be specified more than once.

    !<exclude-pattern>       Exclude a relative path or file pattern from analysis.
    -l                       Output list of visited assemblies to BinaryCompatReport.Assemblies.txt
    -s                       Recursive (visit specified directories recursively). Default is non-recursive.
    -closure:<file.dll>      Path to a root assembly of a closure (to report unused references).
    -resolve:<directory>     Additional directory to resolve reference assemblies from.
    -p:<pattern>             Semicolon-separated file pattern(s) such as *.dll;*.exe.
    -baseline:<baseline.txt> Optional, read <baseline.txt> instead of BinaryCompatReport.txt.
    -out:<report.txt>        Write report to <report.txt> instead of BinaryCompatReport.txt.

    -ignoreVersionMismatch   Do not report assembly version mismatches.
    -ignoreFacade            Do not report facade assemblies.
    -ignoreMissingAssemblies Do not report missing or unresolved assemblies.
    -ignoreMissingTypes      Do not report missing types.
    -ignoreMissingMembers    Do not report missing members.
    -ignoreInterfaces        Do not report missing interface implementations.
    -ivt                     Report internal API surface area consumed via InternalsVisibleTo.
    -embeddedInteropTypes    Report embedded interop types.
    -intPtrCtors             Report IntPtr constructors (Mono).

    If any of the below three output options are specified, all other output is suppressed.
    Use this to limit the output to only the information requires.
    You can use any combination of these options:

    -outputExpectedWarnings  Only output old warnings that were in the baseline but no longer observed (and nothing else).
    -outputNewWarnings       Only output new warnings that weren't in the baseline and nothing else.
    -outputSummary           Output the summary comparing the report and the baseline.

    @response.rsp            Response file containing additional command-line arguments, one per line.
    -?:                      Display help.

-ignoreVersionMismatch can optionally specify a list of app.config file names (semicolon-separated)
that should be ignored for version mismatch reporting. If there is a version mismatch that is covered
by A.exe.config, but not B.exe.config, you can suppress warnings about B.exe.config using:
-ignoreVersionMismatch:B.exe.config

There is a separate command for the tool to replicate binding redirects from an app.config file
to one or more other app.config files:
checkbinarycompat -replicateBindingRedirects <source.exe.config> <destination.exe.config>+
```

## Baseline

All the errors and inconsistencies found are logged into `BinaryCompatReport.txt` by default. You can choose a different file by passing the `-out:<file.txt>` option.

The file is checked in and at runtime the tool checks that the real `BinaryCompatReport.txt` is identical to the checked in one. If it's not, the tool will fail and overwrite the checked in baseline. If changing the build process or dependencies results in new errors being reported and they are all legitimate, the checked in `BinaryCompatReport.txt` needs to be updated. The file is sorted to ease diffing.

## Configuration file

The tool optionally accepts zero or more `@response.rsp` files with additional arguments. They can have any name and extension. You can list multiple directories, .dll files and patterns that should be included or excluded from analysis. If the tool sees the same assembly in more than one file, it just picks one randomly. This is a source of non-determinism and should be avoided (exclude all but one .dlls for each assembly name). Prepend the directory or file path with a ! to exclude it. Please keep the file sorted for convenience.

The config file can also specify one or more `exe.config` or `dll.config` files. They will be inspected for binding redirects and versions.

## Output build artifacts
CI build can publish the "real" `BinaryCompatReport.txt` as a build artifact so you can just download it and replace the checked in copy with the real one. It also can publish a couple other report files that may be helpful:

 * `BinaryCompatReport.Assemblies.txt` - the sorted list of all assembly files and their versions that the tool used as inputs. Specify the `-l` option to generate this file.
 * `BinaryCompatReport.ivt.txt` - list of all internal APIs consumed, grouped by the exposing .dll then by consuming .dll. Specify the `-ivt` option to generate this file.
