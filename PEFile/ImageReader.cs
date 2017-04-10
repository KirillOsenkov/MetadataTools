using System;
using System.IO;
using System.Linq;
using RVA = System.UInt32;
using TargetArchitecture = System.UInt16;

namespace PEFile
{
    public sealed class ImageReader : BinaryStreamReader
    {
        readonly Image image;

        DataDirectory cli;
        DataDirectory metadata;

        uint table_heap_offset;

        public ImageReader(Stream stream)
            : base(stream)
        {
            image = new Image();
            image.Stream = stream;
        }

        public static Guid ReadAssemblyMvid(string filePath)
        {
            Guid mvid = Guid.Empty;
            try
            {
                using (var stream = File.OpenRead(filePath))
                {
                    var imageReader = new ImageReader(stream);
                    if (imageReader.ReadImage())
                    {
                        mvid = imageReader.Mvid;
                    }
                }
            }
            catch (Exception)
            {
            }

            return mvid;
        }

        void MoveTo(DataDirectory directory)
        {
            BaseStream.Position = image.ResolveVirtualAddress(directory.VirtualAddress);
        }

        public bool ReadImage()
        {
            if (BaseStream.Length < 128)
                return false;

            // - DOSHeader

            // PE					2
            // Start				58
            // Lfanew				4
            // End					64

            if (ReadUInt16() != 0x5a4d)
                return false;

            Advance(58);

            MoveTo(ReadUInt32());

            if (ReadUInt32() != 0x00004550)
                return false;

            // - PEFileHeader

            // Machine				2
            image.Architecture = ReadArchitecture();

            // NumberOfSections		2
            ushort sections = ReadUInt16();

            // TimeDateStamp		4
            image.Timestamp = ReadUInt32();
            // PointerToSymbolTable	4
            // NumberOfSymbols		4
            // OptionalHeaderSize	2
            Advance(10);

            // Characteristics		2
            ushort characteristics = ReadUInt16();

            ushort subsystem, dll_characteristics;
            ReadOptionalHeaders(out subsystem, out dll_characteristics);
            ReadSections(sections);
            ReadCLIHeader();
            ReadMetadata();

            return true;
        }

        TargetArchitecture ReadArchitecture()
        {
            return ReadUInt16();
        }

        //static ModuleKind GetModuleKind(ushort characteristics, ushort subsystem)
        //{
        //    if ((characteristics & 0x2000) != 0) // ImageCharacteristics.Dll
        //        return ModuleKind.Dll;

        //    if (subsystem == 0x2 || subsystem == 0x9) // SubSystem.WindowsGui || SubSystem.WindowsCeGui
        //        return ModuleKind.Windows;

        //    return ModuleKind.Console;
        //}

        void ReadOptionalHeaders(out ushort subsystem, out ushort dll_characteristics)
        {
            // - PEOptionalHeader
            //   - StandardFieldsHeader

            // Magic				2
            bool pe64 = ReadUInt16() == 0x20b;

            //						pe32 || pe64

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
            Advance(66);

            // SubSystem			2
            subsystem = ReadUInt16();

            // DLLFlags				2
            dll_characteristics = ReadUInt16();
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

            Advance(pe64 ? 88 : 72);

            // Debug				8
            image.Debug = ReadDataDirectory();

            // Copyright			8
            // GlobalPtr			8
            // TLSTable				8
            // LoadConfigTable		8
            // BoundImport			8
            // IAT					8
            // DelayImportDescriptor8
            Advance(56);

            // CLIHeader			8
            cli = ReadDataDirectory();

            if (cli.IsZero)
                throw new BadImageFormatException();

            // Reserved				8
            Advance(8);
        }

        string ReadAlignedString(int length)
        {
            int read = 0;
            var buffer = new char[length];
            while (read < length)
            {
                var current = ReadByte();
                if (current == 0)
                    break;

                buffer[read++] = (char)current;
            }

            Advance(-1 + ((read + 4) & ~3) - read);

            return new string(buffer, 0, read);
        }

