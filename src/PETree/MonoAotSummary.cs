using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using GuiLabs.FileFormat;
using GuiLabs.FileFormat.Wasm;

/// <summary>
/// For a WebAssembly module with code from Mono's ahead-of-time compiler:
/// the bytes of the module per assembly, namespace, type and method. The
/// functions of an assembly are the ones that its method table lists; the
/// data are the tables that its file info points to.
/// </summary>
public static class MonoAotSummary
{
    private const int DataSection = 11;
    private const int CodeSection = 10;

    /// <summary>
    /// Prints the report. With a Brotli quality of 0 to 11, the code and
    /// the data of each assembly are also compressed, each one separately,
    /// and the compressed lengths are printed.
    /// </summary>
    public static void Print(Node root, MonoAotNames names, int rowCount, int brotliQuality, TextWriter writer)
    {
        if (root is not WasmFile file || file.MonoAotModules.Count == 0)
        {
            writer.WriteLine("The file is not a WebAssembly module with Mono AOT data.");
            return;
        }

        string[] blobKinds = ["blob"];
        string[] methodInfoKinds = ["method info"];
        string[] extraMethodKinds = ["extra method table", "extra method info offsets"];

        long Sum(MonoAotModule module, string[] kinds) => kinds.Sum(k => module.DataLengths.TryGetValue(k, out long bytes) ? bytes : 0);

        var rows = file.MonoAotModules
            .Select(m =>
            {
                long data = m.DataLengths.Values.Sum();
                long blob = Sum(m, blobKinds);
                long methodInfo = Sum(m, methodInfoKinds);
                long extraMethods = Sum(m, extraMethodKinds);
                return (module: m, blob, methodInfo, extraMethods, other: data - blob - methodInfo - extraMethods, total: m.CodeLength + data);
            })
            .OrderByDescending(r => r.total)
            .ToArray();

        var functionsByModule = file.Functions.Where(f => f.Owner != null).ToLookup(f => f.Owner);
        var unowned = file.Functions.Where(f => f.Owner == null).ToArray();

        // Compressed lengths: code and data of each assembly, and the code
        // that is in no method table
        bool isCompressed = brotliQuality >= 0;
        var compressedCode = new long[rows.Length];
        var compressedData = new long[rows.Length];
        long compressedUnowned = 0;
        long compressedRest = 0;
        if (isCompressed)
        {
            // What is left of the file: the data that belongs to no
            // assembly, and the sections other than code and data
            var used = file.Functions.Select(f => (f.Start, f.Length))
                .Concat(file.MonoAotModules.SelectMany(m => m.DataRanges))
                .OrderBy(r => r.Item1)
                .ToArray();
            var rest = new List<(int start, int length)>();
            int position = 0;
            foreach (var (start, length) in used)
            {
                if (start > position)
                {
                    rest.Add((position, start - position));
                }

                position = Math.Max(position, start + length);
            }

            if (position < file.Bytes.Length)
            {
                rest.Add((position, file.Bytes.Length - position));
            }

            Parallel.For(0, rows.Length * 2 + 2, i =>
            {
                if (i == rows.Length * 2 + 1)
                {
                    compressedRest = Compress(file.Bytes, rest, brotliQuality);
                }
                else if (i == rows.Length * 2)
                {
                    compressedUnowned = Compress(file.Bytes, unowned.Select(f => (f.Start, f.Length)), brotliQuality);
                }
                else if (i % 2 == 0)
                {
                    compressedCode[i / 2] = Compress(file.Bytes, functionsByModule[rows[i / 2].module].Select(f => (f.Start, f.Length)), brotliQuality);
                }
                else
                {
                    compressedData[i / 2] = Compress(file.Bytes, rows[i / 2].module.DataRanges, brotliQuality);
                }
            });
        }

        string compressedHeader = isCompressed ? $" {"code br",10} {"data br",10} {"total br",10}" : "";
        writer.WriteLine($"{"Assembly",-42} {"functions",9} {"code",11} {"blob",10} {"method info",11} {"extra methods",13} {"other tables",12} {"total",11}{compressedHeader}");
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            string compressed = isCompressed ? $" {compressedCode[i],10:N0} {compressedData[i],10:N0} {compressedCode[i] + compressedData[i],10:N0}" : "";
            writer.WriteLine($"{row.module.Name,-42} {row.module.FunctionCount,9:N0} {row.module.CodeLength,11:N0} {row.blob,10:N0} {row.methodInfo,11:N0} {row.extraMethods,13:N0} {row.other,12:N0} {row.total,11:N0}{compressed}");
        }

