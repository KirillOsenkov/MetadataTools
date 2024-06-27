using System;
using System.IO;
using System.Reflection.PortableExecutable;

namespace GuiLabs.Metadata
{
    public class Corflags
    {
        static void Main(string[] args)
        {
            if (args.Length < 2 || !File.Exists(args[0]) || args[1] != "/32BITPREF-")
            {
                Console.WriteLine("Usage: corflags Assembly [options]");
                Console.WriteLine("Options:");
                Console.WriteLine("/32BITPREF-");
                return;
            }

            Set32BitPreferredSRM(args[0], prefer32Bit: false);
        }

        public static void Set32BitPreferredSRM(string filePath, bool prefer32Bit)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite))
            {
                PEReader reader = new PEReader(stream);

                CorFlags attributes = reader.PEHeaders.CorHeader.Flags;

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
                    
                    stream.WriteByte((byte)value);
                    stream.WriteByte((byte)(value >> 8));
                    stream.WriteByte((byte)(value >> 16));
                    stream.WriteByte((byte)(value >> 24));
                }
            }
        }
    }
}
