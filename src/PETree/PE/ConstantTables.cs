using System;
using System.Collections.Generic;
using System.Globalization;

namespace GuiLabs.FileFormat.PE;

/// <summary>
/// Tables of constants in the data of an image that no structure refers
/// to by a known pointer: checksum tables, GUIDs, 8-byte floating point
/// numbers and addresses of code. They are recognized by their values. A rule
/// accepts a short table only when it fills the whole range between two
/// labelled nodes, and a table inside other bytes only when it is long
/// enough that such values are not there by chance.
/// </summary>
public partial class PEFile
{
    private const int GuidLength = 16;

    /// <summary>
    /// A table at the position of the byte array, or null. The array is a
    /// range of bytes without a label that starts at the file offset.
    /// </summary>
    private Node ReadConstantTable(byte[] bytes, int position, int start)
    {
        int remaining = bytes.Length - position;
        if (remaining < 8)
        {
            return null;
        }

        int offset = start + position;
        return ReadChecksumTable(bytes, position, offset)
            ?? ReadGuidTable(bytes, position, offset)
            ?? ReadDoubleTable(bytes, position, offset)
            ?? ReadCodeAddressTable(bytes, position, offset);
    }

    private sealed class ChecksumTableInfo
    {
        public string Text;
        public uint[] Entries;
    }

    private static Dictionary<uint, ChecksumTableInfo> checksumTables;

    /// <summary>
    /// The tables of the two common 32-bit cyclic redundancy checks, by the
    /// second entry of each (the first entry is zero). A fast implementation
    /// has several tables: table n gives the checksum of a byte that is
    /// followed by n zero bytes.
    /// </summary>
    private static Dictionary<uint, ChecksumTableInfo> GetChecksumTables()
    {
        if (checksumTables != null)
        {
            return checksumTables;
        }

        const int SliceCount = 16;
        var result = new Dictionary<uint, ChecksumTableInfo>();
        foreach (var (name, polynomial) in new[] { ("CRC-32", 0xEDB88320u), ("CRC-32C", 0x82F63B78u) })
        {
            var first = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint value = n;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? (value >> 1) ^ polynomial : value >> 1;
                }

                first[n] = value;
            }

