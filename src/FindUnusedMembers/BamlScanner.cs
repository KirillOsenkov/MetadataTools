using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using System.Text;
using Confuser.Renamer.BAML;
using Mono.Cecil;

namespace FindUnusedMembers
{
    public class BamlTypeName
    {
        public string Assembly { get; set; }
        public string FullName { get; set; }
        public string Member { get; set; }

        public override string ToString() => FullName + (Member != null ? "." + Member : "") + ", " + Assembly;
    }

    public enum BamlUseKind
    {
        Type,
        InstantiatedType,
        Member,
        String
    }

    public class BamlUse
    {
        public BamlUseKind Kind { get; set; }
        public BamlTypeName Type { get; set; }
        public string Name { get; set; }
        public BamlTypeName Condition { get; set; }
        public string Document { get; set; }
    }

    public class BamlScanner
    {
        public static IEnumerable<(string Name, byte[] Data)> GetBamlStreams(ModuleDefinition module)
        {
            foreach (var resource in module.Resources.OfType<EmbeddedResource>())
            {
                if (!resource.Name.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var streams = new List<(string, byte[])>();
                try
                {
                    using var stream = resource.GetResourceStream();
                    using var reader = new ResourceReader(stream);
                    var enumerator = reader.GetEnumerator();
                    var names = new List<string>();
                    while (enumerator.MoveNext())
                    {
                        if (enumerator.Key is string name && name.EndsWith(".baml", StringComparison.OrdinalIgnoreCase))
                        {
                            names.Add(name);
                        }
                    }

                    foreach (var name in names)
                    {
                        reader.GetResourceData(name, out _, out byte[] data);
                        if (data == null || data.Length < 4)
                        {
                            continue;
                        }

                        streams.Add((name, data.Skip(4).ToArray()));
                    }
                }
                catch
                {
                }

                foreach (var item in streams)
                {
                    yield return item;
                }
            }
        }

        public static void Dump(string assemblyFile, string nameFilter, TextWriter writer)
        {
            var module = ModuleDefinition.ReadModule(assemblyFile);
            foreach (var (name, data) in GetBamlStreams(module))
            {
                if (name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                writer.WriteLine(name);
                var document = Read(data);
                int depth = 0;
                for (int i = 0; i < document.Count; i++)
                {
                    var record = document[i];
                    if (record is ElementEndRecord || record is KeyElementEndRecord || record is PropertyComplexEndRecord ||
                        record is PropertyListEndRecord || record is PropertyDictionaryEndRecord || record is PropertyArrayEndRecord ||
                        record is StaticResourceEndRecord)
                    {
                        depth = Math.Max(0, depth - 1);
                    }

                    var properties = record.GetType().GetProperties()
                        .Where(p => p.Name != "Type" && p.Name != "Position" && p.Name != "Record" && p.GetIndexParameters().Length == 0)
                        .Select(p =>
                        {
                            var value = p.GetValue(record);
                            if (value is byte[] bytes)
                            {
                                value = BitConverter.ToString(bytes);
                            }

                            if (value is ushort number && p.Name.EndsWith("Flags"))
                            {
                                value = "0x" + number.ToString("X4");
                            }

                            return p.Name + "=" + value;
                        });
                    string deferred = record is IBamlDeferRecord defer && defer.Record != null ? " -> #" + document.IndexOf(defer.Record) : "";
                    writer.WriteLine($"{i,5} {new string(' ', depth * 2)}{record.Type} {string.Join(" ", properties)}{deferred}");

                    if ((record.GetType() == typeof(ElementStartRecord) || record is NamedElementStartRecord) ||
                        (record is KeyElementStartRecord) || record.GetType() == typeof(PropertyComplexStartRecord) ||
                        record is PropertyListStartRecord || record is PropertyDictionaryStartRecord || record is PropertyArrayStartRecord ||
                        record is StaticResourceStartRecord)
                    {
                        depth++;
                    }
                }
            }
        }

        public static BamlDocument Read(byte[] data)
        {
            using var stream = new MemoryStream(data);
            return BamlReader.ReadDocument(stream);
        }

        private readonly BamlDocument document;
        private readonly string documentName;
        private readonly Dictionary<ushort, string> assemblies = new Dictionary<ushort, string>();
        private readonly Dictionary<ushort, BamlTypeName> types = new Dictionary<ushort, BamlTypeName>();
        private readonly Dictionary<ushort, (ushort OwnerTypeId, string Name)> attributes = new Dictionary<ushort, (ushort, string)>();
        private readonly Dictionary<ushort, string> strings = new Dictionary<ushort, string>();
        private readonly Dictionary<string, (string Namespace, string Assembly)> prefixes = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        private readonly string documentAssembly;
        private readonly List<BamlUse> uses = new List<BamlUse>();
        private BamlTypeName[] conditions;

        public BamlScanner(BamlDocument document, string documentAssembly, string documentName)
        {
            this.document = document;
            this.documentAssembly = documentAssembly;
            this.documentName = documentAssembly + ":" + documentName;
        }

        public IReadOnlyList<string> ReferencedAssemblies => assemblies.Values.ToArray();

        public IReadOnlyList<BamlUse> Scan()
        {
            BuildTables();
            ComputeConditions();

            for (int i = 0; i < document.Count; i++)
            {
                VisitRecord(document[i], conditions[i]);
            }

            return uses;
        }

        private void BuildTables()
        {
            foreach (var record in document)
            {
                switch (record)
                {
                    case AssemblyInfoRecord assembly:
                        assemblies[assembly.AssemblyId] = GetSimpleName(assembly.AssemblyFullName);
                        break;
                    case TypeInfoRecord type:
                        assemblies.TryGetValue((ushort)(type.AssemblyId & 0x0FFF), out var assemblyName);
                        types[type.TypeId] = new BamlTypeName
                        {
                            Assembly = assemblyName,
                            FullName = NormalizeTypeName(type.TypeFullName)
                        };
                        break;
                    case AttributeInfoRecord attribute:
                        attributes[attribute.AttributeId] = (attribute.OwnerTypeId, attribute.Name);
                        break;
                    case StringInfoRecord stringInfo:
                        strings[stringInfo.StringId] = stringInfo.Value;
                        break;
                    case XmlnsPropertyRecord xmlns:
                        AddPrefix(xmlns.Prefix ?? "", xmlns.XmlNamespace);
                        break;
                }
            }

            foreach (var type in types.Values)
            {
                if (type.Assembly == null)
                {
                    type.Assembly = "";
                }
            }
        }

        private void AddPrefix(string prefix, string xmlNamespace)
        {
            const string clrNamespace = "clr-namespace:";
            if (xmlNamespace == null || !xmlNamespace.StartsWith(clrNamespace, StringComparison.Ordinal))
            {
                return;
            }

            var parts = xmlNamespace.Substring(clrNamespace.Length).Split(';');
            string assembly = documentAssembly;
            foreach (var part in parts.Skip(1))
            {
                const string assemblyPrefix = "assembly=";
                if (part.StartsWith(assemblyPrefix, StringComparison.Ordinal))
                {
                    assembly = GetSimpleName(part.Substring(assemblyPrefix.Length));
                }
            }

            prefixes[prefix] = (parts[0].Trim(), assembly);
        }

        private BamlTypeName ParseStaticReference(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            text = text.Trim();
            string prefix = "";
            int colon = text.IndexOf(':');
            if (colon >= 0)
            {
                prefix = text.Substring(0, colon);
                text = text.Substring(colon + 1);
            }

            if (!prefixes.TryGetValue(prefix, out var mapping) || text.Length == 0 || !text.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.'))
            {
                return null;
            }

            string fullName = string.IsNullOrEmpty(mapping.Namespace) ? text : mapping.Namespace + "." + text;
            int dot = text.LastIndexOf('.');
            if (dot < 0)
            {
                return new BamlTypeName { Assembly = mapping.Assembly, FullName = fullName };
            }

            return new BamlTypeName
            {
                Assembly = mapping.Assembly,
                FullName = fullName.Substring(0, fullName.Length - (text.Length - dot)),
                Member = text.Substring(dot + 1)
            };
        }

        private static string GetSimpleName(string assemblyFullName)
        {
            if (assemblyFullName == null)
            {
                return "";
            }

            int comma = assemblyFullName.IndexOf(',');
            return (comma >= 0 ? assemblyFullName.Substring(0, comma) : assemblyFullName).Trim();
        }

        private static string NormalizeTypeName(string typeName)
        {
            if (typeName == null)
            {
                return "";
            }

            int bracket = typeName.IndexOf('[');
            if (bracket >= 0)
            {
                typeName = typeName.Substring(0, bracket);
            }

            return typeName.Replace('+', '/');
        }

        private void ComputeConditions()
        {
            conditions = new BamlTypeName[document.Count];
            var indexOf = new Dictionary<BamlRecord, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < document.Count; i++)
            {
                indexOf[document[i]] = i;
            }

            for (int i = 0; i < document.Count; i++)
            {
                var record = document[i];
                if (record is not IBamlDeferRecord deferRecord || deferRecord.Record == null)
                {
                    continue;
                }

                BamlTypeName condition = null;
                int keyEnd = i;
                if (record is KeyElementStartRecord)
                {
                    keyEnd = FindKeyElementEnd(i);
                    condition = GetSingleTypeUsedIn(i, keyEnd);
                    if (condition == null)
                    {
                        var texts = Enumerable.Range(i, keyEnd - i + 1).Select(k => document[k]).OfType<TextRecord>().ToArray();
                        if (texts.Length == 1)
                        {
                            condition = ParseStaticReference(texts[0].Value);
                        }
                    }
                }
                else if (record is DefAttributeKeyTypeRecord keyType)
                {
                    types.TryGetValue(keyType.TypeId, out condition);
                }

                if (condition == null)
                {
                    continue;
                }

                for (int k = i; k <= keyEnd; k++)
                {
                    conditions[k] = condition;
                }

                if (!indexOf.TryGetValue(deferRecord.Record, out int valueStart))
                {
                    continue;
                }

                int valueEnd = FindElementEnd(valueStart);
                for (int k = valueStart; k <= valueEnd; k++)
                {
                    conditions[k] = condition;
                }
            }
        }

        private int FindKeyElementEnd(int start)
        {
            int depth = 0;
            for (int i = start; i < document.Count; i++)
            {
                if (document[i] is KeyElementStartRecord)
                {
                    depth++;
                }
                else if (document[i] is KeyElementEndRecord)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return start;
        }

        private int FindElementEnd(int start)
        {
            while (start < document.Count && !IsElementStart(document[start]))
            {
                if (document[start] is ElementEndRecord)
                {
                    return start;
                }

                start++;
            }

            if (start >= document.Count)
            {
                return document.Count - 1;
            }

            int depth = 0;
            for (int i = start; i < document.Count; i++)
            {
                if (IsElementStart(document[i]))
                {
                    depth++;
                }
                else if (document[i] is ElementEndRecord)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return document.Count - 1;
        }

        private static bool IsElementStart(BamlRecord record)
        {
            return record.GetType() == typeof(ElementStartRecord) || record.GetType() == typeof(NamedElementStartRecord);
        }

        private BamlTypeName GetSingleTypeUsedIn(int start, int end)
        {
            BamlTypeName result = null;
            for (int i = start; i <= end; i++)
            {
                foreach (var typeId in GetTypeIds(document[i]))
                {
                    if (types.TryGetValue(typeId, out var type))
                    {
                        if (result != null && result != type)
                        {
                            return null;
                        }

                        result = type;
                    }
                }
            }

            return result;
        }

        private IEnumerable<ushort> GetTypeIds(BamlRecord record)
        {
            switch (record)
            {
                case ElementStartRecord elementStart:
                    yield return elementStart.TypeId;
                    break;
                case PropertyTypeReferenceRecord typeReference:
                    yield return typeReference.TypeId;
                    break;
                case ConstructorParameterTypeRecord parameterType:
                    yield return parameterType.TypeId;
                    break;
                case PropertyWithExtensionRecord extension when (extension.Flags & 0x4000) != 0 || (extension.Flags & 0x0FFF) == TypeExtensionKnownTypeId:
                    yield return extension.ValueId;
                    break;
            }
        }

        private const int StaticExtensionKnownTypeId = 602;
        private const int TypeExtensionKnownTypeId = 691;

        private void VisitRecord(BamlRecord record, BamlTypeName condition)
        {
            switch (record)
            {
                case NamedElementStartRecord namedElement:
                    AddType(namedElement.TypeId, instantiated: true, condition);
                    break;
                case DefAttributeKeyTypeRecord keyType:
                    AddType(keyType.TypeId, instantiated: false, condition);
                    break;
                case ElementStartRecord elementStart:
                    AddType(elementStart.TypeId, instantiated: elementStart.GetType() == typeof(ElementStartRecord), condition);
                    break;
                case PropertyTypeReferenceRecord typeReference:
                    AddAttribute(typeReference.AttributeId, condition);
                    AddType(typeReference.TypeId, instantiated: false, condition);
                    break;
                case PropertyStringReferenceRecord stringReference:
                    AddAttribute(stringReference.AttributeId, condition);
                    AddStringId(stringReference.StringId, condition);
                    break;
                case PropertyComplexStartRecord complexStart:
                    AddAttribute(complexStart.AttributeId, condition);
                    break;
                case ConstructorParameterTypeRecord parameterType:
                    AddType(parameterType.TypeId, instantiated: false, condition);
                    break;
                case PropertyWithConverterRecord propertyWithConverter:
                    AddAttribute(propertyWithConverter.AttributeId, condition);
                    AddType(propertyWithConverter.ConverterTypeId, instantiated: true, condition);
                    AddString(propertyWithConverter.Value, condition);
                    break;
                case PropertyRecord property:
                    AddAttribute(property.AttributeId, condition);
                    AddString(property.Value, condition);
                    break;
                case TextWithConverterRecord textWithConverter:
                    AddType(textWithConverter.ConverterTypeId, instantiated: true, condition);
                    AddString(textWithConverter.Value, condition);
                    break;
                case TextWithIdRecord textWithId:
                    AddStringId(textWithId.ValueId, condition);
                    break;
                case TextRecord text:
                    AddString(text.Value, condition);
                    break;
                case PropertyWithExtensionRecord extension:
                    AddAttribute(extension.AttributeId, condition);
                    int extensionTypeId = extension.Flags & 0x0FFF;
                    if (extensionTypeId == StaticExtensionKnownTypeId)
                    {
                        AddAttribute(extension.ValueId, condition);
                    }
                    else if (extensionTypeId == TypeExtensionKnownTypeId)
                    {
                        AddType(extension.ValueId, instantiated: false, condition);
                    }
                    else if ((extension.Flags & 0x4000) != 0)
                    {
                        AddType(extension.ValueId, instantiated: false, condition);
                    }
                    else if ((extension.Flags & 0x2000) != 0)
                    {
                        AddAttribute(extension.ValueId, condition);
                    }
                    else
                    {
                        AddStringId(extension.ValueId, condition);
                    }

                    break;
                case PropertyCustomRecord custom:
                    AddAttribute(custom.AttributeId, condition);
                    AddCustomData(custom.Data, condition);
                    break;
                case PropertyWithStaticResourceIdRecord staticResource:
                    AddAttribute(staticResource.AttributeId, condition);
                    break;
                case ContentPropertyRecord contentProperty:
                    AddAttribute(contentProperty.AttributeId, condition);
                    break;
                case RoutedEventRecord routedEvent:
                    AddAttribute(routedEvent.AttributeId, condition);
                    AddString(routedEvent.Value, condition);
                    break;
                case DefAttributeRecord defAttribute:
                    AddString(defAttribute.Value, condition);
                    break;
                case LiteralContentRecord literal:
                    AddString(literal.Value, condition);
                    break;
                case OptimizedStaticResourceRecord optimized:
                    if ((optimized.Flags & 0x02) != 0)
                    {
                        AddType(optimized.ValueId, instantiated: false, condition);
                    }
                    else if ((optimized.Flags & 0x04) != 0)
                    {
                        AddAttribute(optimized.ValueId, condition);
                    }
                    else
                    {
                        AddStringId(optimized.ValueId, condition);
                    }

                    break;
            }
        }

        private void AddCustomData(byte[] data, BamlTypeName condition)
        {
            if (data == null || data.Length < 2)
            {
                return;
            }

            ushort id = BitConverter.ToUInt16(data, 0);
            if (data.Length == 2)
            {
                AddAttribute(id, condition);
                return;
            }

            if (data.Length > 3 && types.TryGetValue(id, out var ownerType))
            {
                try
                {
                    using var reader = new BinaryReader(new MemoryStream(data, 2, data.Length - 2), Encoding.UTF8);
                    var name = reader.ReadString();
                    uses.Add(new BamlUse
                    {
                        Kind = BamlUseKind.Member,
                        Type = ownerType,
                        Name = name,
                        Condition = condition,
                        Document = documentName
                    });
                }
                catch
                {
                }
            }

            var sb = new StringBuilder();
            foreach (var b in data)
            {
                char c = (char)b;
                if (char.IsLetterOrDigit(c) || c == '_' || c == '.')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append(' ');
                }
            }

            AddString(sb.ToString(), condition);
        }

        private void AddType(ushort typeId, bool instantiated, BamlTypeName condition)
        {
            if (!types.TryGetValue(typeId, out var type))
            {
                return;
            }

            uses.Add(new BamlUse
            {
                Kind = instantiated ? BamlUseKind.InstantiatedType : BamlUseKind.Type,
                Type = type,
                Condition = condition,
                Document = documentName
            });
        }

        private void AddAttribute(ushort attributeId, BamlTypeName condition)
        {
            if (!attributes.TryGetValue(attributeId, out var attribute))
            {
                return;
            }

            if (!types.TryGetValue(attribute.OwnerTypeId, out var owner))
            {
                return;
            }

            uses.Add(new BamlUse
            {
                Kind = BamlUseKind.Member,
                Type = owner,
                Name = attribute.Name,
                Condition = condition,
                Document = documentName
            });
        }

        private void AddStringId(ushort stringId, BamlTypeName condition)
        {
            if (strings.TryGetValue(stringId, out var value))
            {
                AddString(value, condition);
            }
        }

        private void AddString(string value, BamlTypeName condition)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (value.IndexOf(':') > 0)
            {
                var reference = ParseStaticReference(value);
                if (reference != null)
                {
                    uses.Add(new BamlUse
                    {
                        Kind = reference.Member == null ? BamlUseKind.Type : BamlUseKind.Member,
                        Type = new BamlTypeName { Assembly = reference.Assembly, FullName = reference.FullName },
                        Name = reference.Member,
                        Condition = condition,
                        Document = documentName
                    });

                    if (reference.Member != null)
                    {
                        uses.Add(new BamlUse
                        {
                            Kind = BamlUseKind.Type,
                            Type = new BamlTypeName { Assembly = reference.Assembly, FullName = reference.FullName + "." + reference.Member },
                            Condition = condition,
                            Document = documentName
                        });
                    }

                    return;
                }
            }

            uses.Add(new BamlUse
            {
                Kind = BamlUseKind.String,
                Name = value,
                Condition = condition,
                Document = documentName
            });
        }
    }
}
