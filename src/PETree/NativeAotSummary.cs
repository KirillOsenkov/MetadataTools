using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using GuiLabs.FileFormat;
using GuiLabs.FileFormat.PE;

/// <summary>
/// For a NativeAOT image: the bytes per assembly, namespace, type and
/// method, from the .mstat file that the compiler writes with
/// IlcGenerateMstatFile. The file is an assembly without code of its own.
/// Its global type has one method per kind of entry, and the IL of that
/// method is the list: for each compiled method a metadata token, the
/// length of the code, of the GC info and of the exception handling info;
/// for each type a token and the length of its MethodTable; and so on.
/// </summary>
public static class NativeAotSummary
{
    private const string CoreLibrary = "System.Private.CoreLib";

    private sealed class TypeDesc
    {
        public string Assembly = "";
        public string Namespace = "";
        public string Name = "";

        /// <summary>
        /// The type arguments of a generic instance, or null.
        /// </summary>
        public string Arguments;

        public string Definition => Namespace.Length > 0 ? Namespace + "." + Name : Name;
        public string Display => Arguments != null ? $"{Definition}<{Arguments}>" : Definition;
    }

    private sealed class Item
    {
        public TypeDesc Type;
        public string Method;
        public string MethodArguments;
        public long Code;
        public long GcInfo;
        public long EhInfo;
        public long Bytes;

        /// <summary>
        /// File offset of the code, when the map file was given and the
        /// code was found in the image.
        /// </summary>
        public int Start = -1;

        public bool IsInstance => Type.Arguments != null || MethodArguments != null;
        public string Instantiation => !IsInstance ? null : MethodArguments == null ? Type.Arguments : $"{Type.Arguments} / {MethodArguments}";

        public string FullName
        {
            get
            {
                string name = $"{Type.Display}.{Method}";
                return MethodArguments != null ? $"{name}<{MethodArguments}>" : name;
            }
        }
    }

    /// <summary>
    /// Makes a type description from a type signature.
    /// </summary>
    private sealed class TypeProvider : ISignatureTypeProvider<TypeDesc, object>
    {
        private readonly Dictionary<TypeReferenceHandle, TypeDesc> references = new();
        private readonly Dictionary<TypeSpecificationHandle, TypeDesc> specifications = new();

        public TypeDesc GetPrimitiveType(PrimitiveTypeCode typeCode) => new() { Assembly = CoreLibrary, Namespace = "System", Name = typeCode.ToString() };

        public TypeDesc GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind = 0)
        {
            if (references.TryGetValue(handle, out var type))
            {
                return type;
            }

            var reference = reader.GetTypeReference(handle);
            string name = reader.GetString(reference.Name);
            var scope = reference.ResolutionScope;
            if (scope.Kind == HandleKind.TypeReference)
            {
                var outer = GetTypeFromReference(reader, (TypeReferenceHandle)scope);
                type = new TypeDesc { Assembly = outer.Assembly, Namespace = outer.Namespace, Name = outer.Name + "." + name };
            }
            else
            {
                string assembly = scope.Kind == HandleKind.AssemblyReference ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) : "";
                type = new TypeDesc { Assembly = assembly, Namespace = reader.GetString(reference.Namespace), Name = name };
            }