            var previous = first;
            for (int slice = 0; slice < SliceCount; slice++)
            {
                var entries = previous;
                if (slice > 0)
                {
                    entries = new uint[256];
                    for (int n = 0; n < 256; n++)
                    {
                        entries[n] = (previous[n] >> 8) ^ first[previous[n] & 0xFF];
                    }
                }

                string text = slice == 0 ? $"{name} table" : $"{name} table {slice + 1} (for a byte followed by {slice} bytes)";
                result[entries[1]] = new ChecksumTableInfo { Text = text, Entries = entries };
                previous = entries;
            }
        }

        checksumTables = result;
        return result;
    }

    /// <summary>
    /// The entries 1 to 255 of a checksum table. The zero entry before them
    /// is labelled as padding by then; the caller takes it back.
    /// </summary>
    private static Node ReadChecksumTable(byte[] bytes, int position, int offset)
    {
        const int EntryCount = 255;
        if (bytes.Length - position < EntryCount * 4)
        {
            return null;
        }

        if (!GetChecksumTables().TryGetValue(BitConverter.ToUInt32(bytes, position), out var table))
        {
            return null;
        }

        for (int n = 1; n <= EntryCount; n++)
        {
            if (BitConverter.ToUInt32(bytes, position + (n - 1) * 4) != table.Entries[n])
            {
                return null;
            }
        }

        return new ChecksumTable { Start = offset, Length = EntryCount * 4, Text = table.Text };
    }

    private static bool IsPrintable(byte[] bytes, int position, int length)
    {
        for (int i = position; i < position + length; i++)
        {
            if (bytes[i] < 0x20 || bytes[i] > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when one byte value is a third of the bytes or more: a table of
    /// small numbers has such a byte, and the random or time-based bytes of
    /// a GUID do not.
    /// </summary>
    private static bool HasRepeatedByte(byte[] bytes, int position, int length)
    {
        int limit = length / 3;
        for (int i = position; i < position + length; i++)
        {
            int count = 0;
            for (int j = position; j < position + length; j++)
            {
                count += bytes[j] == bytes[i] ? 1 : 0;
            }

            if (count >= limit)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// GUIDs, by the version and variant bits that every GUID made by the
    /// usual algorithms has. One GUID or a few are accepted when they are
    /// the whole range; a table inside other bytes needs three GUIDs of
    /// the two versions that are in use, time-based and random.
    /// </summary>
    private static Node ReadGuidTable(byte[] bytes, int position, int offset)
    {
        const int MinimumCountInsideOtherBytes = 3;
        int count = 0;
        bool areCommonVersions = true;
        int p = position;
        while (p + GuidLength <= bytes.Length)
        {
            int version = bytes[p + 7] >> 4;
            bool hasVariant = (bytes[p + 8] & 0xC0) == 0x80;
            if (version < 1 || version > 5 || !hasVariant || BitConverter.ToUInt32(bytes, p) == 0 ||
                IsPrintable(bytes, p, GuidLength) || HasRepeatedByte(bytes, p, GuidLength))
            {
                break;
            }

            areCommonVersions &= version == 1 || version == 4;
            count++;
            p += GuidLength;
        }

        bool isWholeRange = position == 0 && p == bytes.Length;
        if (count == 0 || (!isWholeRange && (count < MinimumCountInsideOtherBytes || !areCommonVersions)))
        {
            return null;
        }

        return new GuidTable { Start = offset, Length = count * GuidLength };
    }

    private static bool IsDouble(ulong value)
    {
        // Between 2^-80 and 2^80: the range of the constants of numeric code
        const int Bias = 1023;
        const int Range = 80;
        int exponent = (int)((value >> 52) & 0x7FF);
        return exponent > Bias - Range && exponent < Bias + Range;
    }

    /// <summary>
    /// True when the lower 4 bytes are not zero and two or more of them
    /// are. The end of the fraction of a number is all zero for a number
    /// such as 0.5 or 100 and has no zero bytes for most others; a few zero
    /// bytes among others are what a pair of small integers has.
    /// </summary>
    private static bool HasSparseFraction(byte[] bytes, int position)
    {
        int zeroCount = 0;
        for (int i = position; i < position + 4; i++)
        {
            zeroCount += bytes[i] == 0 ? 1 : 0;
        }

        return zeroCount >= 2 && zeroCount < 4;
    }

    /// <summary>
    /// Floating point numbers of 8 bytes at an address that is a multiple
    /// of 8. Six numbers are needed inside other bytes, two when they are
    /// the whole range, and one when its lower half is zero, as it is for
    /// a number such as 0.5 or 100.
    /// </summary>
    private static Node ReadDoubleTable(byte[] bytes, int position, int offset)
    {
        const int MinimumCountInsideOtherBytes = 6;
        if ((offset & 7) != 0)
        {
            return null;
        }

        int count = 0;
        int singlePairs = 0;
        int end = position;
        int p = position;
        while (p + 8 <= bytes.Length)
        {
            ulong value = BitConverter.ToUInt64(bytes, p);
            bool isZero = (value << 1) == 0;
            if (IsDouble(value) && !IsPrintable(bytes, p, 8) && !HasSparseFraction(bytes, p))
            {
                count++;
                singlePairs += IsSingle((uint)value) ? 1 : 0;
                end = p + 8;
            }
            else if (!isZero || count == 0)
            {
                break;
            }

            // A zero between numbers is a number of the table too
            p += 8;
        }

        if (count == 0)
        {
            return null;
        }

        bool isWholeRange = position == 0 && end == bytes.Length;
        bool isRoundNumber = count == 1 && BitConverter.ToUInt32(bytes, position) == 0;
        if (count < MinimumCountInsideOtherBytes && !(isWholeRange && (count >= 2 || isRoundNumber)))
        {
            return null;
        }

        // Two values of 4 bytes read together can be in the range of the
        // 8-byte numbers: status codes that start with 0xC0, pairs of
        // 16-bit numbers, 4-byte floating point numbers. Their lower halves
        // are then in the range of a 4-byte floating point number. The
        // lower half of an 8-byte number is the end of its fraction and is
        // in that range only by chance, so a table where every lower half
        // is in it is not taken for 8-byte numbers.
        if (singlePairs == count)
        {
            return null;
        }

        return new DoubleTable { Start = offset, Length = end - position };
    }

    private static bool IsSingle(uint value)
    {
        // Between 2^-40 and 2^40
        const int Bias = 127;
        const int Range = 40;
        int exponent = (int)((value >> 23) & 0xFF);
        return exponent > Bias - Range && exponent < Bias + Range;
    }

    private List<(int start, int end)> codeAddressRanges;

    private bool IsCodeRva(int rva)
    {
        if (codeAddressRanges == null)
        {
            const uint ImageScnCntCode = 0x00000020;
            const uint ImageScnMemExecute = 0x20000000;
            codeAddressRanges = new List<(int start, int end)>();
            if (SectionTable != null)
            {
                foreach (var section in SectionTable.SectionHeaders)
                {
                    if (((uint)section.Characteristics.Value & (ImageScnCntCode | ImageScnMemExecute)) != 0)
                    {
                        int sectionStart = section.VirtualAddress.Value;
                        codeAddressRanges.Add((sectionStart, sectionStart + Math.Min(section.VirtualSize.Value, section.SizeOfRawData.Value)));
                    }
                }
            }
        }

        foreach (var (rangeStart, rangeEnd) in codeAddressRanges)
        {
            if (rva >= rangeStart && rva < rangeEnd)
            {
                return true;
            }
        }

        return false;
    }

    private int[] functionStarts;
    private int[] functionEnds;

    /// <summary>
    /// The index of the function of the exception table that has the file
    /// offset, or -1.
    /// </summary>
    private int FindFunction(int offset)
    {
        if (functionStarts == null)
        {
            var starts = new List<int>();
            var ends = new List<int>();
            void Collect(Node node)
            {
                if (node is NativeCode)
                {
                    starts.Add(node.Start);
                    ends.Add(node.End);
                }
                else if (node.HasChildren && (node is Section || node is Region || node == this))
                {
                    foreach (var child in node.Children)
                    {
                        Collect(child);
                    }
                }
            }

            Collect(this);
            functionStarts = starts.ToArray();
            functionEnds = ends.ToArray();
            Array.Sort(functionStarts, functionEnds);
        }

        int index = Array.BinarySearch(functionStarts, offset);
        if (index < 0)
        {
            index = ~index - 1;
        }

        return index >= 0 && offset < functionEnds[index] ? index : -1;
    }

    /// <summary>
    /// 32-bit relative virtual addresses of places in one function, as the
    /// targets of a switch statement are. Other numbers can be in the range
    /// of the code by chance, so the targets have to be inside one function
    /// of the exception table, at least 3 bytes apart, as instructions
    /// are and the numbers of a list of identifiers are not, and not all
    /// at the same place modulo 16, as the entries of a table of bit
    /// fields are.
    /// </summary>
    private Node ReadCodeAddressTable(byte[] bytes, int position, int offset)
    {
        const int MinimumCount = 4;
        const int MinimumDistinctCount = 3;
        const int MinimumSpacing = 3;
        if (ExceptionTable == null || Machine == MachineI386)
        {
            return null;
        }

        var targets = new List<int>();
        int function = -1;
        int p = position;
        while (p + 4 <= bytes.Length)
        {
            int value = BitConverter.ToInt32(bytes, p);
            if (value <= 0 || !IsCodeRva(value))
            {
                break;
            }

            int target = ResolveVirtualAddress(value);
            int index = target > 0 ? FindFunction(target) : -1;
            if (index < 0 || (function >= 0 && index != function) || target == functionStarts[index])
            {
                break;
            }

            function = index;
            targets.Add(value);
            p += 4;
        }

        if (targets.Count < MinimumCount)
        {
            return null;
        }

        var distinct = new List<int>(targets);
        distinct.Sort();
        int distinctCount = 1;
        bool haveSamePlace = true;
        for (int i = 1; i < distinct.Count; i++)
        {
            int difference = distinct[i] - distinct[i - 1];
            if (difference == 0)
            {
                continue;
            }

            if (difference < MinimumSpacing)
            {
                return null;
            }

            distinctCount++;
            haveSamePlace &= (difference & 0xF) == 0;
        }

        if (distinctCount < MinimumDistinctCount || haveSamePlace)
        {
            return null;
        }

        return new CodeAddressTable { Start = offset, Length = targets.Count * 4 };
    }
}

/// <summary>
/// A lookup table of a checksum algorithm.
/// </summary>
public class ChecksumTable : Node
{
}

public class GuidTable : Node
{
    public override void Parse()
    {
        const int GuidLength = 16;
        int count = Length / GuidLength;
        string first = null;
        for (int i = 0; i < count; i++)
        {
            var guid = new Guid(Buffer.ReadBytes(Start + i * GuidLength, GuidLength));
            string text = guid.ToString("B").ToUpperInvariant();
            first ??= text;
            AddBytes(GuidLength, $"GUID {text}");
        }

        Text = count == 1 ? $"GUID {first}" : $"GUIDs ({count})";
    }
}

public class DoubleTable : Node
{
    public override void Parse()
    {
        int count = Length / 8;
        string first = null;
        for (int i = 0; i < count; i++)
        {
            var entry = AddEightBytes();
            double value = BitConverter.Int64BitsToDouble((long)Buffer.ReadUInt64(entry.Start));
            string text = value.ToString("R", CultureInfo.InvariantCulture);
            first ??= text;
            entry.Text = $"Double {text}";
        }

        Text = count == 1 ? $"Double {first}" : $"Doubles ({count})";
    }
}

public class CodeAddressTable : Node
{
    public override void Parse()
    {
        int count = Length / 4;
        for (int i = 0; i < count; i++)
        {
            var entry = AddFourBytes();
            entry.Text = $"RVA 0x{Buffer.ReadUInt32(entry.Start):X}";
        }

        Text = $"RVAs of code ({count})";
    }
}
