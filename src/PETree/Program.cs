using System.Collections.Generic;
using System.IO;
using System.Linq;
using GuiLabs.FileFormat;
using GuiLabs.Utilities;

namespace GuiLabs.PEFile;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            return;
        }

        var filePath = args[0];
        if (!File.Exists(filePath))
        {
            return;
        }

        using var stream = new FileStream(filePath, FileMode.Open);

        var length = stream.Length;
        var buffer = new StreamBuffer(stream);

        var peFile = new PEFile(buffer);
        peFile.Parse();
        peFile.Length = (int)length;

        var uncovered = new List<(Span, string)>();
        peFile.ComputeUncoveredSpans(s => uncovered.Add((s, buffer.ReadBytes(s.Start, s.Length).Take(32).ToArray().ToHexString())));
    }
}
