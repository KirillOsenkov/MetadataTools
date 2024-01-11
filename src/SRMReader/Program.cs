using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SRMReader;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length != 1)
        {
            return;
        }

        string filePath = args[0];
        ReadModuleInfo(filePath);
    }

    private static void ReadModuleInfo(string filePath)
    {
        using FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        using PEReader peReader = new PEReader(fileStream);

        if (!peReader.HasMetadata)
        {
            return;
        }

        MetadataReader metadataReader = peReader.GetMetadataReader();

        var assemblyDefinition = metadataReader.GetAssemblyDefinition();

        foreach (var customAttributeHandle in assemblyDefinition.GetCustomAttributes())
        {
            var customAttribute = metadataReader.GetCustomAttribute(customAttributeHandle);
            string attributeName = GetAttributeFullName(metadataReader, customAttribute);
            if (attributeName == "System.Runtime.Versioning.TargetFrameworkAttribute")
            {
                var value = GetCustomAttributeStringArgument(metadataReader, customAttribute);
                string targetFramework = value;
                Console.WriteLine($"Target framework: {targetFramework}");
            }
            else if (attributeName == "System.Reflection.AssemblyFileVersionAttribute")
            {
                var fileVersion = GetCustomAttributeStringArgument(metadataReader, customAttribute);
                Console.WriteLine($"File version: {fileVersion}");
            }
            else if (attributeName == "System.Reflection.AssemblyInformationalVersionAttribute")
            {
                var informationalVersion = GetCustomAttributeStringArgument(metadataReader, customAttribute);
                Console.WriteLine($"Informational version: {informationalVersion}");
            }
        }
    }

    private static string GetCustomAttributeStringArgument(MetadataReader reader, System.Reflection.Metadata.CustomAttribute attribute)
    {
        var signature = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Signature;
        var signatureReader = reader.GetBlobReader(signature);
        var valueReader = reader.GetBlobReader(attribute.Value);

        var prolog = valueReader.ReadUInt16();
        if (prolog != 1)
        {
            // Invalid custom attribute prolog
            return null;
        }

        var header = signatureReader.ReadSignatureHeader();
        if (header.Kind != SignatureKind.Method || header.IsGeneric)
        {
            // Invalid custom attribute constructor signature
            return null;
        }

        int parameterCount;
        if (!signatureReader.TryReadCompressedInteger(out parameterCount))
        {
            // Invalid custom attribute constructor signature
            return null;
        }

        var returnType = signatureReader.ReadSignatureTypeCode();
        if (returnType != SignatureTypeCode.Void)
        {
            // Invalid custom attribute constructor signature
            return null;
        }

        if (parameterCount != 1)
        {
            return null;
        }

        var signatureTypeCode = signatureReader.ReadSignatureTypeCode();
        if (signatureTypeCode == SignatureTypeCode.String)
        {
            // Custom attribute constructor must take only strings
            string result = valueReader.ReadSerializedString();
            return result;
        }

        return null;
    }

    internal static string GetAttributeFullName(MetadataReader reader, System.Reflection.Metadata.CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MethodDefinition:
                {
                    var methodDef = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                    var declaringTypeHandle = methodDef.GetDeclaringType();
                    var typeDefinition = reader.GetTypeDefinition(declaringTypeHandle);
                    var @namespace = reader.GetString(typeDefinition.Namespace);
                    var name = reader.GetString(typeDefinition.Name);
                    return $"{@namespace}.{name}";
                }
            case HandleKind.MemberReference:
                {
                    var memberRef = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    var typeRef = reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                    var @namespace = reader.GetString(typeRef.Namespace);
                    var name = reader.GetString(typeRef.Name);
                    return $"{@namespace}.{name}";
                }
            default:
                throw new InvalidOperationException();
        }
    }

}
