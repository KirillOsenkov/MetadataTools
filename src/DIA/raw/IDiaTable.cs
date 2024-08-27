using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;

namespace VisualStudioProvider.PDB.raw
{
    [ComImport, Guid("4A59FB77-ABAC-469b-A30B-9ECC85BFEF14"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDiaTable : IEnumUnknown
    {
        int get__NewEnum(object[] pRetVal);
        int get_name(out string pRetVal);
        int get_Count(ref int pRetVal);
        int Item(uint index, out object element);
    }
}
