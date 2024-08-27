using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VisualStudioProvider.PDB.raw;
using Microsoft.VisualStudio;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.IO;

namespace VisualStudioProvider.PDB.diaapi
{
    [DebuggerDisplay(" { DebugInfo } ")]
    public class DiaSymbol
    {
        private IDiaSymbol symbol;

        public DiaSymbol(IDiaSymbol symbol)
        {
            this.symbol = symbol;
        }

        public uint IndexId
        {
            get
            {
                int hr;
                uint id = 0;

                hr = symbol.get_symIndexId(ref id);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return id;
            }
        }

        public string Name
        {
            get
            {
                int hr;
                string name;

                hr = symbol.get_name(out name);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return name;
            }
        }

        public string DebugInfo
        {
            get
            {
                if (this.SymTag == SymTagEnum.SymTagCompiland)
                {
                    return string.Format("{0} [{1}]", Path.GetFileName(this.LibraryName), Path.GetFileName(this.Name));
                }
                else
                {
                    return string.Format("{0}, RVA=[{1:X8}]", this.UndecoratedName.ToString(), (ulong) this.RVA);
                }
            }
        }

        public DataKind DataKind
        {
            get
            {
                int hr;
                uint dataKind = 0;

                hr = symbol.get_dataKind(ref dataKind);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return (DataKind) dataKind;
            }
        }

        public uint AddressOffset
        {
            get
            {
                int hr;
                uint offset = 0;

                hr = symbol.get_addressOffset(ref offset);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return offset;
            }
        }

        public uint AddressSection
        {
            get
            {
                int hr;
                uint section = 0;

                hr = symbol.get_addressSection(ref section);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return section;
            }
        }

        public int Offset
        {
            get
            {
                int hr;
                int offset = 0;

                hr = symbol.get_offset(ref offset);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return offset;
            }
        }

        public ulong Length
        {
            get
            {
                int hr;
                ulong length = 0;

                hr = symbol.get_length(ref length);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return length;
            }
        }

        public IntPtr RVA
        {
            get
            {
                int hr;
                uint rva = 0;

                hr = symbol.get_relativeVirtualAddress(ref rva);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return (IntPtr) rva;
            }
        }

        public SymTagEnum SymTag
        {
            get
            {
                int hr;
                uint symTag = 0;

                hr = symbol.get_symTag(ref symTag);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return (SymTagEnum)symTag;
            }
        }

        public LocationType LocationType
        {
            get
            {
                int hr;
                uint locationType = 0;

                hr = symbol.get_locationType(ref locationType);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return (LocationType) locationType;
            }
        }

        public BasicType BasicType
        {
            get
            {
                int hr;
                uint basicType = 0;

                hr = symbol.get_baseType(ref basicType);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return (BasicType)basicType;
            }
        }

        public Language Language
        {
            get
            {
                int hr;
                uint language = 0;

                hr = symbol.get_language(ref language);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return (Language)language;
            }
        }

        public Guid Guid
        {
            get
            {
                int hr;
                var guid = Guid.Empty;

                hr = symbol.get_guid(ref guid);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return guid;
            }
        }

        public string LibraryName
        {
            get
            {
                int hr;
                string libraryName;

                hr = symbol.get_libraryName(out libraryName);

                return libraryName;
            }
        }

        public string SymbolsFileName
        {
            get
            {
                int hr;
                string symbolsFileName;

                hr = symbol.get_symbolsFileName(out symbolsFileName);

                return symbolsFileName;
            }
        }

        public string SourceFileName
        {
            get
            {
                int hr;
                string sourceFileName;

                hr = symbol.get_sourceFileName(out sourceFileName);

                return sourceFileName;
            }
        }

