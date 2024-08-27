using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;

namespace VisualStudioProvider.PDB.raw
{
    [ComImport, Guid("C32ADB82-73F4-421b-95D5-A4706EDF5DBE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public unsafe interface IDiaLoadCallback
    {
        int NotifyDebugDir(int fExecutable, uint cbData, byte* pbData);
        int NotifyOpenDBG([MarshalAs(UnmanagedType.LPWStr)] string dbgPath, int resultCode);
        int NotifyOpenPDB([MarshalAs(UnmanagedType.LPWStr)] string pdbPath, int resultCode);
        int RestrictRegistryAccess();
        int RestrictSymbolServerAccess();
    }
}
