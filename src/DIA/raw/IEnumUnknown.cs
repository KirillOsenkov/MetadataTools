using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;

namespace VisualStudioProvider.PDB.raw
{
    [ComImport, Guid("00000100-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	public interface IEnumUnknown
	{
		int Next(uint celt, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex=0)] object[] rgelt, out uint pceltFetched);
		int Skip(uint celt);
		int Reset();
		int Clone(out IEnumUnknown ppenum);
	}
}
