using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;

namespace VisualStudioProvider.PDB.raw
{
    [ComImport, Guid("08CBB41E-47A6-4f87-92F1-1C9C87CED044"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDiaEnumDebugStreamData
    {
        int get__NewEnum(out IntPtr pRetVal);
        int get_Count(ref int pRetVal);
        int get_name(out string pRetVal);
        int Item(object index, out IDiaTable table);
        int Next(uint celt, int cbData, out int pcbData, IntPtr data, ref uint pceltFetched);
        int Skip(uint celt);
        int Reset();
        int Clone(out IDiaEnumDebugStreamData ppenum);
    }
}