            references[handle] = type;
            return type;
        }

        public TypeDesc GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var definition = reader.GetTypeDefinition(handle);
            return new TypeDesc { Namespace = reader.GetString(definition.Namespace), Name = reader.GetString(definition.Name) };
        }

        public TypeDesc GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind = 0)
        {
            if (!specifications.TryGetValue(handle, out var type))
            {
                type = reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
                specifications[handle] = type;
            }

            return type;
        }

        public TypeDesc GetGenericInstantiation(TypeDesc genericType, ImmutableArray<TypeDesc> typeArguments) =>
            new() { Assembly = genericType.Assembly, Namespace = genericType.Namespace, Name = genericType.Name, Arguments = Join(typeArguments) };

        public static string Join(ImmutableArray<TypeDesc> types) => string.Join(", ", types.Select(ShortName));

        /// <summary>
        /// A type argument is written without the namespace.
        /// </summary>
        private static string ShortName(TypeDesc type) => type.Arguments != null ? $"{type.Name}<{type.Arguments}>" : type.Name;

        private static TypeDesc WithSuffix(TypeDesc type, string suffix) =>
            new() { Assembly = type.Assembly, Namespace = type.Namespace, Name = ShortName(type) + suffix };

        public TypeDesc GetSZArrayType(TypeDesc elementType) => WithSuffix(elementType, "[]");
        public TypeDesc GetArrayType(TypeDesc elementType, ArrayShape shape) => WithSuffix(elementType, "[" + new string(',', Math.Max(shape.Rank - 1, 0)) + "]");
        public TypeDesc GetPointerType(TypeDesc elementType) => WithSuffix(elementType, "*");
        public TypeDesc GetByReferenceType(TypeDesc elementType) => WithSuffix(elementType, "&");
        public TypeDesc GetPinnedType(TypeDesc elementType) => elementType;
        public TypeDesc GetModifiedType(TypeDesc modifier, TypeDesc unmodifiedType, bool isRequired) => unmodifiedType;
        public TypeDesc GetFunctionPointerType(MethodSignature<TypeDesc> signature) => new() { Assembly = CoreLibrary, Namespace = "System", Name = "(function pointer)" };
        public TypeDesc GetGenericMethodParameter(object genericContext, int index) => new() { Name = "!!" + index };
        public TypeDesc GetGenericTypeParameter(object genericContext, int index) => new() { Name = "!" + index };
    }

    private struct Operand
    {
        public bool IsToken;
        public int Value;
        public string Text;
    }

    /// <summary>
    /// The operands of the IL of a list method: tokens, numbers and strings.
    /// </summary>
    private static List<Operand> ReadOperands(byte[] il, MetadataReader metadata)
    {
        const byte LoadToken = 0xD0;
        const byte LoadInt32 = 0x20;
        const byte LoadInt8 = 0x1F;
        const byte LoadMinusOne = 0x15;
        const byte LoadZero = 0x16;
        const byte LoadEight = 0x1E;
        const byte LoadString = 0x72;

        var operands = new List<Operand>();
        int position = 0;
        while (position < il.Length)
        {
            byte opcode = il[position++];
            if (opcode == LoadToken && position + 4 <= il.Length)
            {
                operands.Add(new Operand { IsToken = true, Value = BitConverter.ToInt32(il, position) });
                position += 4;
            }
            else if (opcode == LoadInt32 && position + 4 <= il.Length)
            {
                operands.Add(new Operand { Value = BitConverter.ToInt32(il, position) });
                position += 4;
            }
            else if (opcode == LoadInt8 && position < il.Length)
            {
                operands.Add(new Operand { Value = (sbyte)il[position++] });
            }
            else if (opcode >= LoadMinusOne && opcode <= LoadEight)
            {
                operands.Add(new Operand { Value = opcode - LoadZero });
            }
            else if (opcode == LoadString && position + 4 <= il.Length)
            {
                var handle = MetadataTokens.UserStringHandle(BitConverter.ToInt32(il, position) & 0xFFFFFF);
                operands.Add(new Operand { Text = metadata.GetUserString(handle) });
                position += 4;
            }
            else
            {
                break;
            }
        }

        return operands;
    }

    /// <summary>
    /// Splits the operands into entries. An entry starts at a token, or at
    /// a string when the list has no tokens.
    /// </summary>
    private static IEnumerable<(Operand first, List<int> numbers, string text)> ReadEntries(List<Operand> operands)
    {
        bool hasTokens = operands.Any(o => o.IsToken);
        int i = 0;
        while (i < operands.Count)
        {
            var first = operands[i++];
            if (hasTokens ? !first.IsToken : first.Text == null)
            {
                continue;
            }

            var numbers = new List<int>();
            string text = first.Text;
            while (i < operands.Count && (hasTokens ? !operands[i].IsToken : operands[i].Text == null))
            {
                if (operands[i].Text != null)
                {
                    text ??= operands[i].Text;
                }
                else
                {
                    numbers.Add(operands[i].Value);
                }

                i++;
            }

            yield return (first, numbers, text);
        }
    }

    public static void Print(Node root, string mstatPath, string mapPath, int rowCount, int brotliQuality, bool withDetails, TextWriter writer)
    {
        using var stream = File.OpenRead(mstatPath);
        using var reader = new PEReader(stream);
        var metadata = reader.GetMetadataReader();
        var provider = new TypeProvider();

        var lists = new Dictionary<string, List<Operand>>();
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            foreach (var methodHandle in metadata.GetTypeDefinition(typeHandle).GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress != 0)
                {
                    lists[metadata.GetString(method.Name)] = ReadOperands(reader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes(), metadata);
                }
            }
        }

        List<Operand> List(string name) => lists.TryGetValue(name, out var list) ? list : new List<Operand>();

        TypeDesc ResolveType(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeReference:
                    return provider.GetTypeFromReference(metadata, (TypeReferenceHandle)handle);
                case HandleKind.TypeSpecification:
                    return provider.GetTypeFromSpecification(metadata, null, (TypeSpecificationHandle)handle);
                case HandleKind.TypeDefinition:
                    return provider.GetTypeFromDefinition(metadata, (TypeDefinitionHandle)handle, 0);
                default:
                    return new TypeDesc { Name = "(not resolved)" };
            }
        }

        Item ResolveMember(EntityHandle handle)
        {
            if (handle.Kind == HandleKind.MethodSpecification)
            {
                var specification = metadata.GetMethodSpecification((MethodSpecificationHandle)handle);
                var item = ResolveMember(specification.Method);
                item.MethodArguments = TypeProvider.Join(specification.DecodeSignature(provider, null));
                return item;
            }

            if (handle.Kind == HandleKind.MemberReference)
            {
                var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
                return new Item { Type = ResolveType(member.Parent), Method = metadata.GetString(member.Name) };
            }

            return new Item { Type = new TypeDesc { Name = "(not resolved)" }, Method = "" };
        }

        var methods = new List<Item>();
        foreach (var (first, numbers, _) in ReadEntries(List("Methods")))
        {
            var item = ResolveMember(MetadataTokens.EntityHandle(first.Value));
            item.Code = numbers.Count > 0 ? numbers[0] : 0;
            item.GcInfo = numbers.Count > 1 ? numbers[1] : 0;
            item.EhInfo = numbers.Count > 2 ? numbers[2] : 0;
            item.Bytes = item.Code + item.GcInfo + item.EhInfo;
            methods.Add(item);
        }

        var types = new List<Item>();
        foreach (var (first, numbers, _) in ReadEntries(List("Types")))
        {
            types.Add(new Item { Type = ResolveType(MetadataTokens.EntityHandle(first.Value)), Bytes = numbers.Count > 0 ? numbers[0] : 0 });
        }

        // A frozen object entry is the token of the type of the object, the
        // length, the index of the name, and then either the token of the
        // type that owns the object or a number in its place. The bytes
        // are counted for the owning type. A string literal has none.
        var frozenObjects = new List<Item>();
        var frozenOperands = List("FrozenObjects");
        var noOwner = new TypeDesc { Assembly = "(frozen objects without an owning type)" };
        int operand = 0;
        while (operand + 2 < frozenOperands.Count)
        {
            if (!frozenOperands[operand].IsToken || frozenOperands[operand + 1].IsToken || frozenOperands[operand + 2].IsToken)
            {
                operand++;
                continue;
            }

            var objectType = ResolveType(MetadataTokens.EntityHandle(frozenOperands[operand].Value));
            long length = frozenOperands[operand + 1].Value;
            TypeDesc owner = null;
            int next = operand + 3;
            if (next < frozenOperands.Count)
            {
                if (frozenOperands[next].IsToken)
                {
                    owner = ResolveType(MetadataTokens.EntityHandle(frozenOperands[next].Value));
                }

                next++;
            }

            frozenObjects.Add(new Item { Type = owner ?? noOwner, Method = objectType.Display, Bytes = length });
            operand = next;
        }

        var fields = new List<Item>();
        foreach (var (first, numbers, _) in ReadEntries(List("RvaFields")))
        {
            var item = ResolveMember(MetadataTokens.EntityHandle(first.Value));
            item.Bytes = numbers.Count > 0 ? numbers[0] : 0;
            fields.Add(item);
        }

        var blobs = ReadEntries(List("Blobs")).Select(e => (name: e.text, bytes: e.numbers.Count > 0 ? (long)e.numbers[0] : 0)).ToList();

        // A resource entry is the token of the assembly, the name and the length
        var resources = new List<(string assembly, string name, long bytes)>();
        var resourceOperands = List("ManifestResources");
        for (int i = 0; i + 2 < resourceOperands.Count; i += 3)
        {
            var handle = MetadataTokens.EntityHandle(resourceOperands[i].Value);
            string assembly = handle.Kind == HandleKind.AssemblyReference ? metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)handle).Name) : "";
            resources.Add((assembly, resourceOperands[i + 1].Text ?? "", resourceOperands[i + 2].Value));
        }

        var layout = mapPath != null && root is PEFile peFile ? NativeAotLayout.Read(peFile, mapPath) : null;
        if (layout != null && !layout.Assign(methods.Count, i => methods[i].Code, i => methods[i].FullName, (i, start) => methods[i].Start = start))
        {
            brotliQuality = -1;
        }

        var version = metadata.GetAssemblyDefinition().Version;
        writer.WriteLine($"{Path.GetFileName(mstatPath)}: format {version.Major}.{version.Minor}, {methods.Count:N0} methods, {types.Count:N0} types, {frozenObjects.Count:N0} frozen objects");
        writer.WriteLine();

        // Per assembly
        var assemblies = new Dictionary<string, long[]>();
        long[] Row(string assembly)
        {
            if (!assemblies.TryGetValue(assembly, out var row))
            {
                row = new long[8];
                assemblies[assembly] = row;
            }

            return row;
        }

        const int Count = 0, Code = 1, GcInfo = 2, EhInfo = 3, TypeData = 4, Frozen = 5, FieldData = 6, Resources = 7;
        foreach (var item in methods)
        {
            var row = Row(item.Type.Assembly);
            row[Count]++;
            row[Code] += item.Code;
            row[GcInfo] += item.GcInfo;
            row[EhInfo] += item.EhInfo;
        }

        foreach (var item in types)
        {
            Row(item.Type.Assembly)[TypeData] += item.Bytes;
        }

        foreach (var item in frozenObjects)
        {
            Row(item.Type.Assembly)[Frozen] += item.Bytes;
        }

        foreach (var item in fields)
        {
            Row(item.Type.Assembly)[FieldData] += item.Bytes;
        }

        foreach (var (assembly, _, bytes) in resources)
        {
            Row(assembly)[Resources] += bytes;
        }

        bool isCompressed = brotliQuality >= 0 && layout != null;
        var fileBytes = isCompressed ? layout.FileBytes : null;
        var ordered = assemblies.OrderByDescending(a => a.Value.Skip(1).Sum()).ToArray();
        var compressed = new long[ordered.Length];
        if (isCompressed)
        {
            var byAssembly = methods.Where(m => m.Start >= 0).ToLookup(m => m.Type.Assembly);
            System.Threading.Tasks.Parallel.For(0, ordered.Length, i =>
                compressed[i] = SizeTables.Compress(fileBytes, byAssembly[ordered[i].Key].Select(m => (m.Start, (int)m.Code)), brotliQuality));
        }

        string compressedHeader = isCompressed ? $" {"code br",10}" : "";
        writer.WriteLine($"{"Assembly",-42} {"methods",8} {"code",11} {"GC info",10} {"EH info",9} {"MethodTables",12} {"frozen obj.",11} {"field data",10} {"resources",10} {"total",11}{compressedHeader}");
        foreach (var (assembly, row) in ordered.Select(a => (a.Key, a.Value)))
        {
            string compressedText = isCompressed ? $" {compressed[Array.FindIndex(ordered, a => a.Key == assembly)],10:N0}" : "";
            string name = assembly.Length > 0 ? assembly : "(no assembly)";
            writer.WriteLine($"{name,-42} {row[Count],8:N0} {row[Code],11:N0} {row[GcInfo],10:N0} {row[EhInfo],9:N0} {row[TypeData],12:N0} {row[Frozen],11:N0} {row[FieldData],10:N0} {row[Resources],10:N0} {row.Skip(1).Sum(),11:N0}{compressedText}");
        }

        long Total(int column) => assemblies.Values.Sum(r => r[column]);
        long allCode = Total(Code);
        long perAssembly = assemblies.Values.Sum(r => r.Skip(1).Sum());
        string compressedTotal = isCompressed ? $" {compressed.Sum(),10:N0}" : "";
        writer.WriteLine($"{"All assemblies",-42} {Total(Count),8:N0} {allCode,11:N0} {Total(GcInfo),10:N0} {Total(EhInfo),9:N0} {Total(TypeData),12:N0} {Total(Frozen),11:N0} {Total(FieldData),10:N0} {Total(Resources),10:N0} {perAssembly,11:N0}{compressedTotal}");

        // The list of other data is by kind, for all assemblies together.
        // The frozen objects, the field data and the resources of the
        // table above are counted in it again, under their kinds.
        long otherBytes = blobs.Sum(b => b.bytes);
        long listed = allCode + Total(GcInfo) + Total(EhInfo) + Total(TypeData) + otherBytes;
        long fileLength = root.Buffer.Length;
        writer.WriteLine();
        writer.WriteLine("Other data by kind, for all assemblies together");
        foreach (var (name, bytes) in blobs.OrderByDescending(b => b.bytes).Take(rowCount))
        {
            writer.WriteLine($"{bytes,12:N0}  {name}");
        }

        if (blobs.Count > rowCount)
        {
            writer.WriteLine($"{blobs.OrderByDescending(b => b.bytes).Skip(rowCount).Sum(b => b.bytes),12:N0}  ({blobs.Count - rowCount:N0} more)");
        }

        writer.WriteLine();
        writer.WriteLine($"File:                              {fileLength,12:N0} bytes");
        writer.WriteLine($"Listed in the mstat file:          {listed,12:N0}  ({SizeTables.Percent(listed, fileLength)})");
        writer.WriteLine($"  code of the methods:             {allCode,12:N0}");
        writer.WriteLine($"  GC info and EH info:             {Total(GcInfo) + Total(EhInfo),12:N0}");
        writer.WriteLine($"  MethodTables:                    {Total(TypeData),12:N0}");
        writer.WriteLine($"  other data:                      {otherBytes,12:N0}  (with the frozen objects, the field data and the resources)");
        writer.WriteLine($"Not listed:                        {fileLength - listed,12:N0}  (the native runtime, unwind data, import and relocation tables, headers, padding)");

        if (layout != null)
        {
            writer.WriteLine();
            writer.WriteLine(layout.Message);
            writer.WriteLine(layout.DataMessage);
            if (withDetails)
            {
                foreach (string line in layout.Details)
                {
                    writer.WriteLine("  " + line);
                }
            }
        }

        writer.WriteLine();
        writer.WriteLine(SizeTables.Legend1);
        writer.WriteLine(SizeTables.Legend2 + " The bytes of a method are its code, GC info and EH info.");
        if (isCompressed)
        {
            writer.WriteLine("\"br\" is the length of the code of the row compressed alone.");
        }

        long allMethodBytes = methods.Sum(m => m.Bytes);
        void Groups(string title, string keyHeader, IReadOnlyList<Item> items, Func<Item, string> getKey, long all, bool withCode)
        {
            SizeTables.PrintGroups(
                writer, title, keyHeader, items, getKey,
                m => m.Bytes,
                m => m.IsInstance,
                m => m.Instantiation,
                rowCount, all, "share",
                withCode ? fileBytes : null,
                m => (Math.Max(m.Start, 0), m.Start >= 0 ? (int)m.Code : 0),
                brotliQuality);
        }

        string Namespace(Item item) => item.Type.Namespace.Length > 0 ? item.Type.Namespace : "(no namespace)";
        string TypeKey(Item item) => $"{item.Type.Definition}  ({item.Type.Assembly})";

        Groups("Largest namespaces, by the bytes of their methods", "Namespace", methods, Namespace, allMethodBytes, true);
        Groups("Largest types, by the bytes of their methods", "Type (assembly that declares it)", methods, TypeKey, allMethodBytes, true);
        Groups("Types with the most bytes in methods of generic instances", "Type (assembly that declares it)", methods.Where(m => m.IsInstance).ToArray(), TypeKey, allMethodBytes, true);

        writer.WriteLine();
        writer.WriteLine("Largest methods");
        writer.WriteLine($"{"bytes",10} {"code",10}  Method");
        foreach (var item in methods.OrderByDescending(m => m.Bytes).Take(rowCount))
        {
            writer.WriteLine($"{item.Bytes,10:N0} {item.Code,10:N0}  {item.FullName}");
        }

        long allTypeBytes = types.Sum(t => t.Bytes);
        Groups("Largest namespaces, by the bytes of the MethodTables of their types", "Namespace", types, Namespace, allTypeBytes, false);
        Groups("Types with the most bytes in MethodTables (a generic type has one for each instance)", "Type (assembly that declares it)", types, TypeKey, allTypeBytes, false);
    }
}
