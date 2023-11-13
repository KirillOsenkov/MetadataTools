using System;
using Mono.Cecil;
using Mono.Collections.Generic;

namespace BinaryCompatChecker;

public partial class Checker
{
    public static string GetAssemblyName(MemberReference memberReference)
    {
        var declaringType = memberReference.DeclaringType ?? (memberReference as TypeReference);
        if (declaringType == null)
        {
            return null;
        }

        IMetadataScope scope = declaringType.Scope;
        string referenceToAssembly = scope?.Name;

        if (scope is AssemblyNameReference assemblyNameReference)
        {
            referenceToAssembly = assemblyNameReference.FullName;
        }

        return referenceToAssembly;
    }

    public static MethodDefinition FindInterfaceMethodImplementation(TypeDefinition typeDefinition, MethodDefinition interfaceMethod, ref bool sawGenerics)
    {
        if (typeDefinition.HasGenericParameters)
        {
            sawGenerics = true;
            return null;
        }

        var matching = GetMethod(typeDefinition.Methods, interfaceMethod);
        if (matching != null)
        {
            return matching;
        }

        var baseType = ResolveBaseType(typeDefinition);
        if (baseType != null)
        {
            return FindInterfaceMethodImplementation(baseType, interfaceMethod, ref sawGenerics);
        }

        return null;
    }

    public static MethodDefinition GetBaseMethod(MethodDefinition methodDefinition)
    {
        if (methodDefinition == null)
        {
            throw new ArgumentNullException("methodDefinition");
        }

        if (!methodDefinition.IsVirtual)
        {
            return null;
        }

        if (methodDefinition.IsNewSlot)
        {
            return null;
        }

        try
        {
            var baseType = ResolveBaseType(methodDefinition.DeclaringType);
            while (baseType != null)
            {
                var baseMethod = MetadataResolver.GetMethod(baseType.Methods, methodDefinition);
                if (baseMethod != null)
                {
                    return baseMethod;
                }

                baseType = ResolveBaseType(baseType);
            }
        }
        catch
        {
        }

        return null;
    }

    public static TypeDefinition ResolveBaseType(TypeDefinition type)
    {
        if (type == null)
        {
            return null;
        }

        var baseType = type.BaseType;
        if (baseType == null)
        {
            return null;
        }

        return baseType.Resolve();
    }

    private bool AllPublic(MemberReference memberReference)
    {
        return memberReference switch
        {
            TypeDefinition typeDefinition => AllPublic(typeDefinition),
            MethodDefinition methodDefinition => AllPublic(methodDefinition),
            FieldDefinition fieldDefinition => AllPublic(fieldDefinition),
            _ => true
        };
    }

    private static bool AllPublic(FieldDefinition field)
    {
        if (field.IsAssembly || field.IsFamilyAndAssembly)
        {
            return false;
        }

        var type = field.DeclaringType;
        return AllPublic(type);
    }

    private static bool AllPublic(MethodDefinition method)
    {
        if (method.IsAssembly || method.IsFamilyAndAssembly)
        {
            return false;
        }

        var type = method.DeclaringType;
        return AllPublic(type);
    }

    private static bool AllPublic(TypeDefinition type)
    {
        while (type != null)
        {
            if (!type.IsPublic && !type.IsNestedPublic)
            {
                return false;
            }

            type = type.DeclaringType;
        }

        return true;
    }

