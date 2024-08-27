using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;

namespace VisualStudioProvider.PDB.raw
{
    [ComImport, Guid("C65C2B0A-1150-4d7a-AFCC-E05BF3DEE81E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public unsafe interface IDiaEnumTables
    {
        int get__NewEnum(out IntPtr pRetVal);
        int get_Count(ref int pRetVal);
        int Item(object index, out IDiaTable table);
        int Next(uint celt, out IntPtr rgelt, ref uint pceltFetched);
        int Skip(uint celt);
        int Reset();
        int Clone(out IDiaEnumTables ppenum);
    }
}
