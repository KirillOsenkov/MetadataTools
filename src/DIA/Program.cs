using System.IO;
using VisualStudioProvider.PDB.diaapi;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            return;
        }

        string pdb = args[0];
        if (!File.Exists(pdb))
        {
            return;
        }

        var diaDataSource = new DiaDataSource();
        diaDataSource.LoadPdb(pdb);
    }
}

namespace Microsoft.VisualStudio
{
    public sealed class VSConstants
    {
        public const int S_OK = 0;
    }
}