    public static MethodDefinition GetMethod(Collection<MethodDefinition> methods, MethodReference reference)
    {
        for (int i = 0; i < methods.Count; i++)
        {
            var method = methods[i];

            if (method.HasOverrides)
            {
                foreach (var overrideMethod in method.Overrides)
                {
                    if (AreSame(overrideMethod, reference))
                    {
                        return method;
                    }
                }
            }

            string methodName = method.Name;
            int dot = methodName.LastIndexOf('.');
            if (dot > 0)
            {
                methodName = methodName.Substring(dot + 1);
            }

            if (methodName != reference.Name)
                continue;

            if (method.HasGenericParameters != reference.HasGenericParameters)
                continue;

            if (method.HasGenericParameters && method.GenericParameters.Count != reference.GenericParameters.Count)
                continue;

            if (!AreSame(method.ReturnType, reference.ReturnType))
                continue;

            if (IsVarArg(method) != IsVarArg(reference))
                continue;

            if (IsVarArg(method) && IsVarArgCallTo(method, reference))
                return method;

            if (method.HasParameters != reference.HasParameters)
                continue;

            if (!method.HasParameters && !reference.HasParameters)
                return method;

            if (!AreSame(method.Parameters, reference.Parameters))
                continue;

            return method;
        }

        return null;
    }

    public static bool AreSame(MethodReference method, MethodReference reference)
    {
        if (method.Name != reference.Name)
            return false;

        if (method.HasGenericParameters != reference.HasGenericParameters)
            return false;

        if (method.HasGenericParameters && method.GenericParameters.Count != reference.GenericParameters.Count)
            return false;

        if (!AreSame(method.ReturnType, reference.ReturnType))
            return false;

        if (IsVarArg(method) != IsVarArg(reference))
            return false;

        if (IsVarArg(method) && IsVarArgCallTo(method, reference))
            return true;

        if (method.HasParameters != reference.HasParameters)
            return false;

        if (!method.HasParameters && !reference.HasParameters)
            return true;

        if (!AreSame(method.Parameters, reference.Parameters))
            return false;

        return true;
    }

    public static bool IsVarArg(IMethodSignature self)
    {
        return self.CallingConvention == MethodCallingConvention.VarArg;
    }

    static bool AreSame(Collection<ParameterDefinition> a, Collection<ParameterDefinition> b)
    {
        var count = a.Count;

        if (count != b.Count)
        {
            return false;
        }

        if (count == 0)
        {
            return true;
        }

        for (int i = 0; i < count; i++)
        {
            if (!AreSame(a[i].ParameterType, b[i].ParameterType))
            {
                return false;
            }
        }

        return true;
    }

    static bool IsVarArgCallTo(MethodReference method, MethodReference reference)
    {
        var methodParameters = method.Parameters;
        var referenceParameters = reference.Parameters;

        if (methodParameters.Count >= referenceParameters.Count)
        {
            return false;
        }

        if (GetSentinelPosition(reference) != methodParameters.Count)
        {
            return false;
        }

        for (int i = 0; i < methodParameters.Count; i++)
        {
            if (!AreSame(methodParameters[i].ParameterType, referenceParameters[i].ParameterType))
            {
                return false;
            }
        }

        return true;
    }

