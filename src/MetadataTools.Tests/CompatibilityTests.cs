using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using BinaryCompatChecker;
using RefDump;
using Xunit;

namespace MetadataTools.Tests;

public sealed class CompatibilityTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "CompatibilityTests-" + Guid.NewGuid().ToString("N"));

    public CompatibilityTests()
    {
        Directory.CreateDirectory(root);
    }

    [Theory]
    [InlineData(@"..\..\target\*.dll")]
    [InlineData(@"child\..\..\..\target\*.dll")]
    public void ParentTraversalContinuesResolvingPathComponents(string pattern)
    {
        string current = Path.Combine(root, "first", "second");
        Directory.CreateDirectory(Path.Combine(current, "child"));
        string expected = CreateFile(Path.Combine(root, "target", "Expected.dll"));
        CreateFile(Path.Combine(root, "Other.dll"));

        var commandLine = CommandLine.Parse(new[] { pattern }, current);

        Assert.NotNull(commandLine);
        Assert.Equal(expected, Assert.Single(commandLine.Files));
    }

    [Theory]
    [InlineData("Expected.dll")]
    [InlineData("Expected.exe")]
    [InlineData("Expected.exe.config")]
    public void AbsoluteFileMatchesRelativeFileSelection(string fileName)
    {
        string file = CreateFile(Path.Combine(root, fileName));

        var absolute = CommandLine.Parse(new[] { file }, root);
        var relative = CommandLine.Parse(new[] { fileName }, root);

        Assert.NotNull(absolute);
        Assert.NotNull(relative);
        Assert.Equal(relative.Files.OrderBy(f => f), absolute.Files.OrderBy(f => f));
        Assert.Equal(relative.AppConfigFiles.OrderBy(f => f), absolute.AppConfigFiles.OrderBy(f => f));
        Assert.Equal(file, Assert.Single(absolute.Files.Concat(absolute.AppConfigFiles)));
    }

    [Fact]
    public void InvocationAppliesDistinctBaselineAndReportPaths()
    {
        CreateFile(Path.Combine(root, "Input.dll"));
        var invocation = new Invocation
        {
            Directory = root,
            BaselinePath = Path.Combine(root, "baseline.txt"),
            ReportPath = Path.Combine(root, "report.txt")
        };

        var commandLine = invocation.GetCommandLine(new CommandLine());

        Assert.NotNull(commandLine);
        Assert.Equal(invocation.BaselinePath, commandLine.BaselineFile);
        Assert.Equal(invocation.ReportPath, commandLine.ReportFile);
        Assert.NotEqual(commandLine.BaselineFile, commandLine.ReportFile);
    }

    [Fact]
    public void BatchInvocationPreservesIgnoreUnreferenced()
    {
        CreateFile(Path.Combine(root, "Input.dll"));
        var original = new CommandLine { IgnoreUnreferencedAssemblies = true };

        Assert.True(original.Clone().IgnoreUnreferencedAssemblies);
        var commandLine = new Invocation { Directory = root }.GetCommandLine(original);

        Assert.NotNull(commandLine);
        Assert.True(commandLine.IgnoreUnreferencedAssemblies);
    }

    [Theory]
    [InlineData("-m")]
    [InlineData("/m")]
    public void MemberFlagImpliesTypesAndIncludesMembersInXml(string flag)
    {
        var dumper = new Dumper();
        string output = Path.Combine(root, "report.xml");
        var parseArgs = typeof(Dumper).GetMethod("ParseArgs", BindingFlags.Instance | BindingFlags.NonPublic);
        var dumpAssembly = typeof(Dumper).GetMethod("DumpAssembly", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(parseArgs);
        Assert.NotNull(dumpAssembly);
        Assert.True((bool)parseArgs.Invoke(dumper, new object[] { new[] { flag, output } }));
        Assert.True(dumper.OutputMembers);
        Assert.True(dumper.OutputTypes);

        var assemblies = new XElement("Assemblies");
        dumpAssembly.Invoke(dumper, new object[] { typeof(CompatibilityTests).Assembly.Location, assemblies });

        Assert.NotEmpty(assemblies.Descendants("Type"));
        Assert.NotEmpty(assemblies.Descendants("Member"));
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "");
        return path;
    }

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
    }
}
