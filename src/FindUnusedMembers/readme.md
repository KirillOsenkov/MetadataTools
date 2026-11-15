# FindUnusedMembers

Lists the types and members of one or more .NET assemblies that cannot be reached from a set of consumer assemblies.

```
dotnet tool update -g FindUnusedMembers
FindUnusedMembers Lib.dll -c:C:\App\bin\Debug -out:unused.txt
```

Use it to remove the parts of a library that an application does not use, or, without `-c`, to find internal code of
a library that its own public API does not reach.

## Options

| Option | Meaning |
|---|---|
| `<target.dll>` | Assembly to analyze. Several targets are analyzed together. |
| `-c:<path>` | Consumer assemblies: a file, a directory (searched recursively for `*.dll` and `*.exe`), or `dir\pattern`. Repeatable. Copies of the targets in it are ignored. |
| `-keepPublic` | Treat the public and protected API of the targets as used. This is the default when no `-c` is given. |
| `-roots:<file>` | Extra roots, one documentation ID per line (`T:`, `M:`, `F:`, `P:`, `E:`). A trailing `*` matches a prefix. `#` starts a comment. |
| `-r:<dir>` | Extra directory to resolve references from. Repeatable. |
| `-why:<text>` | For every used member whose ID contains `<text>`, print the chain of references that marks it as used. Repeatable. |
| `-out:<file>` | Write the report to a file. |
| `-accessors` | Also list the unused getter or setter of a used property. Off by default: deleting one accessor changes the shape of an API (a read-write property becomes read-only or write-only) and saves little. |
| `-v` | Also list resolution warnings. |
| `-dumpBaml:<name>` | Print the records of the BAML resources of the target whose name contains `<name>` (for diagnostics). |

## How it works

The tool reads the targets and the consumers with Mono.Cecil and computes the set of target types and members that can
be reached:

* Roots are the type and member references of every consumer that references a target, `typeof` and enum values in
  consumer custom attributes, target members that a consumer type overrides or implements, and the compiled XAML (BAML)
  of every consumer. The module initializer, assembly attributes, `[Export]` types and the XAML type helper of the
  targets are roots too.
* From a used method, everything its signature, attributes and IL body refers to is used. A field counts as used only
  when it is read; a field that is only written is reported as `(written but never read)`.
* A virtual method is used when its declaring type is used and the method it overrides or implements is used, or
  belongs to another assembly (for example `OnApplyTemplate` or `IDisposable.Dispose`).
* The BAML of the targets is read too. A style or template that is keyed by a type, or by an `x:Static` member, counts
  only when that type or member is used. Types, properties, attached properties (`GetX`/`SetX`), events,
  `x:Static`/`x:Type` references and converters are resolved from the BAML records.
* Enum members, delegate members, members of COM interfaces, fields of types with an interop layout and members with
  serialization or composition attributes are kept with their type. C# operator pairs (`==` and `!=`) are kept
  together.
* Constructors that the runtime calls through reflection are kept with their type: the parameterless constructor of a
  `SafeHandle` or `CriticalHandle` subclass (the interop marshaler creates it for a returned handle), the
  `(SerializationInfo, StreamingContext)` constructor of an `ISerializable` type, and the constructors of a type that
  is named by `typeof` in an attribute (`[TypeConverter(typeof(X))]`) or by a type name string.
* Without `-accessors`, the unused accessor of a used property is kept, and so is everything that accessor uses (for
  example the field that a getter returns). A get/set auto-property whose getter is never called is still reported as
  `(written but never read)`.
* A type that declares constants is never reported as a whole unused type, because the code that uses a constant holds
  a copy of its value and no reference to the type. Its constants are listed for a source search and its other
  members are listed one by one. After its constants are deleted, a later run reports the empty type.

## Heuristics for reflection and string-based access

* Names in XAML strings (binding paths, trigger properties) mark public properties with that name, when the declaring
  type is used.
* A string that is the full name of a target type marks that type.
* A call with a target type as a generic argument and a string that names a member of that type marks the member:
  `Reflector.SetValue<Foo>("Bar", value)`, `WeakEventManager<Foo, Args>.AddHandler(source, "Changed", handler)`.
* In a method that calls a reflection API (`Type.GetProperty`, `Activator.CreateInstance`, `new Binding("...")`, ...),
  the string literals mark members with those names.

The `Reflection call sites` section of the report lists the places where these heuristics applied, and reflection calls
whose arguments are not constants. Check them by hand.

The `Classes that are never instantiated` section lists used classes with used instance members but no used instance
constructor. Their instance members can only run on a null reference, which usually means that the code which reaches
them is dead too (for example a type test `x is Foo` or a comparer for `Foo`). Delete such a class together with that
code by hand.

## Limitations

* Constant values are copied into the code that uses them, so the IL shows no reference to a `const` field. The report
  lists the constants of used types separately. Search the source of the targets and the consumers for their names.
* Code that the compiler removes as unreachable (for example after `if (SomeConstFalse) return;`) does not appear in
  the IL, but it still refers to members in the source. Delete that code too, or keep the members.
* Members that are accessed by name through reflection, `dynamic`, serialization or data binding from code that the
  heuristics do not cover are reported as unused. Before deleting a member, search the source for its name.
* `nameof(X)` compiles to the string `"X"`, so a member that is used only in `nameof` is reported as unused. After the
  member is deleted, replace each such `nameof(X)` with `"X"`; serialization keys and messages stay the same.
* Interface implementations are matched without regard to the generic arguments of the interface, so a class that
  implements `IComparer<A>` and `IComparer<B>` keeps both implementations when either is used.
* A XAML resource that is never looked up can still be required by the XAML compiler. If a build fails with `MC3050` or
  `MC4005` after a deletion, the type or attached property accessor is referenced from XAML.

## Suggested workflow

1. Build the target and run the tool against a directory that holds the consumers.
2. Delete the unused types first, then the unused members. Rebuild the target and the consumers after each batch.
3. Run the tool again on the new build: members that were used only by deleted code are reported in the next pass.
4. Run the application and exercise the affected features. A `MissingMethodException`, `ArgumentException` from a
   reflection call or a XAML load error points to a member that is accessed by name; use `-why:` to see what marks
   the members around it, and `-roots:` to keep such members in later runs.
