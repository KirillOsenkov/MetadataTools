using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// The tables that the size reports for Mono AOT modules and NativeAOT
/// images share: the largest groups of a list of items, and the length of
/// byte ranges after Brotli compression.
/// </summary>
internal static class SizeTables
{
    public const string Legend1 = "In the tables below, \"instances\" are the bytes of generic instances and wrappers, and \"type args\" is the number";
    public const string Legend2 = "of different lists of type arguments among them.";
    public const string CompressedLegend = "\"br\" is the length of the bytes of the row compressed alone.";

    public static string Percent(long part, long whole) => whole > 0 ? $"{100.0 * part / whole:F1}%" : "";

    /// <summary>
    /// Prints the groups with the most bytes. With the bytes of the file,
    /// the ranges of each printed group are also compressed.
    /// </summary>
    public static void PrintGroups<T>(
        TextWriter writer,
        string title,
        string keyHeader,
        IReadOnlyList<T> items,
        Func<T, string> getKey,
        Func<T, long> getBytes,
        Func<T, bool> isInstance,
        Func<T, string> getInstantiation,
        int rowCount,
        long allBytes,
        string shareHeader,
        byte[] fileBytes = null,
        Func<T, (int start, int length)> getRange = null,
        int brotliQuality = -1)
    {
        var groups = items
            .GroupBy(getKey)
            .Select(g => (
                key: g.Key,
                bytes: g.Sum(getBytes),
                count: g.Count(),
                instanceBytes: g.Where(isInstance).Sum(getBytes),
                instantiations: g.Select(getInstantiation).Where(i => i != null).Distinct().Count(),
                items: g.ToArray()))
            .OrderByDescending(g => g.bytes)
            .ToArray();

        bool isCompressed = fileBytes != null && getRange != null && brotliQuality >= 0;
        int shown = Math.Min(rowCount, groups.Length);
        var compressed = new long[shown];
        if (isCompressed)
        {
            Parallel.For(0, shown, i => compressed[i] = Compress(fileBytes, groups[i].items.Select(getRange), brotliQuality));
        }

        string compressedHeader = isCompressed ? $" {"br",10}" : "";
        writer.WriteLine();
        writer.WriteLine(title);
        writer.WriteLine($"{"bytes",10} {shareHeader,7}{compressedHeader} {"count",9} {"instances",10} {"type args",9}  {keyHeader}");
        for (int i = 0; i < shown; i++)
        {
            var group = groups[i];
            string compressedText = isCompressed ? $" {compressed[i],10:N0}" : "";
            writer.WriteLine($"{group.bytes,10:N0} {Percent(group.bytes, allBytes),7}{compressedText} {group.count,9:N0} {group.instanceBytes,10:N0} {group.instantiations,9:N0}  {group.key}");
        }

        if (groups.Length > shown)
        {
            string compressedText = isCompressed ? $" {"",10}" : "";
            writer.WriteLine($"{groups.Skip(shown).Sum(g => g.bytes),10:N0} {"",7}{compressedText} {groups.Skip(shown).Sum(g => (long)g.count),9:N0} {"",10} {"",9}  ({groups.Length - shown:N0} more)");
        }
    }

    /// <summary>
    /// The length of the ranges of the file, put one after another, after
    /// Brotli compression. A range with a length of zero is left out.
    /// </summary>
    public static long Compress(byte[] bytes, IEnumerable<(int start, int length)> ranges, int quality)
    {
        const int Window = 24;
        var list = ranges.Where(r => r.length > 0).ToArray();
        long total = list.Sum(r => (long)r.length);
        if (total == 0 || total > int.MaxValue / 2)
        {
            return 0;
        }

        var source = new byte[total];
        int position = 0;
        foreach (var (start, length) in list)
        {
            Buffer.BlockCopy(bytes, start, source, position, length);
            position += length;
        }

        var destination = new byte[BrotliEncoder.GetMaxCompressedLength(source.Length)];
        return BrotliEncoder.TryCompress(source, destination, out int written, quality, Window) ? written : 0;
    }
}
