using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;

namespace VisualStudioProvider.PDB.raw
{
    [ComImport, Guid("CAB72C48-443B-48f5-9B0B-42F0820AB29A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDiaEnumSymbols
    {
        int get__NewEnum(out IntPtr pRetVal);
        int get_Count(ref int pRetVal);
        int Item(object index, out IDiaSymbol table);
        int Next(uint celt, out IntPtr rgelt, ref uint pceltFetched);
        int Skip(uint celt);
        int Reset();
        int Clone(out IDiaEnumTables ppenum);
    }
}
