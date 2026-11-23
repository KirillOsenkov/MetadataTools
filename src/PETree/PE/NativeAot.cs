using System;
using System.Collections.Generic;
using System.Linq;

namespace GuiLabs.FileFormat.PE;

public partial class PEFile
{
    public List<NativeAotModuleHeader> NativeAotModuleHeaders { get; } = new();

    public bool IsNativeAot => NativeAotModuleHeaders.Count > 0;

    /// <summary>
    /// A NativeAOT executable has no CLI header. The compiler writes a module
    /// header ("RTR" signature) into read-only data: a table of section ids
    /// with the start and end address of each runtime data region.
    /// </summary>
    private void AddNativeAotModules()
    {
        if (CLIHeader != null)
        {
            return;
        }

        const uint ImageScnCntCode = 0x00000020;
        int rowSize = 8 + 2 * PointerSize;
        foreach (var sectionHeader in SectionTable.SectionHeaders)
        {
            int raw = sectionHeader.PointerToRawData.Value;
            int size = sectionHeader.SizeOfRawData.Value;
            if (raw <= 0 || size < 16 || (long)raw + size > Buffer.Length ||
                ((uint)sectionHeader.Characteristics.Value & ImageScnCntCode) != 0)
            {
                continue;
            }

            var bytes = Buffer.ReadBytes(raw, size);
            for (int i = 0; i + 16 <= bytes.Length; i += 4)
            {
                if (bytes[i] != 'R' || bytes[i + 1] != 'T' || bytes[i + 2] != 'R' || bytes[i + 3] != 0)
                {
                    continue;
                }

                int sectionCount = BitConverter.ToUInt16(bytes, i + 12);
                int entrySize = bytes[i + 14];
                int entryType = bytes[i + 15];
                int length = 16 + sectionCount * entrySize;
                if (entryType != 1 || entrySize != rowSize || sectionCount == 0 || sectionCount > 512 || i + length > bytes.Length)
                {
                    continue;
                }

                var header = new NativeAotModuleHeader { Start = raw + i, Length = length, PointerSize = PointerSize };
                if (TryAdd(header))
                {
                    NativeAotModuleHeaders.Add(header);
                }
            }
        }

        foreach (var header in NativeAotModuleHeaders)
        {
            foreach (var row in header.Rows)
            {
                AddNativeAotRegion(row);
            }
        }
    }

    private void AddNativeAotRegion(Record row)
    {
        int id = row.ReadInt32(0);
        int flags = row.ReadInt32(1);
        ulong start = ReadPointer(row.Children[2].Start);
        ulong end = ReadPointer(row.Children[3].Start);
        string name = ReadyToRunSection.GetSectionTypeName(id);
        int startRva = VirtualAddressToRva(start);
        const int HasEndPointer = 1;
        bool hasEnd = (flags & HasEndPointer) != 0 && end >= start;
        long size = hasEnd ? (long)(end - start) : PointerSize;
        row.Text = $"{name}: RVA 0x{startRva:X}, {size} bytes";

        int offset = startRva > 0 ? ResolveVirtualAddress(startRva) : 0;
        if (offset <= 0)
        {
            row.Text += " (no file data)";
            return;
        }

        if (size <= 0)
        {
            return;
        }

        var sectionHeader = GetSectionAtVirtualAddress(startRva);
        int rawEnd = sectionHeader.PointerToRawData.Value + sectionHeader.SizeOfRawData.Value;
        int length = (int)Math.Min(size, rawEnd - offset);
        string text = $"NativeAOT {name}";

        const int DehydratedDataSection = 207;
        if (id == DehydratedDataSection)
        {
            AddDehydratedData(offset, hasEnd ? length : 0, rawEnd);
            return;
        }

        TryAddRegion(new Region { Start = offset, Length = length, Text = text });
    }

