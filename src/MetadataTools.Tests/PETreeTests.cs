using System;
using System.IO;
using System.Linq;
using System.Text;
using GuiLabs.FileFormat;
using GuiLabs.FileFormat.PE;
using GuiLabs.FileFormat.PE.Dotnet;
using ListDiff;
using Xunit;

namespace MetadataTools.Tests;

public class PETreeTests
{
    [Theory]
    [InlineData("")]
    [InlineData("VS_VERSION_INFO")]
    [InlineData("A\u1234\uD83D\uDE00")]
    public void Utf16StringPreservesBothBytes(string value)
    {
        using var stream = new MemoryStream(Encoding.Unicode.GetBytes(value + "\0"));
        var node = new ZeroTerminatedUtf16String { Buffer = new StreamBuffer(stream) };

        node.Parse();

        Assert.Equal(value, node.Text);
        Assert.Equal(stream.Length, node.Length);
    }

    [Fact]
    public void UserStringExposesFinalByte()
    {
        using var stream = new MemoryStream(new byte[] { 3, 0x41, 0, 1 });
        var node = new UserString { Buffer = new StreamBuffer(stream) };

        node.Parse();

        Assert.Equal("A", node.Text);
        Assert.NotNull(node.FinalByte);
        Assert.Equal(1, node.FinalByte.Value);
        Assert.Same(node.Children.Last(), node.FinalByte);
    }

    [Fact]
    public void DiffUsesTrimmedPrefixOffset()
    {
        var diff = new ListDiff<char, char>("aaaab", "aabaa");

        Assert.Equal(4, diff.Actions.Count(a => a.ActionType == ListDiffActionType.Update));
        Assert.Equal(1, diff.Actions.Count(a => a.ActionType == ListDiffActionType.Add));
        Assert.Equal(1, diff.Actions.Count(a => a.ActionType == ListDiffActionType.Remove));
    }

    [Fact]
    public void DiffMatchesLongestCommonSubsequenceForShortInputs()
    {
        var inputs = Enumerable.Range(0, 63)
            .Select(i => Convert.ToString(i + 1, 2).Substring(1))
            .ToArray();

        foreach (var source in inputs)
        {
            foreach (var destination in inputs)
            {
                var lengths = new int[source.Length + 1, destination.Length + 1];
                for (int i = 1; i <= source.Length; i++)
                {
                    for (int j = 1; j <= destination.Length; j++)
                    {
                        lengths[i, j] = source[i - 1] == destination[j - 1]
                            ? lengths[i - 1, j - 1] + 1
                            : Math.Max(lengths[i - 1, j], lengths[i, j - 1]);
                    }
                }

                var items = source.ToList();
                var diff = items.MergeInto(destination, (left, right) => left == right);

                Assert.Equal(destination, new string(items.ToArray()));
                Assert.Equal(lengths[source.Length, destination.Length],
                    diff.Actions.Count(a => a.ActionType == ListDiffActionType.Update));
            }
        }
    }

    [Fact]
    public void RuntimeFunctionHasFileContextDuringParsing()
    {
        using var stream = new MemoryStream(new byte[128]);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x1000);
        writer.Write(0x1004);
        writer.Write(0);
        writer.Write(64);

        var buffer = new StreamBuffer(stream);
        var file = new PEFile(buffer)
        {
            Length = 128,
            SectionTable = new SectionTable(1)
            {
                SectionHeaders = new[]
                {
                    new SectionHeader
                    {
                        VirtualAddress = new FourBytes { Buffer = buffer, Start = 0 },
                        VirtualSize = new FourBytes { Buffer = buffer, Start = 4 },
                        SizeOfRawData = new FourBytes { Buffer = buffer, Start = 4 },
                        PointerToRawData = new FourBytes { Buffer = buffer, Start = 12 }
                    }
                }
            }
        };
        var table = new ExceptionTable
        {
            Buffer = buffer,
            Length = 12,
            MachineType = ExceptionTable.MachineAmd64,
            PEFile = file
        };

        table.Parse();

        var function = Assert.IsType<RuntimeFunction>(Assert.Single(table.Children));
        Assert.Contains("0x1000-0x1004", function.Text);
    }
}
