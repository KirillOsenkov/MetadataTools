using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Collections.Generic;

namespace BinaryCompatChecker;

public partial class Checker
{
    private void CheckTypeDefinitions(string assemblyFullName, ModuleDefinition module, List<MemberReference> references)
    {
        // Do not search the Xamarin.Mac assembly for NSObject/etc subclasses
        var moduleIsXamarinMac = module.Name == "Xamarin.Mac.dll" || module.Name == "Microsoft.macOS.dll";

        var types = module.GetTypes();
        foreach (var typeDef in types)
        {
            CheckTypeAttributes(assemblyFullName, typeDef);

            if (!typeDef.IsClass)
            {
                continue;
            }

            var foundINativeObjectImplementation = false;
            const string iNativeObjectInterfaceFullName = "ObjCRuntime.INativeObject";
            void CheckNativeObjectConstructors()
            {
                // Looks for constructors that use IntPtr instead of NativeHandle. This will crash at runtime.
                // See https://github.com/xamarin/xamarin-macios/blob/14d5620f5f8b6e5b7541695a22ef7376807c400e/dotnet/BreakingChanges.md#nsobjecthandle-and-inativeobjecthandle-changed-type-from-systemintptr-to-objcruntimenativehandle
                if (typeDef.Methods.Any(m => m.IsConstructor && m.Parameters.Any(p => p.ParameterType.Name == "IntPtr")))
                {
                    // TODO: Check that the ctor calls base? Is this possible?
                    diagnostics.Add($"In assembly '{assemblyFullName}': Type {typeDef.FullName} has a potentially dangerous IntPtr constructor");
                }
            }

            if (typeDef.HasInterfaces)
            {
                foreach (var interfaceImplementation in typeDef.Interfaces)
                {
                    var interfaceTypeRef = interfaceImplementation.InterfaceType;
                    references.Add(interfaceTypeRef);

                    try
                    {
                        if (commandLine.ReportIntPtrConstructors
                            && !foundINativeObjectImplementation
                            && !moduleIsXamarinMac
                            && interfaceTypeRef.FullName == iNativeObjectInterfaceFullName)
                        {
                            foundINativeObjectImplementation = true;
                            CheckNativeObjectConstructors();
                        }

                        var interfaceTypeDef = interfaceTypeRef.Resolve();
                        if (interfaceTypeDef != null)
                        {
                            if (interfaceTypeDef.HasMethods)
                            {
                                foreach (var interfaceMethod in interfaceTypeDef.Methods)
                                {
                                    if (interfaceMethod.HasGenericParameters || interfaceMethod.ContainsGenericParameter)
                                    {
                                        // it's non-trivial to match when generics are involved
                                        continue;
                                    }

                                    if (interfaceMethod.HasBody)
                                    {
                                        // Default method implementation provided by the interface itself
                                        continue;
                                    }

                                    bool sawGenerics = false;
                                    var matching = FindInterfaceMethodImplementation(typeDef, interfaceMethod, ref sawGenerics);
                                    if (matching == null && !sawGenerics && commandLine.ReportInterfaceMismatch)
                                    {
                                        var interfaceAssembly = GetAssemblyName(interfaceMethod);
                                        diagnostics.Add($"In assembly '{assemblyFullName}': Type {typeDef.FullName} does not implement interface method {interfaceMethod.FullName} from assembly {interfaceAssembly}");
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            // For some reason, typeDef.Interfaces does not always show an INativeObject implementation even
            // when it is there (via NSObject, typically). Resolve all base types to see if any of them
            // implement INativeObject.
            if (commandLine.ReportIntPtrConstructors && !foundINativeObjectImplementation && !moduleIsXamarinMac)
            {
                var candidateNativeTypeDef = typeDef;
                while (candidateNativeTypeDef != null)
                {
                    if (candidateNativeTypeDef.HasInterfaces && candidateNativeTypeDef.Interfaces.Any(i => i.InterfaceType.FullName == "ObjCRuntime.INativeObject"))
                    {
                        break;
                    }
                    candidateNativeTypeDef = ResolveBaseType(candidateNativeTypeDef);
                }

                if (candidateNativeTypeDef != null)
                {
                    CheckNativeObjectConstructors();
                }
            }

            if (typeDef.HasMethods)
            {
                foreach (var methodDef in typeDef.Methods)
                {
                    try
                    {
                        if (methodDef.HasOverrides)
                        {
                            foreach (var methodOverride in methodDef.Overrides)
                            {
                                references.Add(methodOverride);
                            }
                        }

                        var baseMethod = GetBaseMethod(methodDef);
                        if (baseMethod != null)
                        {
                            var same = MetadataResolver.GetMethod(baseMethod.DeclaringType.Methods, methodDef);
                            if (same == null)
                            {
                                diagnostics.Add($"In assembly '{assemblyFullName}': Failed to find base method for '{methodDef.FullName}'");
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    private void CheckTypeAttributes(string assemblyFullName, TypeDefinition typeDef)
    {
        if (!typeDef.HasCustomAttributes)
        {
            return;
        }

        var attributes = typeDef.CustomAttributes;
        bool hasCompilerGeneratedAttribute = false;
        bool hasTypeIdentifierAttribute = false;
        foreach (var attribute in attributes)
        {
            string fullName = attribute.AttributeType.FullName;
            if (fullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute")
            {
                hasCompilerGeneratedAttribute = true;
            }
            else if (fullName == "System.Runtime.InteropServices.TypeIdentifierAttribute")
            {
                hasTypeIdentifierAttribute = true;
            }
        }

        if (commandLine.ReportEmbeddedInteropTypes && hasCompilerGeneratedAttribute && hasTypeIdentifierAttribute)
        {
            diagnostics.Add($"In assembly '{assemblyFullName}': Embedded interop type {typeDef.FullName}");
        }
    }

    private void CheckTypes(AssemblyDefinition referencing, AssemblyDefinition reference)
    {
        var typeReferences = referencing.MainModule.GetTypeReferences();
        var types = GetTypes(reference);

        foreach (var referencedType in typeReferences)
        {
            if (referencedType.Scope == null ||
                referencedType.Scope.MetadataScopeType != MetadataScopeType.AssemblyNameReference ||
                !string.Equals(referencedType.Scope.Name, reference.Name.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (types.TryGetValue(referencedType.FullName, out bool isPublic))
            {
                if (!isPublic)
                {
                    var ivtUsage = new IVTUsage
                    {
                        ConsumingAssembly = referencing.MainModule.FileName,
                        ExposingAssembly = reference.MainModule.FileName,
                        Member = referencedType.FullName
                    };
                    AddIVTUsage(ivtUsage);
                }
            }
            else
            {
                if (commandLine.ReportMissingTypes)
                {
                    diagnostics.Add($"In assembly '{referencing.Name.FullName}': Failed to resolve type reference '{referencedType.FullName}' in assembly '{reference.Name}'");
                }
            }
        }
    }

    private void AddIVTUsage(IVTUsage ivtUsage)
    {
        ivtUsages.Add(ivtUsage);
    }

    private Dictionary<string, bool> GetTypes(AssemblyDefinition assembly)
    {
        if (assemblyToTypeList.TryGetValue(assembly, out var types))
        {
            return types;
        }

        types = new Dictionary<string, bool>();
        assemblyToTypeList[assembly] = types;

        foreach (var topLevelType in assembly.MainModule.Types)
        {
            types.Add(topLevelType.FullName, topLevelType.IsPublic);
            AddNestedTypes(topLevelType, types);
        }

        foreach (var exportedType in assembly.MainModule.ExportedTypes)
        {
            types.Add(exportedType.FullName, exportedType.IsPublic);
        }

        return types;
    }

    private void AddNestedTypes(TypeDefinition type, Dictionary<string, bool> types)
    {
        foreach (var nested in type.NestedTypes)
        {
            types.Add(nested.FullName, nested.IsNestedPublic);
            AddNestedTypes(nested, types);
        }
    }
}