    /// <summary>
    /// Older compilers give the end of the command stream in the module
    /// header row; newer ones store the length after the first four bytes.
    /// In both the table of relocation targets follows the commands.
    /// </summary>
    private void AddDehydratedData(int offset, int knownLength, int rawEnd)
    {
        bool hasLengthField = knownLength == 0;
        int headerSize = hasLengthField ? 8 : 4;
        int commandsEnd = hasLengthField ? offset + Buffer.ReadInt32(offset + 4) : offset + knownLength;
        if (commandsEnd <= offset + headerSize || commandsEnd > rawEnd)
        {
            return;
        }

        var commands = Buffer.ReadBytes(offset + headerSize, commandsEnd - offset - headerSize);
        int fixupCount = 0;
        foreach (var command in DehydratedData.Decode(commands))
        {
            if ((command.kind == 2 || command.kind == 3) && command.payload >= fixupCount)
            {
                fixupCount = command.payload + 1;
            }
        }

        int fixupsEnd = Math.Min(commandsEnd + fixupCount * 4, rawEnd);
        TryAdd(new DehydratedData
        {
            Start = offset,
            Length = fixupsEnd - offset,
            HeaderSize = headerSize,
            CommandsLength = commands.Length
        });
    }

    /// <summary>
    /// In a NativeAOT image the unwind info of a managed method is followed
    /// by a flags byte, optional RVAs of associated data and exception
    /// handling info, and for the main body of a method the GC info.
    /// </summary>
    private void AddNativeAotUnwindData(ExceptionTable table)
    {
        if (!IsNativeAot || table == null || Machine != MachineAmd64)
        {
            return;
        }

        const int MaximumBlobSize = 0x4000;
        foreach (var unwindInfo in table.PlainUnwindInfos)
        {
            int start = unwindInfo.End;
            int nextUnwindInfo = (start + 3) & ~3;
            if (table.UnwindOffsets.Contains(nextUnwindInfo) || table.UnwindOffsets.Contains(start))
            {
                continue;
            }

            int available = GetFreeLength(start, MaximumBlobSize);
            if (available < 2)
            {
                continue;
            }

            byte flags = Buffer.ReadByte(start);
            const int KindMask = 0x03;
            const int HasEHInfo = 0x04;
            const int HasAssociatedData = 0x10;
            if ((flags & 0xE0) != 0 || (flags & KindMask) == KindMask)
            {
                continue;
            }

            int headerSize = 1 + ((flags & HasAssociatedData) != 0 ? 4 : 0) + ((flags & HasEHInfo) != 0 ? 4 : 0);
            if (headerSize > available)
            {
                continue;
            }

            Add(new NativeAotUnwindData { Start = start, Length = available, Flags = flags });
        }
    }
}

/// <summary>
/// The module header the NativeAOT compiler emits: version, then one row
/// per runtime data region.
/// </summary>
public class NativeAotModuleHeader : Node
{
    public int PointerSize { get; set; }

    public override void Parse()
    {
        AddFourBytes("Signature (RTR)");
        var major = AddTwoBytes("Major version");
        var minor = AddTwoBytes("Minor version");
        AddFourBytes("Flags");
        var count = AddTwoBytes("Number of sections");
        AddOneByte("Entry size");
        AddOneByte("Entry type");

        var rows = new List<Record>();
        for (int i = 0; i < (ushort)count.Value; i++)
        {
            var row = new Record
            {
                Fields = [("Section id", 4), ("Flags", 4), ("Start", PointerSize), ("End", PointerSize)]
            };
            Add(row);
            rows.Add(row);
        }

        Rows = rows;
        Text = $"NativeAOT module header {major.Value}.{minor.Value}";
    }

    public IReadOnlyList<Record> Rows { get; set; }
}

/// <summary>
/// Data that the startup code expands into memory: a stream of copy, zero
/// fill and relocation commands, followed by the table of relocation targets.
/// </summary>
public class DehydratedData : Node
{
    public int HeaderSize { get; set; }
    public int CommandsLength { get; set; }

    /// <summary>
    /// Splits a command stream into commands. Kinds: 0 copy, 1 zero fill,
    /// 2 relative pointer, 3 pointer, 4 inline relative pointers, 5 inline
    /// pointers. The length includes the bytes a command carries.
    /// </summary>
    public static IEnumerable<(int offset, int length, int kind, int payload)> Decode(byte[] bytes)
    {
        const int MaxShortPayload = 28;
        int position = 0;
        while (position < bytes.Length)
        {
            int commandStart = position;
            byte b = bytes[position++];
            int kind = b & 7;
            int payload = b >> 3;
            int extraBytes = payload - MaxShortPayload;
            if (extraBytes > 0)
            {
                payload = 0;
                for (int i = 0; i < extraBytes && position < bytes.Length; i++)
                {
                    payload |= bytes[position++] << (8 * i);
                }

                payload += MaxShortPayload;
            }

            if (kind == 0)
            {
                position += payload;
            }
            else if (kind == 4 || kind == 5)
            {
                position += payload * 4;
            }
            else if (kind > 5)
            {
                position = bytes.Length;
            }

            if (position > bytes.Length || position < commandStart)
            {
                position = bytes.Length;
            }

            yield return (commandStart, position - commandStart, kind, payload);
        }
    }

