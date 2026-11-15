using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace FindUnusedMembers
{
    public class Reason
    {
        public IMemberDefinition From { get; set; }
        public string Text { get; set; }
    }

    public class ReflectionSite
    {
        public string Assembly { get; set; }
        public string Method { get; set; }
        public string Api { get; set; }
        public List<string> Strings { get; set; } = new List<string>();
    }

    public class Analyzer
    {
        private readonly Resolver resolver;
        private readonly List<AssemblyDefinition> targets = new List<AssemblyDefinition>();
        private readonly HashSet<string> targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<ModuleDefinition> targetModules = new HashSet<ModuleDefinition>();
        private readonly Dictionary<string, TypeDefinition> targetTypesByFullName = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<IMemberDefinition>> targetMembersByName = new Dictionary<string, List<IMemberDefinition>>(StringComparer.Ordinal);
        private readonly Dictionary<MethodDefinition, IMemberDefinition> accessorOwners = new Dictionary<MethodDefinition, IMemberDefinition>();
        private readonly HashSet<TypeDefinition> interopTypes = new HashSet<TypeDefinition>();

        private readonly HashSet<IMemberDefinition> marked = new HashSet<IMemberDefinition>();
        private readonly List<TypeDefinition> markedTypes = new List<TypeDefinition>();
        private readonly Dictionary<IMemberDefinition, Reason> reasons = new Dictionary<IMemberDefinition, Reason>();
        private readonly Queue<IMemberDefinition> worklist = new Queue<IMemberDefinition>();
        private readonly Dictionary<IMemberDefinition, List<Action>> pendingByMember = new Dictionary<IMemberDefinition, List<Action>>();
        private readonly HashSet<FieldDefinition> writtenFields = new HashSet<FieldDefinition>();
        private readonly Dictionary<MethodDefinition, VirtualInfo> virtualInfos = new Dictionary<MethodDefinition, VirtualInfo>();
        private readonly HashSet<TypeDefinition> virtualInfoComputed = new HashSet<TypeDefinition>();
        private readonly HashSet<Guid> visitedConsumers = new HashSet<Guid>();

        /// <summary>
        /// When true, using one accessor of a property keeps the other accessors too, so that code reached only
        /// from those accessors (for example the backing field read by a getter) is not reported either.
        /// </summary>
        public bool KeepAllAccessors { get; set; }

        public List<ReflectionSite> ReflectionSites { get; } = new List<ReflectionSite>();
        public List<string> Consumers { get; } = new List<string>();
        public List<string> BamlConsumers { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public IReadOnlyList<AssemblyDefinition> Targets => targets;

        private class VirtualInfo
        {
            public List<MethodDefinition> Bases { get; } = new List<MethodDefinition>();
            public string External { get; set; }
        }

        public Analyzer(Resolver resolver)
        {
            this.resolver = resolver;
        }

        public void AddTarget(string filePath)
        {
            var readerParameters = new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
                ReadSymbols = true
            };

            AssemblyDefinition assembly;
            try
            {
                assembly = AssemblyDefinition.ReadAssembly(filePath, readerParameters);
            }
            catch
            {
                readerParameters.ReadSymbols = false;
                assembly = AssemblyDefinition.ReadAssembly(filePath, readerParameters);
            }

            targets.Add(assembly);
            targetNames.Add(assembly.Name.Name);
            resolver.RegisterTarget(assembly);
            foreach (var module in assembly.Modules)
            {
                targetModules.Add(module);
                foreach (var type in GetAllTypes(module))
                {
                    targetTypesByFullName[type.FullName] = type;
                    IndexMembers(type);
                }
            }
        }

        public bool IsTargetAssemblyName(string name) => targetNames.Contains(name);

        private void IndexMembers(TypeDefinition type)
        {
            foreach (var property in type.Properties)
            {
                AddMemberByName(property.Name, property);
                if (property.GetMethod != null)
                {
                    accessorOwners[property.GetMethod] = property;
                }

                if (property.SetMethod != null)
                {
                    accessorOwners[property.SetMethod] = property;
                }
            }

            foreach (var @event in type.Events)
            {
                AddMemberByName(@event.Name, @event);
                if (@event.AddMethod != null)
                {
                    accessorOwners[@event.AddMethod] = @event;
                }

                if (@event.RemoveMethod != null)
                {
                    accessorOwners[@event.RemoveMethod] = @event;
                }

                if (@event.InvokeMethod != null)
                {
                    accessorOwners[@event.InvokeMethod] = @event;
                }
            }

            foreach (var field in type.Fields)
            {
                AddMemberByName(field.Name, field);
            }

            foreach (var method in type.Methods)
            {
                if (!method.IsConstructor)
                {
                    AddMemberByName(method.Name, method);
                }
            }
        }

        private void AddMemberByName(string name, IMemberDefinition member)
        {
            if (!targetMembersByName.TryGetValue(name, out var list))
            {
                list = new List<IMemberDefinition>();
                targetMembersByName[name] = list;
            }

            list.Add(member);
        }

        public static IEnumerable<TypeDefinition> GetAllTypes(ModuleDefinition module)
        {
            foreach (var type in module.Types)
            {
                foreach (var nested in GetTypeAndNested(type))
                {
                    yield return nested;
                }
            }
        }

        private static IEnumerable<TypeDefinition> GetTypeAndNested(TypeDefinition type)
        {
            yield return type;
            foreach (var nested in type.NestedTypes)
            {
                foreach (var item in GetTypeAndNested(nested))
                {
                    yield return item;
                }
            }
        }

        public bool IsMarked(IMemberDefinition member) => marked.Contains(member);

        public bool IsWritten(FieldDefinition field) => writtenFields.Contains(field);

        public bool IsAccessor(MethodDefinition method) => accessorOwners.ContainsKey(method);

        public bool IsReferencedByConsumer(IMemberDefinition member) => referencedByConsumers.Contains(member);

        private const string ConsumerReferenceText = "referenced from ";
        private readonly HashSet<IMemberDefinition> referencedByConsumers = new HashSet<IMemberDefinition>();

        public Reason GetReason(IMemberDefinition member) => reasons.TryGetValue(member, out var reason) ? reason : null;

        private bool IsTarget(IMemberDefinition member)
        {
            switch (member)
            {
                case null:
                    return false;
                case TypeDefinition type:
                    return targetModules.Contains(type.Module);
                default:
                    return member.DeclaringType != null && targetModules.Contains(member.DeclaringType.Module);
            }
        }

        private void Mark(IMemberDefinition member, IMemberDefinition from, string text)
        {
            if (member == null || !IsTarget(member))
            {
                return;
            }

            if (from == null && text.StartsWith(ConsumerReferenceText, StringComparison.Ordinal))
            {
                referencedByConsumers.Add(member);
            }

            if (marked.Add(member))
            {
                reasons[member] = new Reason { From = from, Text = text };
                worklist.Enqueue(member);
                if (pendingByMember.Remove(member, out var actions))
                {
                    foreach (var action in actions)
                    {
                        action();
                    }
                }
            }
        }

        private readonly HashSet<MethodDefinition> keptAccessors = new HashSet<MethodDefinition>();

        private void ProcessKeptAccessor(MethodDefinition accessor)
        {
            if (accessor != null && !marked.Contains(accessor) && keptAccessors.Add(accessor))
            {
                worklist.Enqueue(accessor);
            }
        }

        private void WhenMarked(IMemberDefinition condition, Action action)
        {
            if (condition == null || marked.Contains(condition))
            {
                action();
                return;
            }

            if (!pendingByMember.TryGetValue(condition, out var list))
            {
                list = new List<Action>();
                pendingByMember[condition] = list;
            }

            list.Add(action);
        }

        #region Resolution

        private bool IsTargetScope(IMetadataScope scope)
        {
            switch (scope)
            {
                case ModuleDefinition module:
                    return targetModules.Contains(module);
                case AssemblyNameReference assemblyName:
                    return targetNames.Contains(assemblyName.Name);
                default:
                    return false;
            }
        }

        private TypeDefinition TryResolve(TypeReference type)
        {
            if (type == null)
            {
                return null;
            }

            if (type is TypeDefinition definition)
            {
                return definition;
            }

            try
            {
                return type.Resolve();
            }
            catch
            {
                return null;
            }
        }

        private MethodDefinition TryResolve(MethodReference method)
        {
            if (method is MethodDefinition definition)
            {
                return definition;
            }

            try
            {
                return method.Resolve();
            }
            catch
            {
                return null;
            }
        }

        private FieldDefinition TryResolve(FieldReference field)
        {
            if (field is FieldDefinition definition)
            {
                return definition;
            }

            try
            {
                return field.Resolve();
            }
            catch
            {
                return null;
            }
        }

        private TypeDefinition ResolveTargetType(TypeReference type)
        {
            if (type == null || type is GenericParameter)
            {
                return null;
            }

            var element = type.GetElementType();
            if (element is TypeDefinition definition)
            {
                return IsTarget(definition) ? definition : null;
            }

            if (!IsTargetScope(element.Scope))
            {
                return null;
            }

            var resolved = TryResolve(element);
            if (resolved == null)
            {
                Warn("Unable to resolve type " + element.FullName);
                return null;
            }

            return IsTarget(resolved) ? resolved : null;
        }

        private readonly HashSet<string> warnedAbout = new HashSet<string>();

        private void Warn(string text)
        {
            if (warnedAbout.Add(text))
            {
                Warnings.Add(text);
            }
        }

        #endregion

        #region Reference marking

        private void MarkTypeReference(TypeReference type, IMemberDefinition from, string text)
        {
            switch (type)
            {
                case null:
                case GenericParameter:
                    return;
                case GenericInstanceType genericInstance:
                    MarkTypeReference(genericInstance.ElementType, from, text);
                    foreach (var argument in genericInstance.GenericArguments)
                    {
                        MarkTypeReference(argument, from, text);
                    }

                    return;
                case IModifierType modifier:
                    MarkTypeReference(modifier.ModifierType, from, text);
                    MarkTypeReference(modifier.ElementType, from, text);
                    return;
                case FunctionPointerType functionPointer:
                    MarkTypeReference(functionPointer.ReturnType, from, text);
                    foreach (var parameter in functionPointer.Parameters)
                    {
                        MarkTypeReference(parameter.ParameterType, from, text);
                    }

                    return;
                case TypeSpecification specification:
                    MarkTypeReference(specification.ElementType, from, text);
                    return;
            }

            var definition = ResolveTargetType(type);
            if (definition != null)
            {
                Mark(definition, from, text);
            }
        }

        private void MarkMethodReference(MethodReference method, IMemberDefinition from, string text)
        {
            if (method is GenericInstanceMethod genericInstance)
            {
                foreach (var argument in genericInstance.GenericArguments)
                {
                    MarkTypeReference(argument, from, text);
                }

                MarkDefaultConstructorsForConstraints(genericInstance, from);
                method = genericInstance.ElementMethod;
            }

            MarkTypeReference(method.DeclaringType, from, text);

            if (method is MethodDefinition definition)
            {
                Mark(definition, from, text);
                return;
            }

            MarkTypeReference(method.ReturnType, from, text);
            foreach (var parameter in method.Parameters)
            {
                MarkTypeReference(parameter.ParameterType, from, text);
            }

            if (ResolveTargetType(method.DeclaringType) == null)
            {
                return;
            }

            var resolved = TryResolve(method);
            if (resolved == null)
            {
                Warn("Unable to resolve method " + method.FullName);
                return;
            }

            Mark(resolved, from, text);
        }

        private void MarkDefaultConstructorsForConstraints(GenericInstanceMethod genericInstance, IMemberDefinition from)
        {
            var elementMethod = genericInstance.ElementMethod;
            bool isActivator = elementMethod.DeclaringType.FullName == "System.Activator";
            MethodDefinition resolved = null;
            if (!isActivator)
            {
                if (!genericInstance.GenericArguments.Any(a => ResolveTargetType(a) != null))
                {
                    return;
                }

                resolved = TryResolve(elementMethod);
                if (resolved == null)
                {
                    return;
                }
            }

            for (int i = 0; i < genericInstance.GenericArguments.Count; i++)
            {
                bool needsConstructor = isActivator ||
                    (i < resolved.GenericParameters.Count && resolved.GenericParameters[i].HasDefaultConstructorConstraint);
                if (!needsConstructor)
                {
                    continue;
                }

                var type = ResolveTargetType(genericInstance.GenericArguments[i]);
                if (type != null)
                {
                    Mark(GetDefaultConstructor(type), from, "new() constraint");
                }
            }
        }

        private void MarkFieldReference(FieldReference field, bool read, IMemberDefinition from, string text)
        {
            MarkTypeReference(field.DeclaringType, from, text);
            if (ResolveTargetType(field.DeclaringType) == null)
            {
                return;
            }

            var resolved = TryResolve(field);
            if (resolved == null)
            {
                Warn("Unable to resolve field " + field.FullName);
                return;
            }

            if (read)
            {
                Mark(resolved, from, text);
            }
            else
            {
                writtenFields.Add(resolved);
                Mark(resolved.DeclaringType, from, text);
            }
        }

        private void MarkCustomAttributes(ICustomAttributeProvider provider, IMemberDefinition from, TypeDefinition context)
        {
            if (provider == null || !provider.HasCustomAttributes)
            {
                return;
            }

            foreach (var attribute in provider.CustomAttributes)
            {
                MarkCustomAttribute(attribute, from, context);
            }
        }

        private void MarkCustomAttribute(CustomAttribute attribute, IMemberDefinition from, TypeDefinition context)
        {
            const string text = "custom attribute";
            MarkMethodReference(attribute.Constructor, from, text);

            try
            {
                foreach (var argument in attribute.ConstructorArguments)
                {
                    MarkAttributeArgument(argument, from, context);
                }

                var attributeType = ResolveTargetType(attribute.AttributeType);
                foreach (var namedArgument in attribute.Properties)
                {
                    MarkAttributeArgument(namedArgument.Argument, from, context);
                    if (attributeType != null)
                    {
                        var property = FindInHierarchy(attributeType, t => t.Properties.FirstOrDefault(p => p.Name == namedArgument.Name));
                        if (property != null)
                        {
                            Mark(property, from, text);
                            Mark(property.SetMethod, from, text);
                        }
                    }
                }

                foreach (var namedArgument in attribute.Fields)
                {
                    MarkAttributeArgument(namedArgument.Argument, from, context);
                    if (attributeType != null)
                    {
                        var field = FindInHierarchy(attributeType, t => t.Fields.FirstOrDefault(f => f.Name == namedArgument.Name));
                        Mark(field, from, text);
                    }
                }
            }
            catch
            {
            }
        }

        private void MarkAttributeArgument(CustomAttributeArgument argument, IMemberDefinition from, TypeDefinition context)
        {
            MarkTypeReference(argument.Type, from, "custom attribute argument");
            switch (argument.Value)
            {
                case TypeReference type:
                    MarkTypeReference(type, from, "typeof() in custom attribute");

                    // Attributes such as [TypeConverter(typeof(X))] or [Editor(typeof(X))] name a type that
                    // a framework creates through reflection.
                    var attributeType = ResolveTargetType(type);
                    if (attributeType != null && !attributeType.IsAbstract)
                    {
                        MarkInstanceConstructors(attributeType, from, "typeof() in custom attribute; created through reflection");
                    }

                    break;
                case CustomAttributeArgument nested:
                    MarkAttributeArgument(nested, from, context);
                    break;
                case CustomAttributeArgument[] array:
                    foreach (var item in array)
                    {
                        MarkAttributeArgument(item, from, context);
                    }

                    break;
                case string value when context != null:
                    foreach (var name in GetIdentifiers(value))
                    {
                        MarkMembersByName(context, name, from, "name in custom attribute string");
                    }

                    break;
            }
        }

        private static T FindInHierarchy<T>(TypeDefinition type, Func<TypeDefinition, T> find) where T : class
        {
            while (type != null)
            {
                var result = find(type);
                if (result != null)
                {
                    return result;
                }

                try
                {
                    type = type.BaseType?.Resolve();
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private void MarkMembersByName(TypeDefinition type, string name, IMemberDefinition from, string text)
        {
            for (var current = type; current != null && IsTarget(current); current = TryResolve(current.BaseType))
            {
                foreach (var property in current.Properties.Where(p => p.Name == name))
                {
                    MarkPropertyFully(property, from, text);
                }

                foreach (var field in current.Fields.Where(f => f.Name == name))
                {
                    Mark(field, from, text);
                }

                foreach (var method in current.Methods.Where(m => m.Name == name))
                {
                    Mark(method, from, text);
                }

                foreach (var @event in current.Events.Where(e => e.Name == name))
                {
                    MarkEventFully(@event, from, text);
                }
            }
        }

        private void MarkPropertyFully(PropertyDefinition property, IMemberDefinition from, string text)
        {
            Mark(property, from, text);
            Mark(property.GetMethod, from, text);
            Mark(property.SetMethod, from, text);
        }

        private void MarkEventFully(EventDefinition @event, IMemberDefinition from, string text)
        {
            Mark(@event, from, text);
            Mark(@event.AddMethod, from, text);
            Mark(@event.RemoveMethod, from, text);
        }

        private static readonly Regex identifierRegex = new Regex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

        private static IEnumerable<string> GetIdentifiers(string text)
        {
            foreach (Match match in identifierRegex.Matches(text))
            {
                yield return match.Value;
            }
        }

        private static MethodDefinition GetDefaultConstructor(TypeDefinition type)
        {
            return type.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
        }

        /// <summary>
        /// A type that is named in a string may be created through reflection, which calls a constructor
        /// that the IL does not show.
        /// </summary>
        private void MarkInstanceConstructors(TypeDefinition type, IMemberDefinition from, string text)
        {
            foreach (var constructor in type.Methods.Where(m => m.IsConstructor && !m.IsStatic))
            {
                Mark(constructor, from, text);
            }
        }

        private static bool InheritsFrom(TypeDefinition type, string baseTypeFullName)
        {
            return FindInHierarchy(type.BaseType == null ? null : TryResolveStatic(type.BaseType),
                t => t.FullName == baseTypeFullName ? t : null) != null;
        }

        private static bool Implements(TypeDefinition type, string interfaceFullName)
        {
            return FindInHierarchy(type, t => t.Interfaces.Any(i => i.InterfaceType.FullName == interfaceFullName) ? t : null) != null;
        }

        private static TypeDefinition TryResolveStatic(TypeReference type)
        {
            try
            {
                return type.Resolve();
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Roots

        public void AddInherentRoots(bool keepPublicApi)
        {
            foreach (var assembly in targets)
            {
                MarkCustomAttributes(assembly, null, null);
                foreach (var module in assembly.Modules)
                {
                    MarkCustomAttributes(module, null, null);
                    if (module.EntryPoint != null)
                    {
                        Mark(module.EntryPoint, null, "entry point");
                    }

                    var moduleType = module.Types.FirstOrDefault(t => t.Name == "<Module>");
                    if (moduleType != null)
                    {
                        Mark(moduleType, null, "module type");
                    }

                    foreach (var type in GetAllTypes(module))
                    {
                        if (type.BaseType?.FullName == "System.Windows.Markup.InternalTypeHelper")
                        {
                            Mark(type, null, "created by the XAML loader through reflection");
                            Mark(GetDefaultConstructor(type), null, "created by the XAML loader through reflection");
                        }

                        if (IsExported(type))
                        {
                            Mark(type, null, "exported by an Export attribute");
                            foreach (var constructor in type.Methods.Where(m => m.IsConstructor && !m.IsStatic))
                            {
                                if (constructor.Parameters.Count == 0 || HasAttribute(constructor, "ImportingConstructorAttribute"))
                                {
                                    Mark(constructor, null, "exported by an Export attribute");
                                }
                            }
                        }

                        if (keepPublicApi && IsVisibleOutside(type))
                        {
                            Mark(type, null, "public API");
                            foreach (var member in GetMembers(type))
                            {
                                if (IsVisibleOutside(member))
                                {
                                    Mark(member, null, "public API");
                                }
                            }
                        }
                    }

                    ScanBaml(module, isTarget: true);
                }
            }
        }

        public void AddRootsFromFile(string filePath)
        {
            var exact = new HashSet<string>(StringComparer.Ordinal);
            var prefixes = new List<string>();
            foreach (var rawLine in File.ReadAllLines(filePath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }

                if (line.EndsWith("*"))
                {
                    prefixes.Add(line.Substring(0, line.Length - 1));
                }
                else
                {
                    exact.Add(line);
                }
            }

            foreach (var member in GetAllTargetMembers())
            {
                var id = DocumentationId.Get(member);
                if (exact.Contains(id) || prefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal)))
                {
                    if (member is PropertyDefinition property)
                    {
                        MarkPropertyFully(property, null, "roots file");
                    }
                    else if (member is EventDefinition @event)
                    {
                        MarkEventFully(@event, null, "roots file");
                    }
                    else
                    {
                        Mark(member, null, "roots file");
                    }
                }
            }
        }

        public IEnumerable<IMemberDefinition> GetAllTargetMembers()
        {
            foreach (var module in targetModules)
            {
                foreach (var type in GetAllTypes(module))
                {
                    yield return type;
                    foreach (var member in GetMembers(type))
                    {
                        yield return member;
                    }
                }
            }
        }

        public static IEnumerable<IMemberDefinition> GetMembers(TypeDefinition type)
        {
            foreach (var method in type.Methods)
            {
                yield return method;
            }

            foreach (var field in type.Fields)
            {
                yield return field;
            }

            foreach (var property in type.Properties)
            {
                yield return property;
            }

            foreach (var @event in type.Events)
            {
                yield return @event;
            }
        }

        private static bool IsVisibleOutside(IMemberDefinition member)
        {
            switch (member)
            {
                case TypeDefinition type:
                    if (type.IsNested)
                    {
                        return (type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamilyOrAssembly) &&
                            IsVisibleOutside(type.DeclaringType);
                    }

                    return type.IsPublic;
                case MethodDefinition method:
                    return method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;
                case FieldDefinition field:
                    return field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;
                case PropertyDefinition property:
                    return (property.GetMethod != null && IsVisibleOutside(property.GetMethod)) ||
                        (property.SetMethod != null && IsVisibleOutside(property.SetMethod));
                case EventDefinition @event:
                    return @event.AddMethod != null && IsVisibleOutside(@event.AddMethod);
                default:
                    return false;
            }
        }

        private static bool HasAttribute(ICustomAttributeProvider provider, string name)
        {
            return provider.HasCustomAttributes && provider.CustomAttributes.Any(a => a.AttributeType.Name == name);
        }

        private bool IsExported(TypeDefinition type)
        {
            if (!type.HasCustomAttributes)
            {
                return false;
            }

            foreach (var attribute in type.CustomAttributes)
            {
                for (TypeReference current = attribute.AttributeType; current != null;)
                {
                    if (current.Name == "ExportAttribute" || current.Name == "InheritedExportAttribute")
                    {
                        return true;
                    }

                    var definition = TryResolve(current);
                    current = definition?.BaseType;
                }
            }

            return false;
        }

        #endregion

        #region Consumers

        public void AddConsumer(string filePath)
        {
            if (!GuiLabs.Metadata.PEFile.IsManagedAssembly(filePath))
            {
                return;
            }

            AssemblyDefinition assembly;
            try
            {
                assembly = AssemblyDefinition.ReadAssembly(filePath, new ReaderParameters
                {
                    AssemblyResolver = resolver,
                    InMemory = true,
                    ReadingMode = ReadingMode.Deferred
                });
            }
            catch
            {
                return;
            }

            if (targetNames.Contains(assembly.Name.Name))
            {
                return;
            }

            if (!visitedConsumers.Add(assembly.MainModule.Mvid))
            {
                return;
            }

            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)));

            foreach (var module in assembly.Modules)
            {
                bool references = module.AssemblyReferences.Any(r => targetNames.Contains(r.Name));
                if (references)
                {
                    Consumers.Add(filePath);
                    ScanConsumerModule(module);
                }

                if (ScanBaml(module, isTarget: false) && !references)
                {
                    BamlConsumers.Add(filePath);
                }
            }
        }

        private void ScanConsumerModule(ModuleDefinition module)
        {
            string text = ConsumerReferenceText + module.Assembly.Name.Name;

            foreach (var typeReference in module.GetTypeReferences())
            {
                MarkTypeReference(typeReference, null, text);
            }

            foreach (var memberReference in module.GetMemberReferences())
            {
                switch (memberReference)
                {
                    case MethodReference method:
                        MarkMethodReference(method, null, text);
                        break;
                    case FieldReference field:
                        MarkFieldReference(field, read: true, null, text);
                        break;
                }
            }

            MarkConsumerCustomAttributes(module.Assembly, text);
            MarkConsumerCustomAttributes(module, text);

            foreach (var type in GetAllTypes(module))
            {
                MarkConsumerCustomAttributes(type, text);
                foreach (var member in GetMembers(type))
                {
                    MarkConsumerCustomAttributes(member, text);
                    if (member is MethodDefinition method)
                    {
                        foreach (var parameter in method.Parameters)
                        {
                            MarkConsumerCustomAttributes(parameter, text);
                        }

                        MarkConsumerCustomAttributes(method.MethodReturnType, text);

                        if (method.HasBody)
                        {
                            ScanForReflection(method, null);
                        }
                    }
                }

                MarkTargetMembersImplementedByConsumer(type);
            }
        }

        private void MarkConsumerCustomAttributes(ICustomAttributeProvider provider, string text)
        {
            if (provider == null || !provider.HasCustomAttributes)
            {
                return;
            }

            foreach (var attribute in provider.CustomAttributes)
            {
                try
                {
                    foreach (var argument in attribute.ConstructorArguments)
                    {
                        MarkAttributeArgument(argument, null, null);
                    }

                    var attributeType = ResolveTargetType(attribute.AttributeType);
                    foreach (var argument in attribute.Properties)
                    {
                        MarkAttributeArgument(argument.Argument, null, null);
                        if (attributeType != null)
                        {
                            var property = FindInHierarchy(attributeType, t => t.Properties.FirstOrDefault(p => p.Name == argument.Name));
                            if (property != null)
                            {
                                Mark(property, null, text);
                                Mark(property.SetMethod, null, text);
                            }
                        }
                    }

                    foreach (var argument in attribute.Fields)
                    {
                        MarkAttributeArgument(argument.Argument, null, null);
                        if (attributeType != null)
                        {
                            Mark(FindInHierarchy(attributeType, t => t.Fields.FirstOrDefault(f => f.Name == argument.Name)), null, text);
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private void MarkTargetMembersImplementedByConsumer(TypeDefinition consumerType)
        {
            const string text = "overridden or implemented in a consumer";
            var baseType = TryResolve(consumerType.BaseType);
            var visitedBases = new HashSet<TypeDefinition>();
            bool derivesFromTarget = false;
            while (baseType != null && visitedBases.Add(baseType))
            {
                if (IsTarget(baseType))
                {
                    derivesFromTarget = true;
                    break;
                }

                baseType = TryResolve(baseType.BaseType);
            }

            if (derivesFromTarget)
            {
                foreach (var method in consumerType.Methods.Where(m => m.IsVirtual))
                {
                    var info = new VirtualInfo();
                    if (!method.IsNewSlot)
                    {
                        FindBaseMethod(consumerType, method, info);
                    }

                    foreach (var baseMethod in info.Bases)
                    {
                        Mark(baseMethod, null, text);
                    }
                }
            }

            foreach (var interfaceReference in GetAllInterfaces(consumerType))
            {
                var interfaceType = ResolveTargetType(interfaceReference);
                if (interfaceType == null)
                {
                    continue;
                }

                Mark(interfaceType, null, text);
                foreach (var member in GetMembers(interfaceType))
                {
                    Mark(member, null, text);
                }
            }
        }

        #endregion

        #region BAML

        private bool ScanBaml(ModuleDefinition module, bool isTarget)
        {
            bool found = false;
            foreach (var (name, data) in BamlScanner.GetBamlStreams(module))
            {
                IReadOnlyList<BamlUse> uses;
                BamlScanner scanner;
                try
                {
                    var document = BamlScanner.Read(data);
                    scanner = new BamlScanner(document, module.Assembly.Name.Name, name);
                    uses = scanner.Scan();
                }
                catch (Exception ex)
                {
                    Warn("Unable to read BAML " + name + " in " + module.Assembly.Name.Name + ": " + ex.Message);
                    continue;
                }

                if (!isTarget && !scanner.ReferencedAssemblies.Any(a => targetNames.Contains(a)))
                {
                    continue;
                }

                found = true;
                foreach (var use in uses)
                {
                    ApplyBamlUse(use);
                }
            }

            return found;
        }

        private TypeDefinition FindTargetType(BamlTypeName name)
        {
            if (name == null || !targetNames.Contains(name.Assembly))
            {
                return null;
            }

            targetTypesByFullName.TryGetValue(name.FullName, out var type);
            return type;
        }

        private IMemberDefinition FindCondition(BamlTypeName name)
        {
            if (name == null)
            {
                return null;
            }

            if (name.Member == null)
            {
                return FindTargetType(name);
            }

            var type = FindTargetType(name);
            if (type != null)
            {
                IMemberDefinition member = FindInHierarchy(type, t => (IMemberDefinition)t.Fields.FirstOrDefault(f => f.Name == name.Member) ??
                    t.Properties.FirstOrDefault(p => p.Name == name.Member));
                if (member != null && IsTarget(member))
                {
                    return member;
                }
            }

            return FindTargetType(new BamlTypeName { Assembly = name.Assembly, FullName = name.FullName + "." + name.Member });
        }

        private void ApplyBamlUse(BamlUse use)
        {
            var condition = FindCondition(use.Condition);
            string text = "XAML " + use.Document;

            switch (use.Kind)
            {
                case BamlUseKind.Type:
                case BamlUseKind.InstantiatedType:
                    var type = FindTargetType(use.Type);
                    if (type == null)
                    {
                        return;
                    }

                    WhenMarked(condition, () =>
                    {
                        Mark(type, condition, text);
                        if (use.Kind == BamlUseKind.InstantiatedType)
                        {
                            var constructor = GetDefaultConstructor(type);
                            if (constructor != null)
                            {
                                Mark(constructor, condition, text);
                            }
                            else
                            {
                                foreach (var instanceConstructor in type.Methods.Where(m => m.IsConstructor && !m.IsStatic))
                                {
                                    Mark(instanceConstructor, condition, text);
                                }
                            }
                        }
                    });
                    break;
                case BamlUseKind.Member:
                    var owner = FindTargetType(use.Type);
                    if (owner == null)
                    {
                        return;
                    }

                    WhenMarked(condition, () =>
                    {
                        Mark(owner, condition, text);
                        MarkXamlMember(owner, use.Name, condition, text);
                    });
                    break;
                case BamlUseKind.String:
                    WhenMarked(condition, () => MarkNamesFromString(use.Name, condition, text));
                    break;
            }
        }

        private void MarkXamlMember(TypeDefinition owner, string name, IMemberDefinition from, string text)
        {
            for (var current = owner; current != null && IsTarget(current); current = TryResolve(current.BaseType))
            {
                foreach (var property in current.Properties.Where(p => p.Name == name))
                {
                    MarkPropertyFully(property, from, text);
                }

                foreach (var @event in current.Events.Where(e => e.Name == name))
                {
                    MarkEventFully(@event, from, text);
                }

                foreach (var field in current.Fields.Where(f => f.IsStatic && (f.Name == name || f.Name == name + "Property" || f.Name == name + "Event")))
                {
                    Mark(field, from, text);
                }

                foreach (var method in current.Methods.Where(m => m.IsStatic &&
                    (m.Name == "Get" + name || m.Name == "Set" + name ||
                     m.Name == "Add" + name + "Handler" || m.Name == "Remove" + name + "Handler")))
                {
                    Mark(method, from, text);
                }
            }
        }

        private void MarkNamesFromString(string value, IMemberDefinition from, string text)
        {
            if (targetTypesByFullName.TryGetValue(value.Trim(), out var namedType))
            {
                Mark(namedType, from, text + " (type name string)");
                MarkInstanceConstructors(namedType, from, text + " (type name string)");
            }

            foreach (var identifier in GetIdentifiers(value))
            {
                MarkPropertiesByName(identifier, from, text + " (name in string \"" + Shorten(value) + "\")");
            }
        }

        private static string Shorten(string value)
        {
            value = value.Replace("\r", " ").Replace("\n", " ");
            return value.Length > 60 ? value.Substring(0, 60) + "..." : value;
        }

        private void MarkPropertiesByName(string name, IMemberDefinition from, string text)
        {
            if (targetMembersByName.TryGetValue(name, out var members))
            {
                foreach (var property in members.OfType<PropertyDefinition>())
                {
                    if (property.GetMethod == null || !property.GetMethod.IsPublic)
                    {
                        continue;
                    }

                    WhenMarked(property.DeclaringType, () => MarkPropertyFully(property, from, text));
                }
            }

            if (targetMembersByName.TryGetValue(name + "Property", out var fields))
            {
                foreach (var field in fields.OfType<FieldDefinition>().Where(f => f.IsStatic))
                {
                    WhenMarked(field.DeclaringType, () => Mark(field, from, text));
                }
            }
        }

        #endregion

        #region Reflection

        private static readonly HashSet<string> reflectionTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Type",
            "System.Reflection.TypeInfo",
            "System.Reflection.Assembly",
            "System.Reflection.Module",
            "System.Reflection.IntrospectionExtensions",
            "System.Reflection.RuntimeReflectionExtensions",
            "System.Reflection.TypeExtensions",
            "System.Activator",
            "System.Delegate",
            "System.ComponentModel.TypeDescriptor",
            "System.Windows.Data.Binding",
            "System.Windows.PropertyPath",
            "Microsoft.CSharp.RuntimeBinder.Binder",
            "System.Linq.Expressions.Expression",
        };

        private static readonly HashSet<string> reflectionMethodNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "GetMethod", "GetMethods", "GetProperty", "GetProperties", "GetField", "GetFields",
            "GetMember", "GetMembers", "GetEvent", "GetEvents", "GetConstructor", "GetConstructors",
            "GetNestedType", "GetNestedTypes", "GetInterface", "InvokeMember", "GetType", "GetTypes",
            "GetExportedTypes", "CreateInstance", "CreateDelegate", "GetDeclaredMethod", "GetDeclaredProperty",
            "GetDeclaredField", "GetDeclaredEvent", "GetDeclaredNestedType", "GetRuntimeMethod", "GetRuntimeProperty",
            "GetRuntimeField", "GetRuntimeEvent", "GetRuntimeMethods", "GetRuntimeProperties", "GetRuntimeFields",
            "GetRuntimeEvents", ".ctor", "InvokeMember", "GetMember", "SetMember", "Property", "PropertyOrField",
            "Field", "Call"
        };

        private static bool IsReflectionApi(MethodReference method)
        {
            var declaringType = method.DeclaringType.GetElementType().FullName;
            if (!reflectionTypes.Contains(declaringType))
            {
                return false;
            }

            if (method.Name == ".ctor")
            {
                return declaringType == "System.Windows.Data.Binding" || declaringType == "System.Windows.PropertyPath";
            }

            if (method.Name == "GetType" && method.Parameters.Count == 0)
            {
                return false;
            }

            return reflectionMethodNames.Contains(method.Name) || declaringType == "System.ComponentModel.TypeDescriptor";
        }

        private void ScanForReflection(MethodDefinition method, IMemberDefinition from)
        {
            List<string> strings = null;
            List<TypeDefinition> tokenTypes = null;
            var stringArguments = new List<string>();
            string api = null;
            bool callsActivator = false;

            foreach (var instruction in method.Body.Instructions)
            {
                if (EndsStatement(instruction))
                {
                    stringArguments.Clear();
                }

                switch (instruction.Operand)
                {
                    case string value:
                        strings ??= new List<string>();
                        strings.Add(value);
                        stringArguments.Add(value);
                        break;
                    case TypeReference type when instruction.OpCode.Code == Code.Ldtoken:
                        var tokenType = ResolveTargetType(type);
                        if (tokenType != null)
                        {
                            tokenTypes ??= new List<TypeDefinition>();
                            tokenTypes.Add(tokenType);
                        }

                        break;
                    case MethodReference called:
                        if (IsReflectionApi(called))
                        {
                            api ??= called.DeclaringType.Name + "." + called.Name;
                            if (called.DeclaringType.FullName == "System.Activator")
                            {
                                callsActivator = true;
                            }
                        }

                        if (stringArguments.Count > 0)
                        {
                            var methodArguments = (called as GenericInstanceMethod)?.GenericArguments ?? Enumerable.Empty<TypeReference>();
                            var typeArguments = (called.DeclaringType as GenericInstanceType)?.GenericArguments ?? Enumerable.Empty<TypeReference>();
                            var genericArgumentTypes = methodArguments.Concat(typeArguments)
                                .Select(ResolveTargetType)
                                .Where(t => t != null)
                                .ToArray();
                            if (genericArgumentTypes.Length > 0)
                            {
                                MarkMembersNamedNextToType(method, from, stringArguments.ToList(), genericArgumentTypes);
                            }
                        }

                        break;
                }
            }

            if (strings != null)
            {
                foreach (var value in strings)
                {
                    if (targetTypesByFullName.TryGetValue(value, out var namedType) ||
                        (value.Contains(',') && targetTypesByFullName.TryGetValue(value.Substring(0, value.IndexOf(',')).Trim(), out namedType)))
                    {
                        Mark(namedType, from, "type name string in " + DescribeMethod(method));
                        MarkInstanceConstructors(namedType, from, "type name string in " + DescribeMethod(method));
                    }
                }
            }

            if (api == null)
            {
                return;
            }

            bool touchesTarget = IsTarget(method) || tokenTypes != null ||
                (strings != null && strings.Any(s => targetMembersByName.ContainsKey(s)));
            if (!touchesTarget)
            {
                return;
            }

            ReflectionSites.Add(new ReflectionSite
            {
                Assembly = method.Module.Assembly.Name.Name,
                Method = DescribeMethod(method),
                Api = api,
                Strings = strings ?? new List<string>()
            });

            string text = "reflection in " + DescribeMethod(method);

            if (callsActivator && tokenTypes != null)
            {
                foreach (var type in tokenTypes)
                {
                    foreach (var constructor in type.Methods.Where(m => m.IsConstructor && !m.IsStatic))
                    {
                        Mark(constructor, from, text);
                    }
                }
            }

            if (strings == null)
            {
                return;
            }

            foreach (var value in strings)
            {
                foreach (var identifier in GetIdentifiers(value))
                {
                    if (!targetMembersByName.TryGetValue(identifier, out var members))
                    {
                        continue;
                    }

                    foreach (var member in members)
                    {
                        if (tokenTypes != null && !tokenTypes.Contains(member.DeclaringType))
                        {
                            continue;
                        }

                        var declaringType = member.DeclaringType;
                        WhenMarked(declaringType, () =>
                        {
                            switch (member)
                            {
                                case PropertyDefinition property:
                                    MarkPropertyFully(property, from, text);
                                    break;
                                case EventDefinition @event:
                                    MarkEventFully(@event, from, text);
                                    break;
                                default:
                                    Mark(member, from, text);
                                    break;
                            }
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Approximates the end of a source statement, where the evaluation stack is empty: the strings loaded
        /// before it are not arguments of the calls after it.
        /// </summary>
        private static bool EndsStatement(Instruction instruction)
        {
            var previous = instruction.Previous;
            if (previous == null)
            {
                return false;
            }

            switch (previous.OpCode.FlowControl)
            {
                case FlowControl.Branch:
                case FlowControl.Cond_Branch:
                case FlowControl.Return:
                case FlowControl.Throw:
                    return true;
                case FlowControl.Call when previous.Operand is MethodReference called:
                    return called.ReturnType.FullName == "System.Void" && !(called.Name == ".ctor" && previous.OpCode.Code == Code.Newobj);
            }

            switch (previous.OpCode.Code)
            {
                case Code.Pop:
                case Code.Stloc:
                case Code.Stloc_S:
                case Code.Stloc_0:
                case Code.Stloc_1:
                case Code.Stloc_2:
                case Code.Stloc_3:
                case Code.Stfld:
                case Code.Stsfld:
                case Code.Starg:
                case Code.Starg_S:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A call that takes a target type as a generic argument (of the method or of its declaring type)
        /// and a string that is the exact name of a member of that type most likely accesses the member
        /// by name: Reflector.SetValue&lt;Foo&gt;("Bar", value), WeakEventManager&lt;Foo, Args&gt;.AddHandler(source, "Changed", handler).
        /// </summary>
        private void MarkMembersNamedNextToType(MethodDefinition method, IMemberDefinition from, List<string> strings, TypeDefinition[] types)
        {
            string text = "member name string next to the type in " + DescribeMethod(method);
            bool found = false;
            foreach (var value in strings.Distinct())
            {
                foreach (var type in types.Distinct())
                {
                    for (var current = type; current != null && IsTarget(current); current = TryResolve(current.BaseType))
                    {
                        foreach (var member in GetMembers(current).Where(m => m.Name == value))
                        {
                            found = true;
                            switch (member)
                            {
                                case PropertyDefinition property:
                                    MarkPropertyFully(property, from, text);
                                    break;
                                case EventDefinition @event:
                                    MarkEventFully(@event, from, text);
                                    break;
                                default:
                                    Mark(member, from, text);
                                    break;
                            }
                        }
                    }
                }
            }

            if (found && !ReflectionSites.Any(s => s.Method == DescribeMethod(method)))
            {
                ReflectionSites.Add(new ReflectionSite
                {
                    Assembly = method.Module.Assembly.Name.Name,
                    Method = DescribeMethod(method),
                    Api = "a generic method with a target type argument and a member name string",
                    Strings = strings.Where(s => types.Any(t => GetMembers(t).Any(m => m.Name == s))).Distinct().ToList()
                });
            }
        }

        private static string DescribeMethod(MethodDefinition method)
        {
            return method.DeclaringType.FullName + "." + method.Name;
        }

        #endregion

        #region Interop

        public void ComputeInteropTypes()
        {
            var worklistTypes = new Queue<TypeDefinition>();
            void Add(TypeReference type)
            {
                while (type is TypeSpecification specification && type is not GenericInstanceType)
                {
                    type = specification.ElementType;
                }

                if (type is GenericInstanceType genericInstance)
                {
                    Add(genericInstance.ElementType);
                    foreach (var argument in genericInstance.GenericArguments)
                    {
                        Add(argument);
                    }

                    return;
                }

                var definition = ResolveTargetType(type);
                if (definition != null && interopTypes.Add(definition))
                {
                    worklistTypes.Enqueue(definition);
                }
            }

            void AddSignature(MethodDefinition method)
            {
                Add(method.ReturnType);
                foreach (var parameter in method.Parameters)
                {
                    Add(parameter.ParameterType);
                }
            }

            foreach (var module in targetModules)
            {
                foreach (var type in GetAllTypes(module))
                {
                    if (type.IsExplicitLayout || (type.IsClass && !type.IsValueType && type.IsSequentialLayout) ||
                        type.PackingSize > 0 || type.ClassSize > 0 || type.IsImport)
                    {
                        if (interopTypes.Add(type))
                        {
                            worklistTypes.Enqueue(type);
                        }
                    }

                    bool isComInterface = type.IsInterface && (type.IsImport || HasAttribute(type, "InterfaceTypeAttribute"));
                    bool isUnmanagedDelegate = HasAttribute(type, "UnmanagedFunctionPointerAttribute");

                    foreach (var field in type.Fields)
                    {
                        if (field.FieldType is PointerType || field.HasMarshalInfo)
                        {
                            Add(field.FieldType);
                        }
                    }

                    foreach (var method in type.Methods)
                    {
                        if (method.HasPInvokeInfo || isComInterface || (isUnmanagedDelegate && method.Name == "Invoke"))
                        {
                            AddSignature(method);
                        }

                        foreach (var parameter in method.Parameters)
                        {
                            if (parameter.ParameterType is PointerType ||
                                (parameter.ParameterType is ByReferenceType byReference && byReference.ElementType is PointerType))
                            {
                                Add(parameter.ParameterType);
                            }
                        }

                        if (!method.HasBody)
                        {
                            continue;
                        }

                        bool callsMarshal = method.Body.Instructions.Any(i =>
                            i.Operand is MethodReference called && called.DeclaringType.FullName == "System.Runtime.InteropServices.Marshal");
                        foreach (var instruction in method.Body.Instructions)
                        {
                            if (instruction.OpCode.Code == Code.Sizeof && instruction.Operand is TypeReference sizeofType)
                            {
                                Add(sizeofType);
                            }
                            else if (callsMarshal && instruction.OpCode.Code == Code.Ldtoken && instruction.Operand is TypeReference tokenType)
                            {
                                Add(tokenType);
                            }
                            else if (instruction.Operand is GenericInstanceMethod genericCall &&
                                genericCall.DeclaringType.FullName == "System.Runtime.InteropServices.Marshal")
                            {
                                foreach (var argument in genericCall.GenericArguments)
                                {
                                    Add(argument);
                                }
                            }
                        }

                        foreach (var variable in method.Body.Variables)
                        {
                            if (variable.VariableType is PointerType || variable.VariableType.IsPinned)
                            {
                                Add(variable.VariableType);
                            }
                        }
                    }
                }
            }

            while (worklistTypes.Count > 0)
            {
                var type = worklistTypes.Dequeue();
                foreach (var field in type.Fields.Where(f => !f.IsStatic))
                {
                    var fieldType = ResolveTargetType(field.FieldType);
                    if (fieldType != null && fieldType.IsValueType && interopTypes.Add(fieldType))
                    {
                        worklistTypes.Enqueue(fieldType);
                    }
                }
            }
        }

        #endregion

        #region Processing

        public void Run()
        {
            while (true)
            {
                Drain();
                if (!MarkVirtualMethods() && worklist.Count == 0)
                {
                    break;
                }
            }
        }

        private void Drain()
        {
            while (worklist.Count > 0)
            {
                var member = worklist.Dequeue();
                switch (member)
                {
                    case TypeDefinition type:
                        ProcessType(type);
                        break;
                    case MethodDefinition method:
                        ProcessMethod(method);
                        break;
                    case FieldDefinition field:
                        ProcessField(field);
                        break;
                    case PropertyDefinition property:
                        ProcessProperty(property);
                        break;
                    case EventDefinition @event:
                        ProcessEvent(@event);
                        break;
                }
            }
        }

        private void ProcessType(TypeDefinition type)
        {
            markedTypes.Add(type);
            const string text = "required by type";

            if (type.IsNested)
            {
                Mark(type.DeclaringType, type, "declaring type");
            }

            MarkTypeReference(type.BaseType, type, "base type");
            foreach (var interfaceImplementation in type.Interfaces)
            {
                MarkTypeReference(interfaceImplementation.InterfaceType, type, "implemented interface");
                MarkCustomAttributes(interfaceImplementation, type, type);
            }

            MarkGenericParameters(type, type);
            MarkCustomAttributes(type, type, type);

            var staticConstructor = type.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic);
            Mark(staticConstructor, type, "static constructor");

            if (type.IsEnum)
            {
                foreach (var field in type.Fields)
                {
                    Mark(field, type, "enum member");
                }
            }

            if (type.BaseType != null && type.BaseType.FullName == "System.MulticastDelegate")
            {
                foreach (var method in type.Methods)
                {
                    Mark(method, type, "delegate member");
                }
            }

            if (!type.IsAbstract && (InheritsFrom(type, "System.Runtime.InteropServices.SafeHandle") ||
                InheritsFrom(type, "System.Runtime.InteropServices.CriticalHandle")))
            {
                Mark(GetDefaultConstructor(type), type, "created by the interop marshaler for a returned handle");
            }

            if (!type.IsInterface && Implements(type, "System.Runtime.Serialization.ISerializable"))
            {
                var deserializationConstructor = type.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic &&
                    m.Parameters.Count == 2 &&
                    m.Parameters[0].ParameterType.FullName == "System.Runtime.Serialization.SerializationInfo" &&
                    m.Parameters[1].ParameterType.FullName == "System.Runtime.Serialization.StreamingContext");
                Mark(deserializationConstructor, type, "deserialization constructor of an ISerializable type");
            }

            if (interopTypes.Contains(type))
            {
                foreach (var field in type.Fields.Where(f => !f.IsStatic))
                {
                    Mark(field, type, "field of a type with interop layout");
                }
            }

            if (type.IsInterface && (type.IsImport || HasAttribute(type, "InterfaceTypeAttribute")))
            {
                foreach (var member in GetMembers(type))
                {
                    Mark(member, type, "member of a COM interface");
                }
            }

            if (type.IsSerializable)
            {
                foreach (var field in type.Fields.Where(f => !f.IsStatic && !f.IsNotSerialized))
                {
                    Mark(field, type, "field of a [Serializable] type");
                }
            }

            foreach (var member in GetMembers(type))
            {
                if (member is ICustomAttributeProvider provider && provider.HasCustomAttributes &&
                    provider.CustomAttributes.Any(a => IsSerializationOrImportAttribute(a.AttributeType.Name)))
                {
                    Mark(member, type, "member with a serialization or composition attribute");
                    if (member is PropertyDefinition property)
                    {
                        MarkPropertyFully(property, type, text);
                    }
                }
            }
        }

        private static readonly HashSet<string> serializationAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "DataMemberAttribute",
            "JsonPropertyAttribute",
            "JsonPropertyNameAttribute",
            "JsonIncludeAttribute",
            "JsonConstructorAttribute",
            "XmlElementAttribute",
            "XmlAttributeAttribute",
            "XmlArrayAttribute",
            "XmlArrayItemAttribute",
            "XmlTextAttribute",
            "OnSerializingAttribute",
            "OnSerializedAttribute",
            "OnDeserializingAttribute",
            "OnDeserializedAttribute",
            "ImportAttribute",
            "ImportManyAttribute",
            "ImportingConstructorAttribute",
            "ExportAttribute",
            "OnImportsSatisfiedAttribute",
        };

        private static bool IsSerializationOrImportAttribute(string name) => serializationAttributes.Contains(name);

        private void MarkGenericParameters(IGenericParameterProvider provider, IMemberDefinition from)
        {
            if (!provider.HasGenericParameters)
            {
                return;
            }

            foreach (var genericParameter in provider.GenericParameters)
            {
                foreach (var constraint in genericParameter.Constraints)
                {
                    MarkTypeReference(constraint.ConstraintType, from, "generic constraint");
                }

                MarkCustomAttributes(genericParameter, from, null);
            }
        }

        private static readonly Dictionary<string, string> operatorPairs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["op_Equality"] = "op_Inequality",
            ["op_Inequality"] = "op_Equality",
            ["op_LessThan"] = "op_GreaterThan",
            ["op_GreaterThan"] = "op_LessThan",
            ["op_LessThanOrEqual"] = "op_GreaterThanOrEqual",
            ["op_GreaterThanOrEqual"] = "op_LessThanOrEqual",
            ["op_True"] = "op_False",
            ["op_False"] = "op_True",
        };

        private void ProcessMethod(MethodDefinition method)
        {
            Mark(method.DeclaringType, method, "declaring type");

            MarkTypeReference(method.ReturnType, method, "signature");
            foreach (var parameter in method.Parameters)
            {
                MarkTypeReference(parameter.ParameterType, method, "signature");
                MarkCustomAttributes(parameter, method, method.DeclaringType);
            }

            MarkGenericParameters(method, method);
            MarkCustomAttributes(method, method, method.DeclaringType);
            MarkCustomAttributes(method.MethodReturnType, method, method.DeclaringType);

            foreach (var overridden in method.Overrides)
            {
                MarkMethodReference(overridden, method, "explicitly implemented member");
            }

            if (accessorOwners.TryGetValue(method, out var owner))
            {
                Mark(owner, method, "accessor");
                if (KeepAllAccessors && owner is PropertyDefinition ownerProperty)
                {
                    // The unused accessor is not reported, so it stays in the source; whatever it uses stays too.
                    // The accessor itself is not marked, so a write-only auto-property is still recognized.
                    ProcessKeptAccessor(ownerProperty.GetMethod);
                    ProcessKeptAccessor(ownerProperty.SetMethod);
                }
            }

            if (method.IsSpecialName && method.IsStatic && operatorPairs.TryGetValue(method.Name, out var pairName))
            {
                foreach (var pair in method.DeclaringType.Methods.Where(m => m.Name == pairName && m.IsSpecialName && SignaturesMatch(m, method)))
                {
                    Mark(pair, method, "C# requires operators to be declared in pairs");
                }
            }

            if (!method.HasBody)
            {
                return;
            }

            const string text = "used in body";
            var body = method.Body;
            foreach (var variable in body.Variables)
            {
                MarkTypeReference(variable.VariableType, method, text);
            }

            foreach (var handler in body.ExceptionHandlers)
            {
                MarkTypeReference(handler.CatchType, method, text);
            }

            foreach (var instruction in body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case MethodReference called:
                        MarkMethodReference(called, method, text);
                        break;
                    case FieldReference field:
                        var code = instruction.OpCode.Code;
                        bool isWrite = code == Code.Stfld || code == Code.Stsfld;
                        MarkFieldReference(field, read: !isWrite, method, text);
                        break;
                    case TypeReference type:
                        MarkTypeReference(type, method, text);
                        break;
                    case CallSite callSite:
                        MarkTypeReference(callSite.ReturnType, method, text);
                        foreach (var parameter in callSite.Parameters)
                        {
                            MarkTypeReference(parameter.ParameterType, method, text);
                        }

                        break;
                }
            }

            ScanForReflection(method, method);
        }

        private void ProcessField(FieldDefinition field)
        {
            Mark(field.DeclaringType, field, "declaring type");
            MarkTypeReference(field.FieldType, field, "field type");
            MarkCustomAttributes(field, field, field.DeclaringType);
        }

        private void ProcessProperty(PropertyDefinition property)
        {
            Mark(property.DeclaringType, property, "declaring type");
            MarkTypeReference(property.PropertyType, property, "property type");
            MarkCustomAttributes(property, property, property.DeclaringType);
        }

        private void ProcessEvent(EventDefinition @event)
        {
            Mark(@event.DeclaringType, @event, "declaring type");
            MarkTypeReference(@event.EventType, @event, "event type");
            MarkCustomAttributes(@event, @event, @event.DeclaringType);
        }

        #endregion

        #region Virtual methods

        private bool MarkVirtualMethods()
        {
            bool changed = false;
            foreach (var type in markedTypes.ToArray())
            {
                EnsureVirtualInfo(type);
                foreach (var method in type.Methods)
                {
                    if (!method.IsVirtual || marked.Contains(method))
                    {
                        continue;
                    }

                    if (!virtualInfos.TryGetValue(method, out var info))
                    {
                        continue;
                    }

                    if (info.External != null)
                    {
                        Mark(method, null, info.External);
                        changed = true;
                        continue;
                    }

                    var markedBase = info.Bases.FirstOrDefault(b => marked.Contains(b));
                    if (markedBase != null)
                    {
                        Mark(method, markedBase, "overrides or implements");
                        changed = true;
                    }
                }
            }

            return changed;
        }

        /// <summary>
        /// A type can have instances when it is a value type or an interface (implemented elsewhere), when it
        /// is imported from COM, or when one of its instance constructors is used. A derived constructor
        /// calls the base constructor, so a used derived class makes its base classes count as instantiated.
        /// </summary>
        public bool IsInstantiated(TypeDefinition type)
        {
            if (type.IsValueType || type.IsInterface || type.IsImport || interopTypes.Contains(type))
            {
                // Classes with an interop layout are created by the marshaler (for example Marshal.PtrToStructure).
                return true;
            }

            bool hasConstructor = false;
            foreach (var method in type.Methods)
            {
                if (method.IsConstructor && !method.IsStatic)
                {
                    hasConstructor = true;
                    if (marked.Contains(method))
                    {
                        return true;
                    }
                }
            }

            return !hasConstructor;
        }

        private VirtualInfo GetOrCreateVirtualInfo(MethodDefinition method)
        {
            if (!virtualInfos.TryGetValue(method, out var info))
            {
                info = new VirtualInfo();
                virtualInfos[method] = info;
            }

            return info;
        }

        private void EnsureVirtualInfo(TypeDefinition type)
        {
            if (!virtualInfoComputed.Add(type))
            {
                return;
            }

            foreach (var method in type.Methods.Where(m => m.IsVirtual))
            {
                var info = GetOrCreateVirtualInfo(method);
                foreach (var overridden in method.Overrides)
                {
                    var definition = TryResolve(overridden);
                    if (definition != null && IsTarget(definition))
                    {
                        info.Bases.Add(definition);
                    }
                    else
                    {
                        info.External ??= "implements external member " + overridden.DeclaringType.FullName + "." + overridden.Name;
                    }
                }

                if (!method.IsNewSlot)
                {
                    FindBaseMethod(type, method, info);
                }
            }

            foreach (var interfaceReference in GetAllInterfaces(type))
            {
                var interfaceType = TryResolve(interfaceReference);
                if (interfaceType == null)
                {
                    foreach (var method in type.Methods.Where(m => m.IsVirtual && m.IsNewSlot))
                    {
                        GetOrCreateVirtualInfo(method).External ??= "may implement unresolved interface " + interfaceReference.FullName;
                    }

                    continue;
                }

                bool isTargetInterface = IsTarget(interfaceType);
                var typeArguments = (interfaceReference as GenericInstanceType)?.GenericArguments;
                foreach (var interfaceMethod in interfaceType.Methods)
                {
                    var implementation = FindImplementation(type, interfaceMethod, typeArguments);
                    if (implementation == null)
                    {
                        continue;
                    }

                    var info = GetOrCreateVirtualInfo(implementation);
                    if (isTargetInterface)
                    {
                        info.Bases.Add(interfaceMethod);
                    }
                    else
                    {
                        info.External ??= "implements external interface " + interfaceType.FullName;
                    }
                }
            }
        }

        private IEnumerable<TypeReference> GetAllInterfaces(TypeDefinition type)
        {
            var result = new List<TypeReference>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<TypeReference>(type.Interfaces.Select(i => i.InterfaceType));
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!seen.Add(current.FullName))
                {
                    continue;
                }

                result.Add(current);
                var definition = TryResolve(current);
                if (definition == null)
                {
                    continue;
                }

                foreach (var inherited in definition.Interfaces)
                {
                    queue.Enqueue(inherited.InterfaceType);
                }
            }

            return result;
        }

        private MethodDefinition FindImplementation(TypeDefinition type, MethodDefinition interfaceMethod, IList<TypeReference> typeArguments)
        {
            foreach (var method in type.Methods)
            {
                foreach (var overridden in method.Overrides)
                {
                    if (overridden.Name == interfaceMethod.Name && TryResolve(overridden) == interfaceMethod)
                    {
                        return method;
                    }
                }
            }

            for (var current = type; current != null; current = TryResolve(current.BaseType))
            {
                var match = current.Methods.FirstOrDefault(m => m.IsVirtual && !m.IsStatic &&
                    m.Name == interfaceMethod.Name && SignaturesMatch(m, interfaceMethod, typeArguments));
                if (match != null)
                {
                    return IsTarget(match) ? match : null;
                }
            }

            return null;
        }

        private void FindBaseMethod(TypeDefinition type, MethodDefinition method, VirtualInfo info)
        {
            var baseReference = type.BaseType;
            var visited = new HashSet<TypeDefinition>();
            while (baseReference != null)
            {
                var baseType = TryResolve(baseReference);
                if (baseType == null || !visited.Add(baseType))
                {
                    info.External ??= "overrides a member of unresolved type " + baseReference.FullName;
                    return;
                }

                var typeArguments = (baseReference as GenericInstanceType)?.GenericArguments;
                var match = baseType.Methods.FirstOrDefault(m => m.IsVirtual && m.Name == method.Name && SignaturesMatch(method, m, typeArguments));
                if (match != null)
                {
                    if (IsTarget(match))
                    {
                        info.Bases.Add(match);
                    }
                    else
                    {
                        info.External ??= "overrides external " + baseType.FullName + "." + match.Name;
                    }

                    return;
                }

                baseReference = baseType.BaseType;
            }

            info.External ??= "overrides a member not found in base types";
        }

        /// <summary>
        /// Compares the signature of <paramref name="derived"/> with <paramref name="baseMethod"/>.
        /// <paramref name="typeArguments"/> are the generic arguments of the base type or interface
        /// instance, substituted for the type parameters in the signature of <paramref name="baseMethod"/>.
        /// </summary>
        private static bool SignaturesMatch(MethodDefinition derived, MethodDefinition baseMethod, IList<TypeReference> typeArguments = null)
        {
            if (derived.Parameters.Count != baseMethod.Parameters.Count)
            {
                return false;
            }

            if (derived.GenericParameters.Count != baseMethod.GenericParameters.Count)
            {
                return false;
            }

            if (!TypesMatch(derived.ReturnType, baseMethod.ReturnType, typeArguments))
            {
                return false;
            }

            for (int i = 0; i < derived.Parameters.Count; i++)
            {
                if (!TypesMatch(derived.Parameters[i].ParameterType, baseMethod.Parameters[i].ParameterType, typeArguments))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TypesMatch(TypeReference a, TypeReference b, IList<TypeReference> typeArguments)
        {
            if (b is GenericParameter typeParameter &&
                typeParameter.Type == GenericParameterType.Type &&
                typeArguments != null &&
                typeParameter.Position < typeArguments.Count)
            {
                return TypesMatch(a, typeArguments[typeParameter.Position], typeArguments: null);
            }

            if (a is GenericParameter || b is GenericParameter)
            {
                return a is GenericParameter && b is GenericParameter
                    ? ((GenericParameter)a).Position == ((GenericParameter)b).Position
                    : typeArguments == null;
            }

            if (a is GenericInstanceType ga && b is GenericInstanceType gb)
            {
                if (!TypesMatch(ga.ElementType, gb.ElementType, typeArguments) || ga.GenericArguments.Count != gb.GenericArguments.Count)
                {
                    return false;
                }

                for (int i = 0; i < ga.GenericArguments.Count; i++)
                {
                    if (!TypesMatch(ga.GenericArguments[i], gb.GenericArguments[i], typeArguments))
                    {
                        return false;
                    }
                }

                return true;
            }

            if (a is IModifierType ma)
            {
                return TypesMatch(ma.ElementType, b is IModifierType mb ? mb.ElementType : b, typeArguments);
            }

            if (b is IModifierType mb2)
            {
                return TypesMatch(a, mb2.ElementType, typeArguments);
            }

            if (a is TypeSpecification sa && b is TypeSpecification sb)
            {
                return a.GetType() == b.GetType() && TypesMatch(sa.ElementType, sb.ElementType, typeArguments);
            }

            return a.FullName == b.FullName;
        }

        #endregion
    }
}
