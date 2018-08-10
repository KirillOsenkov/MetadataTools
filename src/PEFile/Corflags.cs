using Mono.Cecil;

namespace PEFile
{
    public class Corflags
    {
        public static void Set32BitPreferred(string filePath, bool prefer32Bit)
        {
            using (var module = ModuleDefinition.ReadModule(filePath, new ReaderParameters { InMemory = true }))
            {
                var attributes = module.Attributes;
                var old = (attributes & ModuleAttributes.Preferred32Bit) == ModuleAttributes.Preferred32Bit;
                if (old != prefer32Bit)
                {
                    if (prefer32Bit)
                    {
                        attributes = attributes | ModuleAttributes.Preferred32Bit;
                    }
                    else
                    {
                        attributes = attributes & ~ModuleAttributes.Preferred32Bit;
                        attributes = attributes & ~ModuleAttributes.Required32Bit;
                    }

                    module.Attributes = attributes;
                    module.Write(filePath);
                }
            }
        }
    }
}
