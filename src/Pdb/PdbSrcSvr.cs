using Microsoft.DiaSymReader;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System;

namespace MetadataTools
{
    public class PdbSrcSvr
    {
        public static IEnumerable<string> GetSourceServerData(object reader)
        {
            if (reader is ISymUnmanagedReader5 reader5)
            {
                var result = GetSourceServerData(reader5);
                if (result != null)
                {
                    yield return result;
                }
            }

            if (reader is ISymUnmanagedSourceServerModule sourceServerModule)
            {
                var result = GetSourceServerData(sourceServerModule);
                if (result != null)
                {
                    yield return result;
                }
            }
        }

        public static unsafe string GetSourceServerData(ISymUnmanagedReader5 symReader)
        {
            if (symReader.GetSourceServerData(out byte* data, out int size) < 0 || size == 0)
            {
                return null;
            }

            var buffer = new byte[size];
            Marshal.Copy((IntPtr)data, buffer, 0, buffer.Length);
            return GetText(buffer);
        }

        public static unsafe string GetSourceServerData(ISymUnmanagedSourceServerModule unmanagedSourceServerModule)
        {
            byte* srcSrvData = (byte*)IntPtr.Zero;
            try
            {
                if (unmanagedSourceServerModule.GetSourceServerData(out int sizeData, out srcSrvData) == 0)
                {
                    if (sizeData == 0)
                    {
                        return null;
                    }

                    var data = new byte[sizeData];
                    Marshal.Copy((IntPtr)srcSrvData, data, 0, data.Length);
                    return GetText(data);
                }
            }
            finally
            {
                if ((IntPtr)srcSrvData != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem((IntPtr)srcSrvData);
                }
            }

            return null;
        }

        private static string GetText(byte[] buffer)
        {
            var stream = new MemoryStream(buffer);
            string text = new StreamReader(stream).ReadToEnd();
            return text;
        }
    }
}