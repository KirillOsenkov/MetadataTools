using System.Linq;
using Mono.Cecil;

namespace FindUnusedMembers
{
    public static class DocumentationId
    {
        public static string Get(IMemberDefinition member)
        {
            switch (member)
            {
                case TypeDefinition type:
                    return "T:" + GetTypeName(type);
                case MethodDefinition method:
                    return "M:" + GetMethodName(method);
                case FieldDefinition field:
                    return "F:" + GetTypeName(field.DeclaringType) + "." + Escape(field.Name);
                case PropertyDefinition property:
                    return "P:" + GetTypeName(property.DeclaringType) + "." + Escape(property.Name) + GetParameters(property.Parameters);
                case EventDefinition @event:
                    return "E:" + GetTypeName(@event.DeclaringType) + "." + Escape(@event.Name);
                default:
                    return member?.ToString();
            }
        }

        public static string GetTypeName(TypeDefinition type)
        {
            if (type.IsNested)
            {
                return GetTypeName(type.DeclaringType) + "." + type.Name;
            }

            if (string.IsNullOrEmpty(type.Namespace))
            {
                return type.Name;
            }

            return type.Namespace + "." + type.Name;
        }

        private static string GetMethodName(MethodDefinition method)
        {
            string result = GetTypeName(method.DeclaringType) + "." + Escape(method.Name);
            if (method.HasGenericParameters)
            {
                result += "``" + method.GenericParameters.Count;
            }

            result += GetParameters(method.Parameters);

            if (method.Name == "op_Implicit" || method.Name == "op_Explicit")
            {
                result += "~" + GetSignatureTypeName(method.ReturnType);
            }

            return result;
        }

        private static string GetParameters(Mono.Collections.Generic.Collection<ParameterDefinition> parameters)
        {
            if (parameters.Count == 0)
            {
                return "";
            }

            return "(" + string.Join(",", parameters.Select(p => GetSignatureTypeName(p.ParameterType))) + ")";
        }

        private static string Escape(string name)
        {
            return name.Replace('.', '#').Replace('<', '{').Replace('>', '}').Replace(',', '@');
        }

        private static string GetSignatureTypeName(TypeReference type)
        {
            switch (type)
            {
                case GenericParameter genericParameter:
                    return (genericParameter.Type == GenericParameterType.Method ? "``" : "`") + genericParameter.Position;
                case ByReferenceType byReference:
                    return GetSignatureTypeName(byReference.ElementType) + "@";
                case PointerType pointer:
                    return GetSignatureTypeName(pointer.ElementType) + "*";
                case ArrayType array:
                    if (array.IsVector)
                    {
                        return GetSignatureTypeName(array.ElementType) + "[]";
                    }

                    return GetSignatureTypeName(array.ElementType) + "[" + string.Join(",", Enumerable.Repeat("0:", array.Rank)) + "]";
                case IModifierType modifier:
                    return GetSignatureTypeName(modifier.ElementType);
                case PinnedType pinned:
                    return GetSignatureTypeName(pinned.ElementType);
                case GenericInstanceType genericInstance:
                    return GetQualifiedName(genericInstance.ElementType, stripArity: true) +
                        "{" + string.Join(",", genericInstance.GenericArguments.Select(GetSignatureTypeName)) + "}";
                case FunctionPointerType:
                    return "=FUNC";
                default:
                    return GetQualifiedName(type, stripArity: false);
            }
        }

        private static string GetQualifiedName(TypeReference type, bool stripArity)
        {
            string name = type.Name;
            if (stripArity)
            {
                int tick = name.IndexOf('`');
                if (tick >= 0)
                {
                    name = name.Substring(0, tick);
                }
            }

            if (type.DeclaringType != null)
            {
                return GetQualifiedName(type.DeclaringType, stripArity) + "." + name;
            }

            if (string.IsNullOrEmpty(type.Namespace))
            {
                return name;
            }

            return type.Namespace + "." + name;
        }
    }
}