    public static int GetSentinelPosition(IMethodSignature self)
    {
        if (!self.HasParameters)
        {
            return -1;
        }

        var parameters = self.Parameters;
        for (int i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].ParameterType.IsSentinel)
            {
                return i;
            }
        }

        return -1;
    }

    static bool AreSame(TypeSpecification a, TypeSpecification b)
    {
        if (!AreSame(a.ElementType, b.ElementType))
        {
            return false;
        }

        if (a.IsGenericInstance)
        {
            return AreSame((GenericInstanceType)a, (GenericInstanceType)b);
        }

        if (a.IsRequiredModifier || a.IsOptionalModifier)
        {
            return AreSame((IModifierType)a, (IModifierType)b);
        }

        if (a.IsArray)
        {
            return AreSame((ArrayType)a, (ArrayType)b);
        }

        return true;
    }

    static bool AreSame(ArrayType a, ArrayType b)
    {
        if (a.Rank != b.Rank)
        {
            return false;
        }

        // TODO: dimensions

        return true;
    }

    static bool AreSame(IModifierType a, IModifierType b)
    {
        return AreSame(a.ModifierType, b.ModifierType);
    }

    static bool AreSame(GenericInstanceType a, GenericInstanceType b)
    {
        if (a.GenericArguments.Count != b.GenericArguments.Count)
        {
            return false;
        }

        for (int i = 0; i < a.GenericArguments.Count; i++)
        {
            if (!AreSame(a.GenericArguments[i], b.GenericArguments[i]))
            {
                return false;
            }
        }

        return true;
    }

    static bool AreSame(GenericParameter a, GenericParameter b)
    {
        return a.Position == b.Position;
    }

    static bool AreSame(TypeReference a, TypeReference b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a == null || b == null)
        {
            return false;
        }

        if (a.MetadataType != b.MetadataType)
        {
            return false;
        }

        if (a.IsGenericParameter)
        {
            return AreSame((GenericParameter)a, (GenericParameter)b);
        }

        if (IsTypeSpecification(a))
        {
            return AreSame((TypeSpecification)a, (TypeSpecification)b);
        }

        if (a.Name != b.Name || a.Namespace != b.Namespace)
        {
            return false;
        }

        //TODO: check scope

        return AreSame(a.DeclaringType, b.DeclaringType);
    }

    public static bool IsTypeSpecification(TypeReference type)
    {
        switch ((ElementType)type.MetadataType)
        {
            case ElementType.Array:
            case ElementType.ByRef:
            case ElementType.CModOpt:
            case ElementType.CModReqD:
            case ElementType.FnPtr:
            case ElementType.GenericInst:
            case ElementType.MVar:
            case ElementType.Pinned:
            case ElementType.Ptr:
            case ElementType.SzArray:
            case ElementType.Sentinel:
            case ElementType.Var:
                return true;
        }

        return false;
    }

    private IVTUsage TryGetIVTUsage(MemberReference memberReference, IMemberDefinition definition)
    {
        string consumingModule = memberReference.Module.FileName;

        if (definition is MemberReference memberDefinition)
        {
            string definitionModule = memberDefinition.Module.FileName;

            if (consumingModule == definitionModule)
            {
                return null;
            }

            if (AllPublic(memberDefinition))
            {
                return null;
            }

            return new IVTUsage
            {
                ExposingAssembly = definitionModule,
                ConsumingAssembly = consumingModule,
                Member = definition.ToString()
            };
        }

        return null;
    }

    enum ElementType : byte
    {
        None = 0x00,
        Void = 0x01,
        Boolean = 0x02,
        Char = 0x03,
        I1 = 0x04,
        U1 = 0x05,
        I2 = 0x06,
        U2 = 0x07,
        I4 = 0x08,
        U4 = 0x09,
        I8 = 0x0a,
        U8 = 0x0b,
        R4 = 0x0c,
        R8 = 0x0d,
        String = 0x0e,
        Ptr = 0x0f,   // Followed by <type> token
        ByRef = 0x10,   // Followed by <type> token
        ValueType = 0x11,   // Followed by <type> token
        Class = 0x12,   // Followed by <type> token
        Var = 0x13,   // Followed by generic parameter number
        Array = 0x14,   // <type> <rank> <boundsCount> <bound1>  <loCount> <lo1>
        GenericInst = 0x15,   // <type> <type-arg-count> <type-1> ... <type-n> */
        TypedByRef = 0x16,
        I = 0x18,   // System.IntPtr
        U = 0x19,   // System.UIntPtr
        FnPtr = 0x1b,   // Followed by full method signature
        Object = 0x1c,   // System.Object
        SzArray = 0x1d,   // Single-dim array with 0 lower bound
        MVar = 0x1e,   // Followed by generic parameter number
        CModReqD = 0x1f,   // Required modifier : followed by a TypeDef or TypeRef token
        CModOpt = 0x20,   // Optional modifier : followed by a TypeDef or TypeRef token
        Internal = 0x21,   // Implemented within the CLI
        Modifier = 0x40,   // Or'd with following element types
        Sentinel = 0x41,   // Sentinel for varargs method signature
        Pinned = 0x45,   // Denotes a local variable that points at a pinned object

        // special undocumented constants
        Type = 0x50,
        Boxed = 0x51,
        Enum = 0x55
    }
}