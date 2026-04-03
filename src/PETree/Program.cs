using System.Collections.Generic;
using System.IO;
using System.Linq;
using GuiLabs.FileFormat;
using GuiLabs.FileFormat.PE;
using GuiLabs.Utilities;

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

        var peFile = PEFile.ReadFromFile(filePath);

        var textFile = Path.ChangeExtension(filePath, ".txt");

        var text = peFile.GetText();
        File.WriteAllText(textFile, text);

        if (args.Length == 2)
        {
            var filePath2 = args[1];
            if (!File.Exists(filePath2))
            {
                return;
            }

            var peFile2 = PEFile.ReadFromFile(filePath2);

            var diff = Difference.Diff(peFile, peFile2);
        }
    }
}
