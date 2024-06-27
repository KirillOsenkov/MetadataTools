# Lbi (ListBinaryInfo)

`lbi` (list binary info) is a dotnet global tool to print information about assemblies.

Run lbi in the current directory to print information about all assemblies in the current directory and all subdirectories. By default the output is grouped by assembly full name, then by file SHA and then prints identical files for each SHA.
`lbi -l` will print the file list without grouping (similar to `dir /s`).

Usage:

```
lbi.exe [<pattern>]
        [-l[:<out.txt>]]
        [-d:<path>]*
        [-ed:<path>]*
        [-ef:<substring>]*
        [-nr]
        [-sn]
        [-p]
        [-v]
        [-fv]
        [-iv]
        [-tf]
        [@response.rsp]
```

```
    -l:     List full directory contents (optionally output to a file, e.g. out.txt)
            If not specified, files are grouped by hash, then version.
    -d:     Specify root directory to start in (defaults to current directory).
            Maybe be specified more than once to scan multiple directories.
    -ed:    Exclude directory from search. May be specified more than once.
    -ef:    Exclude files with substring. May be specified more than once.
    -nr:    Non-recursive (current directory only). Recursive by default.

    -sn     Print assembly strong named/delay-signed/public-signed.
    -snv    Validate assembly strong name using sn.exe -vf (slow).
    -p      Print assembly platform.
    -v      Print assembly version.
    -fv     Print assembly file version.
    -iv     Print assembly informational version.
    -tf     Print assembly target framework.

    @r:     Specify a response file (each file line treated as argument).
```

```
Examples:
    lbi foo.dll
    lbi *.exe -nr
    lbi
    lbi -d:sub\directory -d:sub\dir2 -ed:sub\dir2\obj -l:out.txt
```