    public override void Parse()
    {
        AddFourBytes("Hydrated data address (relative)");
        if (HeaderSize == 8)
        {
            AddFourBytes("Command stream end offset");
        }

        var commands = new Node { Start = LastChildEnd, Length = CommandsLength, Text = "Commands" };
        var bytes = Buffer.ReadBytes(commands.Start, commands.Length);
        Add(commands);

        const int pointerSize = 8;
        long copied = 0;
        long zeroFilled = 0;
        long relocations = 0;
        long hydrated = 0;
        foreach (var (offset, length, kind, payload) in Decode(bytes))
        {
            string text;
            switch (kind)
            {
                case 0:
                    copied += payload;
                    hydrated += payload;
                    text = $"Copy {payload} bytes";
                    break;
                case 1:
                    zeroFilled += payload;
                    hydrated += payload;
                    text = $"Zero fill {payload} bytes";
                    break;
                case 2:
                    relocations++;
                    hydrated += 4;
                    text = $"Relative pointer to fixup {payload}";
                    break;
                case 3:
                    relocations++;
                    hydrated += pointerSize;
                    text = $"Pointer to fixup {payload}";
                    break;
                case 4:
                    relocations += payload;
                    hydrated += payload * 4L;
                    text = $"{payload} inline relative pointers";
                    break;
                case 5:
                    relocations += payload;
                    hydrated += (long)payload * pointerSize;
                    text = $"{payload} inline pointers";
                    break;
                default:
                    text = $"Command {kind}";
                    break;
            }

            commands.Add(new Node { Start = commands.Start + offset, Length = length, Text = text });
        }

        int fixupCount = Remaining / 4;
        if (fixupCount > 0)
        {
            var fixups = new RecordTable
            {
                Start = LastChildEnd,
                Length = fixupCount * 4,
                Count = fixupCount,
                Fields = [("Relative pointer", 4)],
                RecordText = "Fixup"
            };
            Add(fixups);
            fixups.Text = $"Fixups ({fixupCount})";
        }

        if (Remaining > 0)
        {
            AddBytes(Remaining, "Trailing bytes");
        }

        HydratedSize = hydrated;
        Text = $"NativeAOT DehydratedData: {hydrated:N0} bytes in memory ({copied:N0} copied, {zeroFilled:N0} zero filled, {relocations:N0} relocations)";
    }

    public long HydratedSize { get; set; }
}

public class NativeAotUnwindData : Node
{
    public byte Flags { get; set; }

    public override void Parse()
    {
        const int HasEHInfo = 0x04;
        const int IsReversePInvoke = 0x08;
        const int HasAssociatedData = 0x10;

        string kind = (Flags & 3) switch
        {
            0 => "method body",
            1 => "handler funclet",
            _ => "filter funclet"
        };
        var flagNames = new List<string> { kind };
        if ((Flags & HasEHInfo) != 0)
        {
            flagNames.Add("EH info");
        }

        if ((Flags & IsReversePInvoke) != 0)
        {
            flagNames.Add("reverse P/Invoke");
        }

        if ((Flags & HasAssociatedData) != 0)
        {
            flagNames.Add("associated data");
        }

        AddOneByte($"Flags: {string.Join(", ", flagNames)}");
        if ((Flags & HasAssociatedData) != 0)
        {
            AddFourBytes("Associated data (relative)");
        }

        if ((Flags & HasEHInfo) != 0)
        {
            AddFourBytes("EH info RVA");
        }

        if (Remaining > 0)
        {
            AddBytes(Remaining, (Flags & 3) == 0 ? "GC info" : "Funclet data");
        }

        Text = $"Managed unwind data ({kind})";
    }
}