        string ReadZeroTerminatedString(int length)
        {
            int read = 0;
            var buffer = new char[length];
            var bytes = ReadBytes(length);
            while (read < length)
            {
                var current = bytes[read];
                if (current == 0)
                    break;

                buffer[read++] = (char)current;
            }

            return new string(buffer, 0, read);
        }

        void ReadSections(ushort count)
        {
            var sections = new Section[count];

            for (int i = 0; i < count; i++)
            {
                var section = new Section();

                // Name
                section.Name = ReadZeroTerminatedString(8);

                // VirtualSize		4
                Advance(4);

                // VirtualAddress	4
                section.VirtualAddress = ReadUInt32();
                // SizeOfRawData	4
                section.SizeOfRawData = ReadUInt32();
                // PointerToRawData	4
                section.PointerToRawData = ReadUInt32();

                // PointerToRelocations		4
                // PointerToLineNumbers		4
                // NumberOfRelocations		2
                // NumberOfLineNumbers		2
                // Characteristics			4
                Advance(16);

                sections[i] = section;
            }

            image.Sections = sections;
        }

        void ReadCLIHeader()
        {
            MoveTo(cli);

            // - CLIHeader

            // Cb						4
            // MajorRuntimeVersion		2
            // MinorRuntimeVersion		2
            Advance(8);

            // Metadata					8
            metadata = ReadDataDirectory();
            // Flags					4
            //image.Attributes = 
            ReadUInt32();
            // EntryPointToken			4
            image.EntryPointToken = ReadUInt32();
            // Resources				8
            image.Resources = ReadDataDirectory();
            // StrongNameSignature		8
            image.StrongName = ReadDataDirectory();
            // CodeManagerTable			8
            // VTableFixups				8
            // ExportAddressTableJumps	8
            // ManagedNativeHeader		8
        }

        void ReadMetadata()
        {
            MoveTo(metadata);

            if (ReadUInt32() != 0x424a5342)
                throw new BadImageFormatException();

            // MajorVersion			2
            // MinorVersion			2
            // Reserved				4
            Advance(8);

            image.RuntimeVersion = ReadZeroTerminatedString(ReadInt32());

            // Flags		2
            Advance(2);

            var streams = ReadUInt16();

            var section = image.GetSectionAtVirtualAddress(metadata.VirtualAddress);
            if (section == null)
                throw new BadImageFormatException();

            image.MetadataSection = section;

            for (int i = 0; i < streams; i++)
                ReadMetadataStream(section);
        }

        void ReadDebugHeader()
        {
            if (image.Debug.IsZero)
            {
                return;
            }

            MoveTo(image.Debug);

            var entries = (int)image.Debug.Size / 28;

            for (int i = 0; i < entries; i++)
            {
                var directory = new
                {
                    Characteristics = ReadInt32(),
                    TimeDateStamp = ReadInt32(),
                    MajorVersion = ReadInt16(),
                    MinorVersion = ReadInt16(),
                    Type = ReadInt32(),
                    SizeOfData = ReadInt32(),
                    AddressOfRawData = ReadInt32(),
                    PointerToRawData = ReadInt32(),
                };

                if (directory.AddressOfRawData == 0)
                {
                    continue;
                }

                var position = Position;
                try
                {
                    MoveTo((uint)directory.PointerToRawData);
                    var data = ReadBytes(directory.SizeOfData);
                }
                finally
                {
                    Position = position;
                }
            }
        }

        public Guid Mvid;