        public string UndecoratedName
        {
            get
            {
                string str;
                string empty;

                if (this.symbol.get_undecoratedName(out str) != 0)
                {
                    empty = string.Empty;
                }
                else if (str != null)
                {
                    if (this.Name == str)
                    {
                        var regex = new Regex(string.Format("_(?<name>{0})@(?<argbytes>\\d+)", "(?:(?(?!\\d)\\w+(?:\\.(?!\\d)\\w+)*)\\.)?((?!\\d)\\w+)"));

                        if (!regex.IsMatch(str))
                        {
                            regex = new Regex(string.Format("(?<name>{0})@(?<argbytes>\\d+)", "(?:(?(?!\\d)\\w+(?:\\.(?!\\d)\\w+)*)\\.)?((?!\\d)\\w+)"));

                            if (regex.IsMatch(str))
                            {
                                var match = regex.Match(str);
                                var value = match.Groups["name"].Value;
                                var value1 = match.Groups["argbytes"].Value;

                                empty = value;

                                return empty;
                            }
                        }
                        else
                        {
                            var match1 = regex.Match(str);
                            var str1 = match1.Groups["name"].Value;
                            var value2 = match1.Groups["argbytes"].Value;

                            empty = str1;

                            return empty;
                        }
                    }

                    empty = str;
                }
                else
                {
                    empty = null;
                }

                return empty;
            }
        }

        public unsafe EnumObjects<DiaSymbol> GetChildren(SymTagEnum symbolTag)
        {
            int hr;
            IDiaEnumSymbols enumSymbols;

            hr = symbol.findChildren(symbolTag, null, 0, out enumSymbols);

            if (hr != VSConstants.S_OK)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            if (enumSymbols == null)
            {
                return new EnumObjects<DiaSymbol>(); 
            }

            return new EnumObjects<DiaSymbol>(() =>      // count
            {
                var count = 0;

                hr = enumSymbols.get_Count(ref count);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return count;

            }, (t) =>                                // next
            {
                uint fetched = 0;
                IntPtr pSymbol;
                IDiaSymbol symbolChild;

                hr = enumSymbols.Next(1, out pSymbol, ref fetched);

                if (fetched > 0)
                {
                    symbolChild = (IDiaSymbol)Marshal.GetObjectForIUnknown(pSymbol);

                    t.InternalValue = symbolChild;
                    t.Value = new DiaSymbol(symbolChild);

                    return true;
                }
                else
                {
                    return false;
                }

            }, (i) =>                               // item
            {
                IDiaSymbol diaSymbol;

                hr = enumSymbols.Item(i, out diaSymbol);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return new DiaSymbol(diaSymbol);

            }, () => enumSymbols.Reset());           // reset
        }

        public unsafe EnumObjects<DiaSymbol> Children
        {
            get
            {
                int hr;
                IDiaEnumSymbols enumSymbols;

                hr = symbol.findChildren(SymTagEnum.SymTagNull, null, 0, out enumSymbols);

                if (hr != VSConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                if (enumSymbols == null)
                {
                    return new EnumObjects<DiaSymbol>(); 
                }

                return new EnumObjects<DiaSymbol>(() =>      // count
                {
                    var count = 0;

                    hr = enumSymbols.get_Count(ref count);

                    if (hr != VSConstants.S_OK)
                    {
                        Marshal.ThrowExceptionForHR(hr);
                    }

                    return count;

                }, (t) =>                                // next
                {
                    uint fetched = 0;
                    IntPtr pSymbol;
                    IDiaSymbol symbolChild;

                    hr = enumSymbols.Next(1, out pSymbol, ref fetched);

                    if (fetched > 0)
                    {
                        symbolChild = (IDiaSymbol)Marshal.GetObjectForIUnknown(pSymbol);

                        t.InternalValue = symbolChild;
                        t.Value = new DiaSymbol(symbolChild);

                        return true;
                    }
                    else
                    {
                        return false;
                    }

                }, (i) =>                               // item
                {
                    IDiaSymbol diaSymbol;

                    hr = enumSymbols.Item(i, out diaSymbol);

                    if (hr != VSConstants.S_OK)
                    {
                        Marshal.ThrowExceptionForHR(hr);
                    }

                    return new DiaSymbol(diaSymbol);

                }, () => enumSymbols.Reset());           // reset
            }
        }
    }
}
