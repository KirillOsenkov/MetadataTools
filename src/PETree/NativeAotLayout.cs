using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using GuiLabs.FileFormat;
using GuiLabs.FileFormat.PE;

/// <summary>
/// Where the code of each method of a NativeAOT image is in the file. The
/// map file of the compiler (IlcGenerateMapFile) lists the methods with
/// their lengths and without addresses, in the order in which the code is
/// stored. The image has the start and the length of each function in its
/// exception table. A method is one function, or several that follow one
/// another: the body and the handlers of its try blocks. The methods are
/// matched to the functions in order: a method starts at a function start
/// and ends at a function end, exactly its length later.
/// </summary>
internal sealed class NativeAotLayout
{
    private const string MethodCodeKind = "MethodCode";

    /// <summary>
    /// Functions that may be between two methods and belong to no method.
    /// </summary>
    private const int MaximumSkippedFunctions = 200;

    private readonly List<(string name, int length)> methods = new();
    private readonly List<NativeCode> functions = new();

    /// <summary>
    /// For each method: the index of its first function and the number of
    /// its functions, or -1 when the method was not found.
    /// </summary>
    private int[] firstFunction;
    private int[] functionCount;

    public byte[] FileBytes { get; private set; }
    public string Message { get; private set; }
    public int MethodCount => methods.Count;
    public int FoundCount { get; private set; }

    public static NativeAotLayout Read(PEFile file, string mapPath)
    {
        var layout = new NativeAotLayout();
        layout.FileBytes = file.Buffer.ReadBytes(0, (int)file.Buffer.Length);
        using (var reader = XmlReader.Create(mapPath))
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && reader.Name == MethodCodeKind &&
                    int.TryParse(reader.GetAttribute("Length"), out int length))
                {
                    layout.methods.Add((reader.GetAttribute("Name") ?? "", length));
                }
            }
        }

        // The managed code is in the first section
        var section = file.Children.OfType<Section>().FirstOrDefault();
        if (section != null)
        {
            layout.Collect(section);
        }

        layout.functions.Sort((left, right) => left.Start.CompareTo(right.Start));
        layout.Match(section?.Start ?? 0);
        return layout;
    }

    private void Collect(Node node)
    {
        if (node is NativeCode code)
        {
            functions.Add(code);
            return;
        }

        if (node.HasChildren)
        {
            foreach (var child in node.Children)
            {
                Collect(child);
            }
        }
    }

    private void Match(int position)
    {
        firstFunction = new int[methods.Count];
        functionCount = new int[methods.Count];
        int index = 0;
        for (int i = 0; i < methods.Count; i++)
        {
            firstFunction[i] = -1;
            int length = methods[i].length;
            while (index < functions.Count && functions[index].Start < position)
            {
                index++;
            }

            for (int attempt = 0; attempt < MaximumSkippedFunctions && index + attempt < functions.Count; attempt++)
            {
                int start = functions[index + attempt].Start;
                int end = start;
                int next = index + attempt;
                while (next < functions.Count && functions[next].Start == end && end - start < length)
                {
                    end += functions[next].Length;
                    next++;
                }

                if (end - start == length)
                {
                    firstFunction[i] = index + attempt;
                    functionCount[i] = next - index - attempt;
                    position = end;
                    index = next;
                    FoundCount++;
                    break;
                }
            }
        }

        if (FoundCount == 0)
        {
            Message = $"Map file: none of its {methods.Count:N0} methods was found in the image. The map file is from another build.";
            return;
        }

        int firstFound = Array.FindIndex(firstFunction, f => f >= 0);
        int lastFound = Array.FindLastIndex(firstFunction, f => f >= 0);
        int from = functions[firstFunction[firstFound]].Start;
        var lastNode = functions[firstFunction[lastFound] + functionCount[lastFound] - 1];
        Message = $"Map file: {FoundCount:N0} of its {methods.Count:N0} methods were found in the image, in the order of the map file, at file offsets 0x{from:X} to 0x{lastNode.End:X}.";
    }

    /// <summary>
    /// Gives the file offset of the code to each method of another list of
    /// the same methods, such as the one of the mstat file. The two lists
    /// have to agree in the number of methods and in every length.
    /// </summary>
    public bool Assign(int count, Func<int, long> getLength, Func<int, string> getName, Action<int, int> setStart)
    {
        if (count != methods.Count)
        {
            Message += $" The mstat file has {count:N0} methods: the two files are not from the same build, and no file offsets are used.";
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (getLength(i) != methods[i].length)
            {
                Message += $" Method {i:N0} has a length of {getLength(i):N0} in the mstat file and of {methods[i].length:N0} in the map file: the two files are not from the same build, and no file offsets are used.";
                return false;
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (firstFunction[i] < 0)
            {
                continue;
            }

            setStart(i, functions[firstFunction[i]].Start);
            string name = getName(i);
            for (int part = 0; part < functionCount[i]; part++)
            {
                var node = functions[firstFunction[i] + part];
                node.Text = part == 0 ? $"Native code: {name}" : $"Native code: {name} (part {part + 1})";
            }
        }

        Message += " The mstat file lists the same methods with the same lengths.";
        return true;
    }
}
