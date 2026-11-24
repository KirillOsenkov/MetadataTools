using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;
using GuiLabs.FileFormat.Wasm;

/// <summary>
/// The method that a function of a module with Mono AOT code was compiled from.
/// </summary>
public sealed class MonoAotMethod
{
    public const string KindMethod = "method";
    public const string KindGenericInstance = "generic instance";
    public const string KindGsharedvtIn = "gsharedvt in wrapper";
    public const string KindGsharedvtOut = "gsharedvt out wrapper";
    public const string KindWrapper = "wrapper";
    public const string KindHelper = "helper of the AOT compiler";
    public const string KindNotNamed = "generic instance or wrapper without a name";

    /// <summary>
    /// "method" for a row of the MethodDef table of the assembly. The other
    /// kinds are methods without a row: generic instances and wrappers.
    /// </summary>
    public string Kind { get; set; }

    /// <summary>
    /// The assembly that declares the type. For a generic instance it can
    /// differ from the assembly that the code is stored with.
    /// </summary>
    public string Assembly { get; set; }

    public string Namespace { get; set; } = "";
    public string Type { get; set; } = "";
    public string Method { get; set; } = "";

    /// <summary>
    /// For a generic instance: the type arguments, in the spelling of the
    /// function symbol, where every character that is not a letter or a
    /// digit is an underscore.
    /// </summary>
    public string Instantiation { get; set; }

    /// <summary>
    /// The name of the function in the object file of the assembly.
    /// </summary>
    public string Symbol { get; set; }

    public bool IsUnboxTrampoline { get; set; }

    public bool IsFromMetadata => Kind == KindMethod;

    public string TypeName => string.IsNullOrEmpty(Namespace) ? Type : Namespace + "." + Type;

    public string FullName
    {
        get
        {
            string type = TypeName;
            if (!string.IsNullOrEmpty(Instantiation))
            {
                type += "<" + Instantiation + ">";
            }

            string name = type.Length > 0 && Method.Length > 0 ? type + "." + Method : type + Method;
            return IsUnboxTrampoline ? name + " (unbox trampoline)" : name;
        }
    }
}

/// <summary>
/// Names for the functions of a module with Mono AOT code. The module has
/// the method index of each function and no names. The names are read from
/// two kinds of files of the same build:
///
/// The assembly that was compiled (name.dll, or the Webcil file name.wasm).
/// A method index below the number of rows of its MethodDef table is the row.
///
/// The object file of the assembly (name.dll.o). Its method table has a
/// relocation for each entry, which names the function symbol. The symbol
/// is the full name of the method with underscores in place of punctuation.
/// It is used for the methods without a row, and is split into type, type
/// arguments and method with the help of the type names of all assemblies.
/// </summary>
public sealed class MonoAotNames
{
    private sealed class TypeInfo
    {
        public string Assembly;
        public string Namespace;
        public string Name;
        public string[] MethodNames;
        public string[] MethodSymbols;
    }

    private sealed class AssemblyInfo
    {
        public string Path;
        public (TypeInfo type, string name)[] Methods;
    }

    /// <summary>
    /// The names that Mono has for its kinds of wrapper methods.
    /// </summary>
    private static readonly string[] wrapperKinds =
    [
        "delegate-begin-invoke", "delegate-end-invoke", "delegate-invoke", "runtime-invoke", "native-to-managed",
        "managed-to-native", "managed-to-managed", "remoting-invoke-with-check", "remoting-invoke", "xdomain-invoke",
        "xdomain-dispatch", "ldflda", "ldfld", "stfld", "synchronized", "dynamic-method", "isinst", "castclass",
        "proxy-isinst", "stelemref", "unbox", "write-barrier", "unknown", "other", "cominterop-invoke", "cominterop", "alloc"
    ];

