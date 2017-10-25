using System;
using System.Reflection;
using Microsoft.DiaSymReader;

namespace MetadataTools
{
    public class SymReaderMetadataProvider : ISymReaderMetadataProvider
    {
        public unsafe bool TryGetStandaloneSignature(int standaloneSignatureToken, out byte* signature, out int length)
        {
            throw new NotImplementedException();
        }

        public bool TryGetTypeDefinitionInfo(int typeDefinitionToken, out string namespaceName, out string typeName, out TypeAttributes attributes)
        {
            throw new NotImplementedException();
        }

        public bool TryGetTypeReferenceInfo(int typeReferenceToken, out string namespaceName, out string typeName)
        {
            throw new NotImplementedException();
        }
    }
}