        void ReadMetadataStream(Section section)
        {
            // Offset		4
            uint offset = metadata.VirtualAddress - section.VirtualAddress + ReadUInt32(); // relative to the section start

            // Size			4
            uint size = ReadUInt32();

            var data = ReadHeapData(offset, size);

            var name = ReadAlignedString(16);
            switch (name)
            {
                case "#~":
                case "#-":
                    //image.TableHeap = new TableHeap(data);
                    table_heap_offset = offset;
                    break;
                case "#Strings":
                    //image.StringHeap = new StringHeap(data);
                    break;
                case "#Blob":
                    //image.BlobHeap = new BlobHeap(data);
                    break;
                case "#GUID":
                    //image.GuidHeap = new GuidHeap(data);
                    if (data.Length >= 16)
                    {
                        Mvid = new Guid(data.Take(16).ToArray());
                    }
                    break;
                case "#US":
                    //image.UserStringHeap = new UserStringHeap(data);
                    break;
                case "#Pdb":
                    //image.PdbHeap = new PdbHeap(data);
                    break;
            }
        }

        byte[] ReadHeapData(uint offset, uint size)
        {
            var position = BaseStream.Position;
            MoveTo(offset + image.MetadataSection.PointerToRawData);
            var data = ReadBytes((int)size);
            BaseStream.Position = position;

            return data;
        }

        void ReadTableHeap()
        {
            MoveTo(table_heap_offset + image.MetadataSection.PointerToRawData);

            // Reserved			4
            // MajorVersion		1
            // MinorVersion		1
            Advance(6);

            // HeapSizes		1
            var sizes = ReadByte();

            // Reserved2		1
            Advance(1);

            // Valid			8
            //heap.Valid = 
            ReadInt64();

            // Sorted			8
            //heap.Sorted = 
            ReadInt64();
        }
    }

    public class BinaryStreamReader : BinaryReader
    {
        public int Position
        {
            get { return (int)BaseStream.Position; }
            set { BaseStream.Position = value; }
        }

        public int Length
        {
            get { return (int)BaseStream.Length; }
        }

        public BinaryStreamReader(Stream stream)
            : base(stream)
        {
        }

        public void Advance(int bytes)
        {
            BaseStream.Seek(bytes, SeekOrigin.Current);
        }

        public void MoveTo(uint position)
        {
            BaseStream.Seek(position, SeekOrigin.Begin);
        }

        public void Align(int align)
        {
            align--;
            var position = Position;
            Advance(((position + align) & ~align) - position);
        }

        internal DataDirectory ReadDataDirectory()
        {
            return new DataDirectory(ReadUInt32(), ReadUInt32());
        }
    }

    struct DataDirectory
    {
        public readonly RVA VirtualAddress;
        public readonly uint Size;

        public bool IsZero
        {
            get { return VirtualAddress == 0 && Size == 0; }
        }

        public DataDirectory(RVA rva, uint size)
        {
            this.VirtualAddress = rva;
            this.Size = size;
        }
    }

    sealed class Section
    {
        public string Name;
        public RVA VirtualAddress;
        public uint SizeOfRawData;
        public uint PointerToRawData;
    }

    sealed class Image : IDisposable
    {
        public Stream Stream;

        public string RuntimeVersion;
        public TargetArchitecture Architecture;

        public Section[] Sections;

        public Section MetadataSection;

        public uint EntryPointToken;
        public uint Timestamp;

        public DataDirectory Debug;
        public DataDirectory Resources;
        public DataDirectory StrongName;

        public uint ResolveVirtualAddress(RVA rva)
        {
            var section = GetSectionAtVirtualAddress(rva);
            if (section == null)
                throw new ArgumentOutOfRangeException();

            return ResolveVirtualAddressInSection(rva, section);
        }

        public uint ResolveVirtualAddressInSection(RVA rva, Section section)
        {
            return rva + section.PointerToRawData - section.VirtualAddress;
        }

        public Section GetSectionAtVirtualAddress(RVA rva)
        {
            var sections = this.Sections;
            for (int i = 0; i < sections.Length; i++)
            {
                var section = sections[i];
                if (rva >= section.VirtualAddress && rva < section.VirtualAddress + section.SizeOfRawData)
                    return section;
            }

            return null;
        }

        public void Dispose()
        {
            Stream.Dispose();
        }
    }
}