using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VisualStudioProvider.PDB.raw;
using Microsoft.VisualStudio;
using System.Runtime.InteropServices;

namespace VisualStudioProvider.PDB.diaapi
{
    public class DiaTable
    {
        private IDiaTable table;

        public DiaTable(IDiaTable table)
        {
            this.table = table;
        }

        public int Count
        {
            get
            {
                int hr;
                var count = 0;

                hr = table.get_Count(ref count);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return count;
            }
        }

        public string Name
        {
            get
            {
                int hr;
                string name;

                hr = table.get_name(out name);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return name;
            }
        }
    }
}
