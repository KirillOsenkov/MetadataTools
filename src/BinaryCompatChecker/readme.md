BinaryCompatChecker runs after build and can validate that your output assemblies are compatible with each other.

## Source code:
https://github.com/KirillOsenkov/MetadataTools/tree/main/src/BinaryCompatChecker

## NuGet:
https://www.nuget.org/packages/BinaryCompatChecker

It looks at all the .dll files in the output directory and follows each assembly reference in each .dll. For each reference it checks that all the APIs expected by the referencing .dll are actually present in the referenced .dll. This is to detect binary mismatches at build time. This prevents runtime exceptions such as MissingMethodException, ReflectionTypeLoadException etc. 

In addition it also looks at the app.config file if specified and does additional checking, such as verifying that the binding redirect ranges are consistent and the .dll version being redirected to is actually present.

## Command line arguments
Once you restore the NuGet package the tools directory inside it contains BinaryCompatChecker.exe. It accepts three arguments:

`BinaryCompatChecker.exe Root\Directory\bin BinaryCompatBaseline.txt BinaryCheckerConfig.txt`

 1. Pass the root directory containing all the .dll files to check. It will be walked recursively.
 2. Pass the path to the checked in baseline file. If it doesn't exist, it will be created. If it exists, it will be overwritten.
 3. Pass the path to the config file.

## Baseline

All the errors and inconsistencies found are logged into BinaryCompatBaseline.txt

The file is checked in and at runtime the tool checks that the real BinaryCompatBaseline.txt is identical to the checked in one. If it's not, the tool will fail and overwrite the checked in baseline. If changing the build process or dependencies results in new errors being reported and they are all legitimate, the checked in BinaryCompatBaseline.txt needs to be updated. The file is sorted to ease diffing.

## Configuration file

The file BinaryCheckerConfig.txt lists the directories and .dll files that should be excluded from analysis. Since the tool can only deal with a single .dll per assembly name we exclude .dlls that don't get loaded to reduce the noise. If the tool sees the same assembly in more than one file, it just picks one randomly. This is a source of non-determinism and should be avoided (exclude all but one .dlls for each assembly name). Prepend the directory or file path with a ! to exclude it. Please keep the file sorted for convenience.

The config file can also specify one or more `app.config` files. They will be inspected for binding redirects and versions.

## Output build artifacts
CI build can publishes the "real" BinaryCompatBaseline.txt as a build artifact so you can just download it and replace the checked in copy with the real one. It also can publish a couple other report files that may be helpful:

 * `BinaryCompatBaseline.assemblylist.txt` - the sorted list of all assembly files and their versions that the tool used as inputs
 * `BinaryCompatBaseline.ivt.txt` - list of all internal APIs consumed, grouped by the exposing .dll then by consuming .dll
