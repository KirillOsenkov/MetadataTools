//
// Author:
//   Jb Evain (jbevain@gmail.com)
//
// Copyright (c) 2008 - 2015 Jb Evain
// Copyright (c) 2008 - 2011 Novell, Inc.
//
// Licensed under the MIT/X11 license.
//

using System.IO;

namespace GuiLabs.Metadata
{
    class ReadHeaders
    {
        private static void ReadManually(string filePath)
        {
            using (var stream = File.OpenRead(filePath))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 128)
                {
                    return;
                }

                if (reader.ReadUInt16() != 0x5a4d)
                {
                    return;
                }

                stream.Position += 58;
                stream.Position = reader.ReadUInt32();
                if (reader.ReadUInt32() != 0x00004550)
                {
                    return;
                }

                var architecture = reader.ReadUInt16();

                ushort sections = reader.ReadUInt16();

                var timestamp = reader.ReadInt32();

                // PointerToSymbolTable 4
                // NumberOfSymbols 4
                // OptionalHeaderSize 2
                stream.Position += 10;

                var characteristics = reader.ReadUInt16();

                // optional headers
                //  PEOptionalHeader
                //  StandardFieldsHeader
                // Magic
                bool pe64 = reader.ReadUInt16() == 0x20b;

                // LMajor				1
                // LMinor				1
                // CodeSize				4
                // InitializedDataSize	4
                // UninitializedDataSize4
                // EntryPointRVA		4
                // BaseOfCode			4
                // BaseOfData			4 || 0

                //   - NTSpecificFieldsHeader

                // ImageBase			4 || 8
                // SectionAlignment		4
                // FileAlignement		4
                // OSMajor				2
                // OSMinor				2
                // UserMajor			2
                // UserMinor			2
                // SubSysMajor			2
                // SubSysMinor			2
                // Reserved				4
                // ImageSize			4
                // HeaderSize			4
                // FileChecksum			4
                stream.Position += 66;

                var subsystem = reader.ReadUInt16();
                var dllCharacteristics = reader.ReadUInt16();

                // StackReserveSize		4 || 8
                // StackCommitSize		4 || 8
                // HeapReserveSize		4 || 8
                // HeapCommitSize		4 || 8
                // LoaderFlags			4
                // NumberOfDataDir		4

                //   - DataDirectoriesHeader

                // ExportTable			8
                // ImportTable			8
                // ResourceTable		8
                // ExceptionTable		8
                // CertificateTable		8
                // BaseRelocationTable	8
                stream.Position += pe64 ? 88 : 72;

                // Debug data directory
                stream.Position += 4;

                // Copyright			8
                // GlobalPtr			8
                // TLSTable				8
                // LoadConfigTable		8
                // BoundImport			8
                // IAT					8
                // DelayImportDescriptor8
                stream.Position += 56;

                // CLIHeader data directory
                stream.Position += 4;

                // Reserved
                stream.Position += 8;

                // sections
                ReadSections(sections, reader, stream);

                // CLI header
            }
        }

        static void ReadSections(ushort count, BinaryReader reader, Stream stream)
        {
            for (int i = 0; i < count; i++)
            {
                // Name
                stream.Position += 8;

                // VirtualSize		4
                stream.Position += 8;

                // VirtualAddress	4
                stream.Position += 4;

                // SizeOfRawData	4
                stream.Position += 4;

                // PointerToRawData	4
                stream.Position += 4;

                // PointerToRelocations		4
                // PointerToLineNumbers		4
                // NumberOfRelocations		2
                // NumberOfLineNumbers		2
                // Characteristics			4
                stream.Position += 16;
            }
        }
    }
}