    /// <summary>
    /// The short names that Mono writes for the types of the core library
    /// that have one, such as "int" for System.Int32.
    /// </summary>
    private static readonly (string shortName, string typeSymbol)[] shortTypeNames =
    [
        ("object", "System_Object"), ("string", "System_String"), ("bool", "System_Boolean"), ("char", "System_Char"),
        ("sbyte", "System_SByte"), ("byte", "System_Byte"), ("int16", "System_Int16"), ("uint16", "System_UInt16"),
        ("int", "System_Int32"), ("uint", "System_UInt32"), ("long", "System_Int64"), ("ulong", "System_UInt64"),
        ("single", "System_Single"), ("double", "System_Double"), ("intptr", "System_IntPtr"), ("uintptr", "System_UIntPtr"),
        ("typedbyref", "System_TypedReference")
    ];

    /// <summary>
    /// Method names of wrappers that are not methods of the type that the
    /// wrapper is for.
    /// </summary>
    private static readonly string[] wrapperMethodNames = ["invoke_callvirt", "invoke", "StructureToPtr", "PtrToStructure"];

    private const string CoreLibrary = "System.Private.CoreLib";

    private readonly Dictionary<string, AssemblyInfo> assemblies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The types by the spelling of the full name in a function symbol.
    /// Several assemblies can have a type with the same name: an internal
    /// type that is compiled into each of them from the same source file.
    /// </summary>
    private readonly Dictionary<string, List<TypeInfo>> typesBySymbol = new(StringComparer.Ordinal);

    public Dictionary<WasmFunction, MonoAotMethod> Methods { get; } = new();

    /// <summary>
    /// What was read and what was not found.
    /// </summary>
    public List<string> Messages { get; } = new();

    public int AssemblyCount { get; private set; }
    public int ObjectFileCount { get; private set; }

