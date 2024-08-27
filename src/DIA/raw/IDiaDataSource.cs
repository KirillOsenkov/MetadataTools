using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;

namespace VisualStudioProvider.PDB.raw
{
    [ComImport, Guid("79F1BB5F-B66E-48e5-B6A9-1545C323CA3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDiaDataSource
    {
        int get_lastError(string pRetVal);
        int loadDataFromPdb(string pdbPath);
        int loadAndValidateDataFromPdb(string pdbPath, Guid pcsig70, uint sig, uint age);
        int loadDataForExe(string executable, string searchPath, IDiaLoadCallback pCallback);
        int loadDataFromIStream(System.Runtime.InteropServices.ComTypes.IStream pIStream);
        int openSession(out IDiaSession2 ppSession);
    }
}
