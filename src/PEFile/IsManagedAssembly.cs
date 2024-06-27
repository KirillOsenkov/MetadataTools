//
// Author:
//   Jb Evain (jbevain@gmail.com)
//
// Copyright (c) 2008 - 2015 Jb Evain
// Copyright (c) 2008 - 2011 Novell, Inc.
//
// Licensed under the MIT/X11 license.
//

using System;
using System.IO;
using System.Text;

namespace GuiLabs.Metadata
{
    // Known issues: returns a false positive for System.Private.CoreLib.ni.dll
    // returns a false positive for an incorrect test binary from the Roslyn testbed
    public class PEFile
    {
        public static bool IsManagedAssembly(string filePath)
        {
            try
            {
                using (Stream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    return IsManagedAssembly(fileStream);
                }
            }
            catch
            {
                return false;
            }
        }

        public bool ManagedAssembly { get; set; }
        public bool Platform64Bit { get; set; }
        public bool IsValidPEFile { get; set; }
        public uint Timestamp { get; private set; }
        public uint SizeOfImage { get; private set; }

        private static PEFile NotAPEFile = new PEFile();
        private static PEFile Native64Bit = new PEFile { IsValidPEFile = true, Platform64Bit = true };
        private static PEFile Native32Bit = new PEFile { IsValidPEFile = true };
        private static PEFile Managed = new PEFile { IsValidPEFile = true, ManagedAssembly = true };

        public static bool IsManagedAssembly(Stream stream)
        {
            return ReadInfo(stream).ManagedAssembly;
        }

        public static bool Is64Bit(Stream stream)
        {
            return ReadInfo(stream).Platform64Bit;
        }

        public static PEFile ReadInfo(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return ReadInfo(stream);
        }

        public static PEFile ReadInfo(Stream stream)
        {
            try
            {
                using (var binaryReader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
                {
                    if (stream.Length < 64)
                    {
                        return NotAPEFile;
                    }

                    // PE Header starts @ 0x3C (60). Its a 4 byte header.
                    stream.Position = 0x3C;
                    uint peHeaderPointer = binaryReader.ReadUInt32();
                    if (peHeaderPointer == 0)
                    {
                        peHeaderPointer = 0x80;
                    }

                    // Ensure there is at least enough room for the following structures:
                    //     24 byte PE Signature & Header
                    //     28 byte Standard Fields         (24 bytes for PE32+)
                    //     68 byte NT Fields               (88 bytes for PE32+)
                    // >= 128 byte Data Dictionary Table
                    if (peHeaderPointer > stream.Length - 256)
                    {
                        return NotAPEFile;
                    }

                    // Check the PE signature.  Should equal 'PE\0\0'.
                    stream.Position = peHeaderPointer;
                    uint peHeaderSignature = binaryReader.ReadUInt32();
                    if (peHeaderSignature != 0x00004550)
                    {
                        return NotAPEFile;
                    }

                    PEFile result = NotAPEFile;

                    ushort platform = (ushort)binaryReader.ReadInt16();

                    // 0x8664 for x64
                    // 0x014C for x86
                    if (platform == 0x8664)
                    {
                        result = Native64Bit;
                    }
                    else if (platform == 0x014C)
                    {
                        result = Native32Bit;
                    }

                    stream.Position += 2;
                    uint timestamp = binaryReader.ReadUInt32();
                    result.Timestamp = timestamp;

                    // skip over the PEHeader fields
                    stream.Position += 12;

                    const ushort PE32 = 0x10b;
                    const ushort PE32Plus = 0x20b;

                    // Read PE magic number from Standard Fields to determine format.
                    var peFormat = binaryReader.ReadUInt16();
                    if (peFormat != PE32 && peFormat != PE32Plus)
                    {
                        return result;
                    }

                    stream.Position = peHeaderPointer + 80;
                    result.SizeOfImage = binaryReader.ReadUInt32();

                    // Read the 15th Data Dictionary RVA field which contains the CLI header RVA.
                    // When this is non-zero then the file contains CLI data otherwise not.
                    ushort dataDictionaryStart = (ushort)(peHeaderPointer + (peFormat == PE32 ? 232 : 248));
                    stream.Position = dataDictionaryStart;

                    uint cliHeaderRva = binaryReader.ReadUInt32();
                    if (cliHeaderRva == 0)
                    {
                        return result;
                    }

                    return Managed;
                }
            }
            catch
            {
                return NotAPEFile;
            }
        }
    }
}
