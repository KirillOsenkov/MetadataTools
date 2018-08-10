using System.IO;
using System.Reflection.PortableExecutable;
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

        public static void Set32BitPreferredSRM(string filePath, bool prefer32Bit)
        {
            CorFlags attributes;

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite))
            {
                PEReader reader = new PEReader(stream);

                attributes = reader.PEHeaders.CorHeader.Flags;

                var old = (attributes & CorFlags.Prefers32Bit) == CorFlags.Prefers32Bit;
                if (old != prefer32Bit)
                {
                    if (prefer32Bit)
                    {
                        attributes = attributes | CorFlags.Prefers32Bit;
                    }
                    else
                    {
                        attributes = attributes & ~CorFlags.Prefers32Bit;
                        attributes = attributes & ~CorFlags.Requires32Bit;
                    }

                    var offset = reader.PEHeaders.CorHeaderStartOffset;
                    offset += 0x10; // flags offset
                    stream.Position = offset;
                    int value = (int)attributes;
                    stream.WriteByte((byte)(value & 0xFF));
                    stream.WriteByte((byte)(value & 0xFF00));
                    stream.WriteByte((byte)(value & 0xFF0000));
                    stream.WriteByte((byte)(value & 0xFF000000));
                }
            }
        }
    }
}
