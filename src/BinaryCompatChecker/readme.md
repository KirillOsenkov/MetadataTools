`checkbinarycompat` is a dotnet global tool that inspects a set of .NET assemblies and finds potential compatibility issues.
It can run after a build, inspect the output, and generate a report or validate against a baseline.

It follows each assembly reference in each .dll. For each reference it checks that all the APIs expected by the referencing .dll are actually present in the referenced .dll. This is to detect binary mismatches at build time. This prevents runtime exceptions such as `MissingMethodException`, `ReflectionTypeLoadException`, etc.

In addition it also looks at the app.config file(s) if specified and does additional checking, such as verifying that the binding redirect ranges are consistent and the .dll version being redirected to is actually present.

It can also determine a closure of all transitive assembly references from a given set of "roots", and list assemblies not referenced in the closure (potentially unused).

## Install:
```
dotnet tool update -g checkbinarycompat
```

## Source code:
https://github.com/KirillOsenkov/MetadataTools/tree/main/src/BinaryCompatChecker

## NuGet:
https://www.nuget.org/packages/BinaryCompatChecker

## Command line arguments

```
Usage: checkbinarycompat <file-spec>* <option>* @<response-file>*

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

    !<exclude-pattern>      Exclude a relative path or file pattern from analysis.
    -l                      Output list of visited assemblies to BinaryCompatReport.Assemblies.txt
    -s                      Recursive (visit specified directories recursively). Default is non-recursive.
    -closure:<file.dll>     Path to a root assembly of a closure (to report unused references).
    -resolve:<directory>    Additional directory to resolve reference assemblies from.
    -p:<pattern>            Semicolon-separated file pattern(s) such as *.dll;*.exe.
    -out:<report.txt>       Write report to <report.txt> instead of BinaryCompatReport.txt.
    -ignoreVersionMismatch  Do not report assembly version mismatches.
    -ivt                    Report internal API surface area consumed via InternalsVisibleTo.
    -embeddedInteropTypes   Report embedded interop types.
    @response.rsp           Response file containing additional command-line arguments, one per line.
    -?:                     Display help.
```

## Baseline

All the errors and inconsistencies found are logged into BinaryCompatReport.txt by default. You can choose a different file by passing the `-out:<file.txt>` option.

The file is checked in and at runtime the tool checks that the real BinaryCompatReport.txt is identical to the checked in one. If it's not, the tool will fail and overwrite the checked in baseline. If changing the build process or dependencies results in new errors being reported and they are all legitimate, the checked in BinaryCompatReport.txt needs to be updated. The file is sorted to ease diffing.

## Configuration file

The tool optionally accepts zero or more @response.rsp files with additional arguments. You can list multiple directories, .dll files and patterns that should be included or excluded from analysis. If the tool sees the same assembly in more than one file, it just picks one randomly. This is a source of non-determinism and should be avoided (exclude all but one .dlls for each assembly name). Prepend the directory or file path with a ! to exclude it. Please keep the file sorted for convenience.

The config file can also specify one or more `exe.config` or `dll.config` files. They will be inspected for binding redirects and versions.

## Output build artifacts
CI build can publish the "real" BinaryCompatReport.txt as a build artifact so you can just download it and replace the checked in copy with the real one. It also can publish a couple other report files that may be helpful:

 * `BinaryCompatReport.Assemblies.txt` - the sorted list of all assembly files and their versions that the tool used as inputs. Specify the `-l` option to generate this file.
 * `BinaryCompatReport.ivt.txt` - list of all internal APIs consumed, grouped by the exposing .dll then by consuming .dll. Specify the `-ivt` option to generate this file.
