using System.Linq;
using System.Text;

namespace GuiLabs.FileFormat.PE;

/// <summary>
/// Structures of the .NET runtime that a debugger finds by the name of an
/// export: the runtime exports the address of each one, and its layout is
/// fixed. The first bytes of a structure are checked before it is labelled.
/// </summary>
public partial class PEFile
{
    private void AddRuntimeExports()
    {
        foreach (var pair in ExportNamesByRva.ToArray())
        {
            int offset = ResolveVirtualAddress(pair.Key);
            if (offset <= 0)
            {
                continue;
            }

            switch (pair.Value)
            {
                case "g_CLREngineMetrics":
                    AddEngineMetrics(offset);
                    break;
                case "DotNetRuntimeInfo":
                    AddRuntimeInfo(offset);
                    break;
                case "DotNetRuntimeDebugHeader":
                    AddRuntimeDebugHeader(offset);
                    break;
                case "DotNetRuntimeContractDescriptor":
                    AddContractDescriptor(offset);
                    break;
                case "g_dacTable":
                    PointerTableNames[offset] = "Addresses of runtime globals for the debugger (g_dacTable)";
                    break;
            }
        }
    }

    private bool HasAscii(int offset, string text)
    {
        if (offset + text.Length > Buffer.Length)
        {
            return false;
        }

        var bytes = Buffer.ReadBytes(offset, text.Length);
        return Encoding.ASCII.GetString(bytes) == text;
    }

    /// <summary>
    /// CLR_ENGINE_METRICS: what a debugger reads first when it attaches.
    /// </summary>
    private void AddEngineMetrics(int offset)
    {
        int length = 8 + PointerSize;
        if (offset + length > Buffer.Length || Buffer.ReadInt32(offset) != length)
        {
            return;
        }

        TryAdd(new Record
        {
            Start = offset,
            Length = length,
            Fields = [("Size", 4), ("Version of the debugger interface", 4), ("Continue startup event", PointerSize)],
            Text = "CLR engine metrics (g_CLREngineMetrics)"
        });
    }

    /// <summary>
    /// RuntimeInfo: the version of the runtime and the identities of the
    /// runtime, DAC and DBI modules, for a debugger of a single-file app.
    /// </summary>
    private void AddRuntimeInfo(int offset)
    {
        const string Signature = "DotNetRuntimeInfo";
        const int SignatureLength = 18;
        const int ModuleIndexLength = 24;
        if (!HasAscii(offset, Signature) || offset + SignatureLength + 2 + 4 > Buffer.Length)
        {
            return;
        }

        // The version comes after the signature at a multiple of 4
        int version = Buffer.ReadInt32(offset + SignatureLength + 2);
        var fields = new System.Collections.Generic.List<(string name, int size)>
        {
            ("Signature", SignatureLength),
            ("Padding", 2),
            ("Version", 4),
            ("Runtime module index", ModuleIndexLength),
            ("DAC module index", ModuleIndexLength),
            ("DBI module index", ModuleIndexLength)
        };
        if (version >= 2)
        {
            fields.Add(("Runtime version", 16));
        }

        int length = fields.Sum(f => f.size);
        if (offset + length > Buffer.Length)
        {
            return;
        }

        TryAdd(new Record
        {
            Start = offset,
            Length = length,
            Fields = fields.ToArray(),
            Text = $"Runtime info, version {version} (DotNetRuntimeInfo)"
        });
    }

    /// <summary>
    /// The debug header of a NativeAOT runtime. Its two pointers are set
    /// when the app starts and are zero in the file.
    /// </summary>
    private void AddRuntimeDebugHeader(int offset)
    {
        if (!HasAscii(offset, "DNDH"))
        {
            return;
        }

        TryAdd(new Record
        {
            Start = offset,
            Length = 16 + 2 * PointerSize,
            Fields =
            [
                ("Cookie", 4),
                ("Major version", 2),
                ("Minor version", 2),
                ("Flags", 4),
                ("Reserved", 4),
                ("Debug type entries", PointerSize),
                ("Global entries", PointerSize)
            ],
            Text = "Runtime debug header (DotNetRuntimeDebugHeader)"
        });
    }

    /// <summary>
    /// The contract descriptor that the data access of newer runtimes
    /// starts from: a JSON text that describes the types and globals of
    /// the runtime, and a table of pointers that the text refers to.
    /// </summary>
    private void AddContractDescriptor(int offset)
    {
        const int MaximumTextLength = 0x100000;
        int pointerSize = PointerSize;
        int length = 16 + pointerSize + 8 + pointerSize;
        if (!HasAscii(offset, "DNCCDAC") || offset + length > Buffer.Length)
        {
            return;
        }

        int textLength = Buffer.ReadInt32(offset + 12);
        int text = ResolvePointerAt(offset + 16);
        int pointerData = ResolvePointerAt(offset + 16 + pointerSize + 8);
        if (!TryAdd(new Record
        {
            Start = offset,
            Length = length,
            Fields =
            [
                ("Magic", 8),
                ("Flags", 4),
                ("Descriptor length", 4),
                ("Descriptor", pointerSize),
                ("Pointer data count", 4),
                ("Padding", 4),
                ("Pointer data", pointerSize)
            ],
            Text = "Runtime contract descriptor (DotNetRuntimeContractDescriptor)"
        }))
        {
            return;
        }

        if (text > 0 && textLength > 0 && textLength <= MaximumTextLength && text + textLength <= Buffer.Length)
        {
            TryAdd(new Utf8String { Start = text, Length = textLength, Text = "Runtime contract descriptor, JSON text" });
        }

        if (pointerData > 0)
        {
            PointerTableNames[pointerData] = "Pointer data of the runtime contract descriptor";
        }
    }
}