    /// <summary>
    /// Reads the names for the functions of the module. The files are
    /// looked for in the folder of the module, in the given folders, and in
    /// the "aot-in" folder inside each of them. The text of each function
    /// node is set to the name.
    /// </summary>
    public static MonoAotNames Read(WasmFile file, string filePath, IEnumerable<string> directories)
    {
        var names = new MonoAotNames();
        var folders = new List<string>();
        void AddFolder(string folder)
        {
            if (Directory.Exists(folder) && !folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(folder);
            }
        }

        string ownFolder = Path.GetDirectoryName(Path.GetFullPath(filePath));
        foreach (string folder in new[] { ownFolder }.Concat(directories.Select(Path.GetFullPath)))
        {
            AddFolder(folder);
            AddFolder(Path.Combine(folder, "aot-in"));
        }

        var modules = file.MonoAotModules;
        var missingAssemblies = new List<string>();
        foreach (var module in modules)
        {
            if (!names.ReadAssembly(module, folders))
            {
                missingAssemblies.Add(module.Name);
            }
        }

        names.AssemblyCount = names.assemblies.Count;
        foreach (var (shortName, typeSymbol) in shortTypeNames)
        {
            if (names.typesBySymbol.TryGetValue(typeSymbol, out var types))
            {
                names.typesBySymbol[shortName] = types;
            }
        }

        // A module that is an object file has the symbols itself
        bool isObjectFile = file.SymbolFunctions.Count > 0;
        var symbols = new Dictionary<MonoAotModule, Dictionary<(int index, bool isUnboxTrampoline), string>>();
        var missingObjectFiles = new List<string>();
        if (isObjectFile)
        {
            foreach (var module in modules)
            {
                symbols[module] = CollectSymbols(file);
            }
        }
        else
        {
            var paths = new List<(MonoAotModule module, string path)>();
            foreach (var module in modules)
            {
                string path = folders.Select(f => Path.Combine(f, module.Name + ".dll.o")).FirstOrDefault(File.Exists);
                if (path == null)
                {
                    missingObjectFiles.Add(module.Name);
                }
                else
                {
                    paths.Add((module, path));
                }
            }

            var results = new Dictionary<(int, bool), string>[paths.Count];
            Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 6) }, i =>
            {
                try
                {
                    results[i] = CollectSymbols(WasmFile.ReadFromFile(paths[i].path));
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is IndexOutOfRangeException || ex is ArgumentException)
                {
                }
            });

            for (int i = 0; i < paths.Count; i++)
            {
                if (results[i] != null && results[i].Count > 0)
                {
                    symbols[paths[i].module] = results[i];
                    names.ObjectFileCount++;
                }
                else
                {
                    missingObjectFiles.Add(paths[i].module.Name);
                }
            }
        }

        foreach (var function in file.Functions)
        {
            if (function.Owner == null)
            {
                continue;
            }

            var method = names.Describe(function, symbols);
            names.Methods[function] = method;
            if (method.Kind != MonoAotMethod.KindHelper && method.Kind != MonoAotMethod.KindNotNamed && !isObjectFile)
            {
                string name = method.FullName;
                file.FunctionNames[function.Index] = name;
                function.Text = file.DescribeFunction(function.Index);
            }
        }

        if (missingAssemblies.Count > 0)
        {
            names.Messages.Add($"No assembly (name.dll or name.wasm with the same module version id) was found for {missingAssemblies.Count} of {modules.Count} assemblies: {Shorten(missingAssemblies)}. Their methods have no names from metadata.");
        }

        if (missingObjectFiles.Count > 0)
        {
            names.Messages.Add($"No object file (name.dll.o) was found for {missingObjectFiles.Count} of {modules.Count} assemblies: {Shorten(missingObjectFiles)}. Their generic instances and wrappers have no names.");
        }

        if (missingAssemblies.Count > 0 || missingObjectFiles.Count > 0)
        {
            names.Messages.Add($"Folders that were searched: {string.Join("; ", folders)}. Add folders with --names <folder>.");
        }

        return names;
    }

    private static string Shorten(List<string> names)
    {
        const int Limit = 6;
        return names.Count <= Limit ? string.Join(", ", names) : string.Join(", ", names.Take(Limit)) + ", ...";
    }

    /// <summary>
    /// The symbol of each function of an object file that the method table
    /// or the unbox trampoline table lists.
    /// </summary>
    private static Dictionary<(int, bool), string> CollectSymbols(WasmFile objectFile)
    {
        var result = new Dictionary<(int, bool), string>();
        foreach (var function in objectFile.Functions)
        {
            if (function.MethodIndex >= 0 && objectFile.FunctionNames.TryGetValue(function.Index, out string name))
            {
                result[(function.MethodIndex, function.IsUnboxTrampoline)] = name;
            }
        }

        return result;
    }

    private bool ReadAssembly(MonoAotModule module, List<string> folders)
    {
        foreach (string folder in folders)
        {
            var candidates = new List<string>();
            string dll = Path.Combine(folder, module.Name + ".dll");
            if (File.Exists(dll))
            {
                candidates.Add(dll);
            }

            // A published Webcil file can have a hash in its name
            candidates.AddRange(Directory.GetFiles(folder, module.Name + ".*wasm").Where(f => !f.EndsWith(".native.wasm", StringComparison.OrdinalIgnoreCase)));
            foreach (string path in candidates)
            {
                try
                {
                    if (ReadAssembly(module, path))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is BadImageFormatException || ex is InvalidDataException || ex is ArgumentException)
                {
                }
            }
        }

        return false;
    }

    private bool ReadAssembly(MonoAotModule module, string path)
    {
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata && AddAssembly(module, reader.GetMetadataReader(), path);
        }

        var webcil = WasmFile.ReadFromFile(path).Webcil?.Metadata;
        if (webcil == null)
        {
            return false;
        }

        var bytes = webcil.Buffer.ReadBytes(webcil.Start, webcil.Length);
        using var provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.Create(bytes));
        return AddAssembly(module, provider.GetMetadataReader(), path);
    }

    private bool AddAssembly(MonoAotModule module, MetadataReader metadata, string path)
    {
        // The method table has an entry for each row of the MethodDef table
        // and then the methods without a row
        int methodCount = metadata.MethodDefinitions.Count;
        if (methodCount > module.MethodCount)
        {
            return false;
        }

        var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        if (Guid.TryParse(module.AssemblyGuid, out var expected) && expected != mvid)
        {
            return false;
        }

        var info = new AssemblyInfo { Path = path, Methods = new (TypeInfo, string)[methodCount] };
        var types = new Dictionary<TypeDefinitionHandle, TypeInfo>();
        TypeInfo GetType(TypeDefinitionHandle handle)
        {
            if (types.TryGetValue(handle, out var type))
            {
                return type;
            }

            var definition = metadata.GetTypeDefinition(handle);
            string name = metadata.GetString(definition.Name);
            var declaringType = definition.GetDeclaringType();
            if (declaringType.IsNil)
            {
                type = new TypeInfo { Assembly = module.Name, Namespace = metadata.GetString(definition.Namespace), Name = name };
            }
            else
            {
                var outer = GetType(declaringType);
                type = new TypeInfo { Assembly = module.Name, Namespace = outer.Namespace, Name = outer.Name + "." + name };
            }

            types[handle] = type;
            return type;
        }

        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = GetType(handle);
            var methodNames = new HashSet<string>();
            foreach (var methodHandle in metadata.GetTypeDefinition(handle).GetMethods())
            {
                string methodName = metadata.GetString(metadata.GetMethodDefinition(methodHandle).Name);
                int row = MetadataTokens.GetRowNumber(methodHandle);
                if (row >= 1 && row <= methodCount)
                {
                    info.Methods[row - 1] = (type, methodName);
                }

                methodNames.Add(methodName);
            }

            type.MethodNames = methodNames.OrderByDescending(n => n.Length).ToArray();
            type.MethodSymbols = type.MethodNames.Select(MakeSymbol).ToArray();
            string full = type.Namespace.Length > 0 ? type.Namespace + "." + type.Name : type.Name;
            string symbol = MakeSymbol(full);
            if (!typesBySymbol.TryGetValue(symbol, out var list))
            {
                list = new List<TypeInfo>(1);
                typesBySymbol[symbol] = list;
            }

            // The core library first: a generic instance of one of its
            // types is counted for it and not for an assembly with a copy
            if (module.Name == CoreLibrary)
            {
                list.Insert(0, type);
            }
            else
            {
                list.Add(type);
            }
        }

        assemblies[module.Name] = info;
        return true;
    }

    /// <summary>
    /// The spelling that the compiler uses for a name in a function symbol:
    /// letters and digits are kept, parentheses and the closing angle
    /// bracket are left out, a comma with a space is one underscore, and
    /// every other character is an underscore.
    /// </summary>
    private static string MakeSymbol(string name)
    {
        var builder = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c < 0x80 && char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (c == ' ' && i + 2 < name.Length && name[i + 1] == '(' && name[i + 2] == ')')
            {
                i += 2;
            }
            else if (c == ',' && i + 1 < name.Length && name[i + 1] == ' ')
            {
                builder.Append('_');
                i++;
            }
            else if (c == '(' || c == ')' || c == '>')
            {
            }
            else if (c < 0x80)
            {
                builder.Append('_');
            }
            else
            {
                // One underscore for each byte of the UTF-8 encoding
                int length = char.IsHighSurrogate(c) && i + 1 < name.Length ? 2 : 1;
                builder.Append('_', Encoding.UTF8.GetByteCount(name.AsSpan(i, length)));
                i += length - 1;
            }
        }

        return builder.ToString();
    }

    private MonoAotMethod Describe(WasmFunction function, Dictionary<MonoAotModule, Dictionary<(int, bool), string>> symbols)
    {
        var module = function.Owner;
        var method = new MonoAotMethod { Assembly = module.Name, IsUnboxTrampoline = function.IsUnboxTrampoline };
        int index = function.MethodIndex;
        if (index < 0)
        {
            method.Kind = MonoAotMethod.KindHelper;
            method.Type = "(helpers of the AOT compiler)";
            method.Method = function.Text;
            return method;
        }

        if (symbols.TryGetValue(module, out var moduleSymbols) && moduleSymbols.TryGetValue((index, function.IsUnboxTrampoline), out string symbol))
        {
            method.Symbol = symbol;
        }

        if (assemblies.TryGetValue(module.Name, out var assembly) && index < assembly.Methods.Length && assembly.Methods[index].type != null)
        {
            var (type, name) = assembly.Methods[index];
            method.Kind = MonoAotMethod.KindMethod;
            method.Namespace = type.Namespace;
            method.Type = type.Name;
            method.Method = name;
            return method;
        }

        // Without the assembly it is not known where the methods without a
        // row start, so the symbol is used for every method
        if (method.Symbol != null)
        {
            ParseSymbol(method, module.Name);
            if (assembly == null && method.Kind == MonoAotMethod.KindGenericInstance)
            {
                method.Kind = MonoAotMethod.KindMethod;
            }

            return method;
        }

        method.Kind = assembly == null ? MonoAotMethod.KindMethod : MonoAotMethod.KindNotNamed;
        method.Type = assembly == null ? "(methods without a name)" : "(generic instances and wrappers without a name)";
        method.Method = $"method {index}";
        return method;
    }

    /// <summary>
    /// Splits the function symbol of a method without a row in the
    /// MethodDef table. The symbol has a prefix for the assembly, and then
    /// either the name of a wrapper or the full name of the method: type,
    /// type arguments, method name, parameter types.
    /// </summary>
    private void ParseSymbol(MonoAotMethod method, string moduleName)
    {
        const string UnboxPrefix = "ut_";
        const string GsharedvtIn = "aot_wrapper_gsharedvt_in_sig_";
        const string GsharedvtOut = "aot_wrapper_gsharedvt_out_sig_";
        const string AotWrapper = "aot_wrapper_";
        const string Wrapper = "wrapper_";

        string symbol = method.Symbol;
        if (symbol.StartsWith(UnboxPrefix, StringComparison.Ordinal))
        {
            symbol = symbol.Substring(UnboxPrefix.Length);
        }

        string prefix = MakeSymbol(moduleName) + "_";
        if (symbol.StartsWith(prefix, StringComparison.Ordinal))
        {
            symbol = symbol.Substring(prefix.Length);
        }
        else if (symbol.StartsWith("corlib_", StringComparison.Ordinal))
        {
            symbol = symbol.Substring("corlib_".Length);
        }

        if (symbol.StartsWith("mono_aot_", StringComparison.Ordinal))
        {
            method.Kind = MonoAotMethod.KindHelper;
            method.Type = "(helpers of the AOT compiler)";
            method.Method = symbol;
            return;
        }

        if (symbol.StartsWith(GsharedvtIn, StringComparison.Ordinal))
        {
            method.Kind = MonoAotMethod.KindGsharedvtIn;
            method.Type = "(gsharedvt in wrappers)";
            method.Method = symbol.Substring(GsharedvtIn.Length);
            return;
        }

        if (symbol.StartsWith(GsharedvtOut, StringComparison.Ordinal))
        {
            method.Kind = MonoAotMethod.KindGsharedvtOut;
            method.Type = "(gsharedvt out wrappers)";
            method.Method = symbol.Substring(GsharedvtOut.Length);
            return;
        }

        if (symbol.StartsWith(AotWrapper, StringComparison.Ordinal))
        {
            // The wrapper of a P/Invoke method has the assembly, the type
            // and the method, and then the word "pinvoke" and the
            // signature. For the other wrappers the first word is the kind.
            string rest = symbol.Substring(AotWrapper.Length);
            int underscore = rest.IndexOf('_');
            bool isPInvoke = rest.Contains("_pinvoke_") || rest.StartsWith("pinvoke_", StringComparison.Ordinal);
            string kind = isPInvoke ? "pinvoke" : underscore > 0 ? rest.Substring(0, underscore) : rest;
            method.Kind = $"{MonoAotMethod.KindWrapper} ({kind})";
            method.Type = $"({kind} wrappers)";
            method.Method = isPInvoke ? rest : underscore > 0 ? rest.Substring(underscore + 1) : "";
            return;
        }

        if (symbol.StartsWith(Wrapper, StringComparison.Ordinal))
        {
            string rest = symbol.Substring(Wrapper.Length);
            string kind = null;
            foreach (string candidate in wrapperKinds)
            {
                string candidateSymbol = candidate.Replace('-', '_') + "_";
                if (rest.StartsWith(candidateSymbol, StringComparison.Ordinal))
                {
                    kind = candidate;
                    rest = rest.Substring(candidateSymbol.Length);
                    break;
                }
            }

            method.Kind = kind == null ? MonoAotMethod.KindWrapper : $"{MonoAotMethod.KindWrapper} ({kind})";
            if (!TryParseMember(rest, method, moduleName, isWrapper: true))
            {
                method.Type = kind == null ? "(wrappers)" : $"({kind} wrappers)";
                method.Method = rest;
            }

            return;
        }

        method.Kind = MonoAotMethod.KindGenericInstance;
        if (!TryParseMember(symbol, method, moduleName, isWrapper: false))
        {
            method.Type = "(type not found)";
            method.Method = symbol;
        }
    }

    /// <summary>
    /// The text starts with the name of a type. The longest name of a type
    /// of the assemblies that the text starts with is taken as the type.
    /// The first place after it where a method name of that type is found
    /// is taken as the method name, and what is between them as the type
    /// arguments.
    /// </summary>
    private bool TryParseMember(string text, MonoAotMethod method, string moduleName, bool isWrapper)
    {
        List<TypeInfo> candidates = null;
        int typeLength = 0;
        for (int i = 1; i <= text.Length; i++)
        {
            if ((i == text.Length || text[i] == '_') && typesBySymbol.TryGetValue(text.Substring(0, i), out var found))
            {
                candidates = found;
                typeLength = i;
            }
        }

        if (candidates == null)
        {
            return false;
        }

        // When several assemblies have a type with this name: the type of
        // the assembly that the code is stored with, or else the first one
        // that has a method whose name is in the text
        string rest = text.Substring(typeLength);
        TypeInfo type = null;
        string name = null;
        int position = 0;
        if (isWrapper && FindName(rest, wrapperMethodNames, out position, out int wrapperName))
        {
            name = wrapperMethodNames[wrapperName];
        }
        else
        {
            foreach (var candidate in candidates.OrderBy(c => c.Assembly == moduleName ? 0 : 1))
            {
                if (FindName(rest, candidate.MethodSymbols, out position, out int index))
                {
                    type = candidate;
                    name = candidate.MethodNames[index];
                    break;
                }
            }
        }

        type ??= candidates.FirstOrDefault(c => c.Assembly == moduleName) ?? candidates[0];
        method.Assembly = type.Assembly;
        method.Namespace = type.Namespace;
        method.Type = type.Name;
        if (name == null)
        {
            method.Method = rest.Trim('_');
            return true;
        }

        method.Method = name;
        string instantiation = rest.Substring(0, position).Trim('_');
        if (instantiation.Length > 0)
        {
            method.Instantiation = instantiation;
        }

        return true;
    }

    /// <summary>
    /// Finds the name that is in the text at the lowest position, as a
    /// whole word between underscores. Of two names at the same position
    /// the longer one is taken.
    /// </summary>
    private static bool FindName(string text, string[] names, out int position, out int index)
    {
        position = int.MaxValue;
        index = -1;
        int length = 0;
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            if (name.Length == 0)
            {
                continue;
            }

            int from = 0;
            while (from < text.Length)
            {
                int found = text.IndexOf(name, from, StringComparison.Ordinal);
                if (found < 0 || found > position)
                {
                    break;
                }

                int end = found + name.Length;
                bool isWhole = found > 0 && text[found - 1] == '_' && (end == text.Length || text[end] == '_');
                if (isWhole)
                {
                    if (found < position || name.Length > length)
                    {
                        position = found;
                        index = i;
                        length = name.Length;
                    }

                    break;
                }

                from = found + 1;
            }
        }

        return index >= 0;
    }
}