        long functions = rows.Sum(r => (long)r.module.FunctionCount);
        long code = rows.Sum(r => r.module.CodeLength);
        long total = rows.Sum(r => r.total);
        string compressedTotals = isCompressed ? $" {compressedCode.Sum(),10:N0} {compressedData.Sum(),10:N0} {compressedCode.Sum() + compressedData.Sum(),10:N0}" : "";
        writer.WriteLine($"{"All assemblies",-42} {functions,9:N0} {code,11:N0} {rows.Sum(r => r.blob),10:N0} {rows.Sum(r => r.methodInfo),11:N0} {rows.Sum(r => r.extraMethods),13:N0} {rows.Sum(r => r.other),12:N0} {total,11:N0}{compressedTotals}");

        long unownedCode = unowned.Sum(f => (long)f.Length);
        long dataSection = file.Sections.Where(s => s.Id == DataSection).Sum(s => (long)s.Length);
        long codeSection = file.Sections.Where(s => s.Id == CodeSection).Sum(s => (long)s.Length);
        long assemblyData = total - code;
        long fileLength = file.Buffer.Length;

        writer.WriteLine();
        writer.WriteLine($"File:                              {fileLength,12:N0} bytes");
        writer.WriteLine($"Code section:                      {codeSection,12:N0}");
        writer.WriteLine($"  functions of the assemblies:     {code,12:N0}  ({functions:N0} functions)");
        writer.WriteLine($"  functions in no method table:    {unownedCode,12:N0}  ({unowned.Length:N0} functions: the runtime, native libraries, helpers)");
        writer.WriteLine($"Data section:                      {dataSection,12:N0}");
        writer.WriteLine($"  data of the assemblies:          {assemblyData,12:N0}");
        writer.WriteLine($"  other data:                      {dataSection - assemblyData,12:N0}  (includes the headers of {file.DataSegments.Count:N0} data segments)");
        writer.WriteLine($"Other sections:                    {fileLength - codeSection - dataSection,12:N0}");

        if (isCompressed)
        {
            long restLength = fileLength - code - assemblyData - unownedCode;
            long parts = compressedCode.Sum() + compressedData.Sum() + compressedUnowned + compressedRest;
            writer.WriteLine();
            writer.WriteLine($"Brotli, quality {brotliQuality}. Each \"br\" number is the length of that part compressed alone.");
            writer.WriteLine($"  code of the assemblies:          {compressedCode.Sum(),12:N0}  ({Percent(compressedCode.Sum(), code)} of {code:N0})");
            writer.WriteLine($"  data of the assemblies:          {compressedData.Sum(),12:N0}  ({Percent(compressedData.Sum(), assemblyData)} of {assemblyData:N0})");
            writer.WriteLine($"  functions in no method table:    {compressedUnowned,12:N0}  ({Percent(compressedUnowned, unownedCode)} of {unownedCode:N0})");
            writer.WriteLine($"  the rest of the file:            {compressedRest,12:N0}  ({Percent(compressedRest, restLength)} of {restLength:N0})");
            writer.WriteLine($"  all parts together:              {parts,12:N0}  ({Percent(parts, fileLength)} of the file; the file compressed in one piece is somewhat smaller)");
        }

        if (names == null)
        {
            return;
        }

        if (names.Messages.Count > 0)
        {
            writer.WriteLine();
            foreach (string message in names.Messages)
            {
                writer.WriteLine(message);
            }
        }

        var methods = file.Functions
            .Where(f => f.Owner != null && names.Methods.ContainsKey(f))
            .Select(f => (function: f, method: names.Methods[f]))
            .ToArray();

