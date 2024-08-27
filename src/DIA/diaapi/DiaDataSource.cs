using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VisualStudioProvider.PDB.raw;
using Microsoft.VisualStudio;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.IO;
using System.Diagnostics;

namespace VisualStudioProvider.PDB.diaapi
{
    public unsafe class DiaDataSource
    {
        private Guid CLSID_DiaSource64 = Guid.Parse("E6756135-1E65-4D17-8576-610761398C3C");
        private Guid CLSID_DiaSource32 = Guid.Parse("B86AE24D-BF2F-4ac9-B5A2-34B14E4CE11D");
        private Guid IID_IDiaDataSource = Guid.Parse("79F1BB5F-B66E-48e5-B6A9-1545C323CA3D");
        private Guid IID_IDiaDataSource8 = Guid.Parse("D808F8D0-0F8D-4CA7-8A05-8963F7D5F9F1");
        private IDiaDataSource dataSource;
        private IDiaSession2 session;
        public string Version { get; private set; }
        public string GlobalName { get; private set; }
        public DiaSymbol GlobalScope { get; private set; }
        private IDiaSymbol globalScope;

        public DiaDataSource()
        {
            int hr;
            IntPtr pUnk;

            try
            {
                hr = NativeMethods.CoCreateInstance(CLSID_DiaSource64, IntPtr.Zero, 1, IID_IDiaDataSource8, out pUnk);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                dataSource = (IDiaDataSource)Marshal.GetObjectForIUnknown(pUnk);
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        public void LoadPdb(string pdbFile)
        {
            int hr;
            string globalName;

            hr = dataSource.loadDataFromPdb(pdbFile);

            if (hr != VSConstants.S_OK)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            hr = dataSource.openSession(out session);

            if (hr != VSConstants.S_OK)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            hr = session.get_globalScope(out globalScope);

            if (hr != VSConstants.S_OK)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            hr = globalScope.get_name(out globalName);

            if (hr != VSConstants.S_OK)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            this.GlobalName = globalName;
            this.GlobalScope = new DiaSymbol(globalScope);
        }

        public void LoadExe(string imageFile)
        {
            int hr;
            var searchPath = Environment.ExpandEnvironmentVariables(@"%localappdata%\Temp\SymbolCache");
            string globalName;

            hr = dataSource.loadDataForExe(imageFile, searchPath, null);

            if (hr != VSConstants.S_OK)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            hr = dataSource.openSession(out session);

            if (hr != VSConstants.S_OK)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            hr = session.get_globalScope(out globalScope);

            if (hr != VSConstants.S_OK)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            hr = globalScope.get_name(out globalName);

            if (hr != VSConstants.S_OK)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            this.GlobalName = globalName;
            this.GlobalScope = new DiaSymbol(globalScope);
        }

        public DiaSymbol FindSymbolByRVA(IntPtr rva, SymTagEnum symTag)
        {
            int hr;
            IDiaSymbol symbol;

            hr = session.findSymbolByRVA((uint)rva, symTag, out symbol);

            if (hr != VSConstants.S_OK)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            return new DiaSymbol(symbol);
        }

        public unsafe EnumObjects<DiaTable> Tables
        {
            get
            {
                int hr;
                IDiaEnumTables enumTables;

                hr = session.getEnumTables(out enumTables);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return new EnumObjects<DiaTable>(() =>      // count
                    {
                        var count = 0;

                        hr = enumTables.get_Count(ref count);

                        if (hr != VSConstants.S_OK)
                        {
                            Marshal.ThrowExceptionForHR(hr);
                        }

                        return count;

                    }, (t) =>                                // next
                    {
                        uint fetched = 0;
                        IntPtr pTable;
                        IDiaTable table;

                        hr = enumTables.Next(1, out pTable, ref fetched);

                        if (fetched > 0)
                        {
                            table = (IDiaTable)Marshal.GetObjectForIUnknown(pTable);

                            t.InternalValue = table;
                            t.Value = new DiaTable(table);

                            return true;
                        }
                        else
                        {
                            return false;
                        }

                    }, (i) =>                               // item
                    {
                        IDiaTable diaTable;

                        hr = enumTables.Item(i, out diaTable);

                        if (hr != VSConstants.S_OK)
                        {
                            Marshal.ThrowExceptionForHR(hr);
                        }

                        return new DiaTable(diaTable);

                    }, () => enumTables.Reset());           // reset
            }
        }
    }
}
