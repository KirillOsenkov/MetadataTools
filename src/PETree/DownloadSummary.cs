using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// The files that a browser app publishes, with the length of each one and
/// of its Brotli-compressed copy, which is what a browser downloads. The
/// list is read from the manifest that the build writes into the obj
/// folder (staticwebassets.publish.json): every published file is an asset
/// there, and a compressed copy is an alternative asset of the file.
/// </summary>
public static class DownloadSummary
{
    private sealed class Asset
    {
        public string Path;
        public long Length;
        public long Compressed = -1;
    }

    public static void Print(string manifestPath, int rowCount, TextWriter writer)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        if (!document.RootElement.TryGetProperty("Assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            writer.WriteLine("The file has no list of assets. The expected file is staticwebassets.publish.json from the obj folder.");
            return;
        }

        string Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";

        long Length(JsonElement element)
        {
            if (element.TryGetProperty("FileLength", out var value) && value.TryGetInt64(out long length) && length >= 0)
            {
                return length;
            }

            string identity = Text(element, "Identity");
            return File.Exists(identity) ? new FileInfo(identity).Length : 0;
        }

        var byIdentity = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        var alternatives = new List<(string related, long length)>();
        foreach (var element in assets.EnumerateArray())
        {
            string role = Text(element, "AssetRole");
            if (role == "Primary")
            {
                // The path has a placeholder where the name gets a hash
                string path = Regex.Replace(Text(element, "RelativePath"), @"#\[[^\]]*\][?!]?", "");
                byIdentity[Text(element, "Identity")] = new Asset { Path = path, Length = Length(element) };
            }
            else if (role == "Alternative" && Text(element, "AssetTraitName") == "Content-Encoding" && Text(element, "AssetTraitValue") == "br")
            {
                alternatives.Add((Text(element, "RelatedAsset"), Length(element)));
            }
        }

        foreach (var (related, length) in alternatives)
        {
            if (byIdentity.TryGetValue(related, out var asset))
            {
                asset.Compressed = length;
            }
        }

        var all = byIdentity.Values.ToArray();
        if (all.Length == 0)
        {
            writer.WriteLine("The manifest lists no files.");
            return;
        }

        // A file without a compressed copy is downloaded as it is
        long Download(Asset asset) => asset.Compressed >= 0 ? asset.Compressed : asset.Length;

        long totalLength = all.Sum(a => a.Length);
        long totalDownload = all.Sum(Download);
        writer.WriteLine($"{all.Length:N0} published files, {totalLength:N0} bytes, {totalDownload:N0} bytes with Brotli.");
        writer.WriteLine("All published files are listed. Which of them a browser downloads at startup depends on the app.");
        writer.WriteLine();
        writer.WriteLine($"{"files",7} {"bytes",13} {"Brotli",12} {"share",7}  Group");
        foreach (var group in all.GroupBy(GetGroup).OrderByDescending(g => g.Sum(Download)))
        {
            long download = group.Sum(Download);
            writer.WriteLine($"{group.Count(),7:N0} {group.Sum(a => a.Length),13:N0} {download,12:N0} {SizeTables.Percent(download, totalDownload),7}  {group.Key}");
        }

        writer.WriteLine();
        writer.WriteLine("Largest files");
        writer.WriteLine($"{"bytes",13} {"Brotli",12} {"share",7}  File");
        foreach (var asset in all.OrderByDescending(Download).Take(rowCount))
        {
            string compressed = asset.Compressed >= 0 ? asset.Compressed.ToString("N0") : "(none)";
            writer.WriteLine($"{asset.Length,13:N0} {compressed,12} {SizeTables.Percent(Download(asset), totalDownload),7}  {asset.Path}");
        }
    }

    /// <summary>
    /// The files of the runtime are grouped by what they are, the other
    /// files by their folder and extension.
    /// </summary>
    private static string GetGroup(Asset asset)
    {
        string path = asset.Path.Replace('\\', '/');
        string name = path.Substring(path.LastIndexOf('/') + 1);
        string extension = Path.GetExtension(name).ToLowerInvariant();
        int slash = path.IndexOf('/');
        string folder = slash > 0 ? path.Substring(0, slash) : "";
        if (folder == "_framework")
        {
            if (name.StartsWith("dotnet.native", StringComparison.OrdinalIgnoreCase) && extension == ".wasm")
            {
                return "_framework: native module (runtime, native libraries, AOT code)";
            }

            switch (extension)
            {
                case ".wasm":
                case ".dll":
                    return "_framework: assemblies";
                case ".dat":
                    return "_framework: ICU data";
                case ".js":
                    return "_framework: scripts";
                case ".pdb":
                    return "_framework: debug symbols";
                default:
                    return $"_framework: other ({extension})";
            }
        }

        return folder.Length > 0 ? $"{folder}/ ({extension})" : $"root folder ({extension})";
    }
}