        writer.WriteLine();
        writer.WriteLine(SizeTables.Legend1);
        writer.WriteLine(SizeTables.Legend2 + " A generic instance or a wrapper is a method without a row in the MethodDef table.");
        if (isCompressed)
        {
            writer.WriteLine(SizeTables.CompressedLegend);
        }

        var bytes = isCompressed ? file.Bytes : null;
        void Groups(string title, string keyHeader, (WasmFunction function, MonoAotMethod method)[] items, Func<(WasmFunction function, MonoAotMethod method), string> getKey, int count)
        {
            SizeTables.PrintGroups(
                writer, title, keyHeader, items, getKey,
                m => m.function.Length,
                m => !m.method.IsFromMetadata,
                m => m.method.Instantiation,
                count, code, "of code", bytes,
                m => (m.function.Start, m.function.Length),
                brotliQuality);
        }

        Groups("Code by kind of method", "Kind", methods, m => m.method.Kind, int.MaxValue);
        Groups("Largest namespaces", "Namespace", methods, m => GroupNamespace(m.method), rowCount);
        Groups("Largest types", "Type (assembly that declares it)", methods, m => $"{m.method.TypeName}  ({m.method.Assembly})", rowCount);
        Groups(
            "Types with the most code in generic instances and wrappers",
            "Type (assembly that declares it)",
            methods.Where(m => !m.method.IsFromMetadata && m.method.Kind != MonoAotMethod.KindHelper).ToArray(),
            m => $"{m.method.TypeName}  ({m.method.Assembly})",
            rowCount);

        writer.WriteLine();
        writer.WriteLine("Largest methods");
        writer.WriteLine($"{"bytes",10}  Method (assembly that the code is stored with, when it is not the one that declares the type)");
        foreach (var (function, method) in methods.OrderByDescending(m => m.function.Length).Take(rowCount))
        {
            string stored = function.Owner.Name != method.Assembly ? $"  ({function.Owner.Name})" : "";
            writer.WriteLine($"{function.Length,10:N0}  {method.FullName}{stored}");
        }

        writer.WriteLine();
        writer.WriteLine("Largest functions in no method table");
        foreach (var function in unowned.OrderByDescending(f => f.Length).Take(Math.Min(rowCount, 15)))
        {
            writer.WriteLine($"{function.Length,10:N0}  {function.Text}");
        }
    }

    /// <summary>
    /// One line per function of the assemblies, with tabs between the columns.
    /// </summary>
    public static void WriteFunctionList(Node root, MonoAotNames names, string path)
    {
        if (root is not WasmFile file || names == null)
        {
            return;
        }

        using var writer = new StreamWriter(path);
        writer.WriteLine("Bytes\tStored with\tKind\tDeclaring assembly\tNamespace\tType\tType arguments\tMethod\tUnbox trampoline\tMethod index\tFunction index\tSymbol");
        foreach (var function in file.Functions)
        {
            if (function.Owner == null || !names.Methods.TryGetValue(function, out var method))
            {
                continue;
            }

            writer.WriteLine(string.Join("\t",
                function.Length,
                function.Owner.Name,
                method.Kind,
                method.Assembly,
                method.Namespace,
                method.Type,
                method.Instantiation,
                method.Method,
                method.IsUnboxTrampoline ? "yes" : "",
                function.MethodIndex,
                function.Index,
                method.Symbol));
        }
    }

    private static string Percent(long part, long whole) => whole > 0 ? $"{100.0 * part / whole:F1}%" : "";

    /// <summary>
    /// A wrapper that is not for a method of a type has a text in
    /// parentheses in place of the type, and that text is the group.
    /// </summary>
    private static string GroupNamespace(MonoAotMethod method)
    {
        if (method.Type.StartsWith("("))
        {
            return method.Type;
        }

        return method.Namespace.Length > 0 ? method.Namespace : "(no namespace)";
    }

    private static long Compress(byte[] bytes, IEnumerable<(int start, int length)> ranges, int quality) => SizeTables.Compress(bytes, ranges, quality);
}
