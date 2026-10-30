using System;
using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GuiLabs.Metadata;
using Ref12.Diagnostics.Extraction;
using Xunit;

namespace MetadataTools.Tests
{
    public sealed class UtilityTests : IDisposable
    {
        private readonly string root = Path.Combine(AppContext.BaseDirectory, "UtilityTests-" + Guid.NewGuid().ToString("N"));

        public UtilityTests()
        {
            Directory.CreateDirectory(root);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ParsedCspKeyHasBinaryStrongNameHeader(bool includePrivateKey)
        {
            using var rsa = new RSACryptoServiceProvider(1024);
            byte[] publicBlob = rsa.ExportCspBlob(false);
            byte[] blob = rsa.ExportCspBlob(includePrivateKey);

            Assert.True(CryptoBlobParser.TryParseKey(blob, out var key, out var privateKey));

            using var expected = new MemoryStream();
            using var writer = new BinaryWriter(expected);
            writer.Write(0x2400u);
            writer.Write(0x8004u);
            writer.Write((uint)publicBlob.Length);
            writer.Write(publicBlob, 0, 4);
            writer.Write(includePrivateKey ? 0x2400u : BitConverter.ToUInt32(publicBlob, 4));
            writer.Write(publicBlob, 8, publicBlob.Length - 8);
            Assert.Equal(expected.ToArray(), key);
            Assert.True(CryptoBlobParser.IsValidPublicKey(key));
            Assert.Equal(includePrivateKey, privateKey.HasValue);
            if (includePrivateKey)
            {
                var parameters = rsa.ExportParameters(true);
                Assert.Equal(parameters.Modulus, privateKey.Value.Modulus);
                Assert.Equal(parameters.Exponent, privateKey.Value.Exponent);
                Assert.Equal(parameters.D, privateKey.Value.D);
            }
        }

        [Theory]
        [InlineData(true, CorFlags.ILOnly | CorFlags.TrackDebugData,
            CorFlags.ILOnly | CorFlags.TrackDebugData | CorFlags.Prefers32Bit)]
        [InlineData(false, CorFlags.ILOnly | CorFlags.TrackDebugData | CorFlags.Prefers32Bit | CorFlags.Requires32Bit,
            CorFlags.ILOnly | CorFlags.TrackDebugData)]
        public void PreferredBitChangesPreserveOtherCorFlags(bool preferred, CorFlags initial, CorFlags expected)
        {
            string path = Path.Combine(root, "Flags.dll");
            WriteAssembly(path, initial);

            Corflags.Set32BitPreferredSRM(path, preferred);

            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            Assert.Equal(expected, reader.PEHeaders.CorHeader.Flags);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void PeInfoRetainsArchitectureTimestampAndSize(bool x64, bool managed)
        {
            using var stream = CreatePeHeader(x64, managed, 12345, 24576);

            var info = GuiLabs.Metadata.PEFile.ReadInfo(stream);

            Assert.True(info.IsValidPEFile);
            Assert.Equal(x64, info.Platform64Bit);
            Assert.Equal(managed, info.ManagedAssembly);
            Assert.Equal(12345u, info.Timestamp);
            Assert.Equal(24576u, info.SizeOfImage);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PeInfoResultsDoNotShareMutableState(bool managed)
        {
            using var firstStream = CreatePeHeader(false, managed, 111, 4096);
            using var secondStream = CreatePeHeader(false, managed, 222, 8192);
            var first = GuiLabs.Metadata.PEFile.ReadInfo(firstStream);

            var second = GuiLabs.Metadata.PEFile.ReadInfo(secondStream);

            Assert.NotSame(first, second);
            Assert.Equal(111u, first.Timestamp);
            Assert.Equal(4096u, first.SizeOfImage);
            Assert.Equal(222u, second.Timestamp);
            Assert.Equal(8192u, second.SizeOfImage);
        }

        [Theory]
        [InlineData("abc\r", false, 3, 4)]
        [InlineData("abc\r", true, 4, 4)]
        [InlineData("abc\r\r", false, 0, 5)]
        [InlineData("abc\r\r", true, 1, 5)]
        public void TrailingCarriageReturnEndsWithAnEmptySpanAtEndOfText(
            string text, bool includeLineBreak, int precedingLength, int end)
        {
            var spans = TextUtilities.GetLineSpans(text, includeLineBreak);

            Assert.Equal(precedingLength, spans[spans.Count - 2].Length);
            Assert.Equal(end, spans[spans.Count - 1].Start);
            Assert.Equal(0, spans[spans.Count - 1].Length);
        }

        [Fact]
        public void VirtualStreamLengthUsesItsStoredLength()
        {
            using var stream = new ReadVirtualStream(null, 0, 17);
            Assert.Equal(17, stream.Length);

            stream.SetLength(23);

            Assert.Equal(23, stream.Length);
            Assert.Equal(23, stream.Seek(0, SeekOrigin.End));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AttributeStringArgumentSupportsBothConstructorHandleKinds(bool methodDefinition)
        {
            var metadata = CreateAssemblyMetadata();
            metadata.AddTypeDefinition(TypeAttributes.Public,
                metadata.GetOrAddString("System.Reflection"),
                metadata.GetOrAddString("AssemblyFileVersionAttribute"),
                default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            var signature = metadata.GetOrAddBlob(new byte[] { 0x20, 1, 1, 0x0e });
            EntityHandle constructor;
            if (methodDefinition)
            {
                constructor = metadata.AddMethodDefinition(
                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    MethodImplAttributes.IL, metadata.GetOrAddString(".ctor"), signature, 0,
                    MetadataTokens.ParameterHandle(1));
            }
            else
            {
                var typeReference = metadata.AddTypeReference(
                    MetadataTokens.EntityHandle(0x00000001),
                    metadata.GetOrAddString("System.Reflection"),
                    metadata.GetOrAddString("AssemblyFileVersionAttribute"));
                constructor = metadata.AddMemberReference(typeReference, metadata.GetOrAddString(".ctor"), signature);
            }
            metadata.AddCustomAttribute(MetadataTokens.EntityHandle(0x20000001), constructor,
                metadata.GetOrAddBlob(new byte[] { 1, 0, 5, 49, 46, 50, 46, 51, 0, 0 }));
            var image = new BlobBuilder();
            new MetadataRootBuilder(metadata).Serialize(image, 0, 0);
            using var provider = MetadataReaderProvider.FromMetadataImage(image.ToImmutableArray());
            var reader = provider.GetMetadataReader();
            var attribute = reader.GetCustomAttribute(MetadataTokens.CustomAttributeHandle(1));
            var program = typeof(UtilityTests).Assembly.GetType("SRMReader.Program")
                ?? Assembly.Load("SRMReader").GetType("SRMReader.Program", throwOnError: true);
            var method = program.GetMethod("GetCustomAttributeStringArgument", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var value = method.Invoke(null, new object[] { reader, attribute });

            Assert.Equal("1.2.3", value);
        }

        [Fact]
        public async Task PdbDownloadUsesTheRecordedPdbFileName()
        {
            string source = Path.Combine(root, "Source.pdb");
            File.WriteAllBytes(source, new byte[] { 1, 2, 3 });
            await using var server = new PointerServer(source);
            var record = new PdbRecord
            {
                AssemblyFilePath = Path.Combine(root, "Example.dll"),
                Path = @"C:\build\CustomSymbols.pdb",
                Guid = Guid.NewGuid(),
                Age = 1
            };

            string downloaded = record.DownloadPdb(server.Url);

            Assert.Equal(Path.Combine(root, "Example.pdb"), downloaded);
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(downloaded));
            Assert.Equal($"/CustomSymbols.pdb/{record.Guid.ToString("N").ToUpperInvariant()}1/file.ptr",
                await server.Request.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public async Task ReplacingMismatchedPdbUsesTheSuppliedSymbolServer()
        {
            string scan = Path.Combine(root, "scan");
            Directory.CreateDirectory(scan);
            string source = Path.Combine(root, "Source.pdb");
            var id = WritePortablePdb(source);
            string assembly = Path.Combine(scan, "Example.dll");
            WriteAssembly(assembly, CorFlags.ILOnly, "Example.pdb", id);
            string destination = Path.ChangeExtension(assembly, ".pdb");
            WritePortablePdb(destination);
            Assert.False(PdbInfo.IsMatch(assembly, destination));
            await using var server = new PointerServer(source);
            var method = typeof(PdbInfo).Assembly.GetType("MetadataTools.Program", throwOnError: true)
                .GetMethod("DownloadSymbolsForAllFiles", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            method.Invoke(null, new object[] { scan, server.Url });

            Assert.True(File.Exists(destination));
            Assert.True(PdbInfo.IsMatch(assembly, destination));
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(destination));
            Assert.StartsWith("/Example.pdb/", await server.Request.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        private static MemoryStream CreatePeHeader(bool x64, bool managed, uint timestamp, uint size)
        {
            var stream = new MemoryStream(new byte[1024]);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write((ushort)0x5a4d);
            stream.Position = 0x3c;
            writer.Write(0x80u);
            stream.Position = 0x80;
            writer.Write(0x4550u);
            writer.Write((ushort)(x64 ? 0x8664 : 0x14c));
            writer.Write((ushort)1);
            writer.Write(timestamp);
            stream.Position = 0x98;
            writer.Write((ushort)(x64 ? 0x20b : 0x10b));
            stream.Position = 0xd0;
            writer.Write(size);
            stream.Position = x64 ? 0x178 : 0x168;
            writer.Write(managed ? 0x2000u : 0u);
            stream.Position = 0;
            return stream;
        }

        private static MetadataBuilder CreateAssemblyMetadata()
        {
            var metadata = new MetadataBuilder();
            metadata.AddModule(0, metadata.GetOrAddString("Example.dll"),
                metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
            metadata.AddAssembly(metadata.GetOrAddString("Example"), new Version(1, 0),
                default, default, 0, AssemblyHashAlgorithm.None);
            metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"),
                default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            return metadata;
        }

        private static void WriteAssembly(string path, CorFlags flags, string pdbPath = null, BlobContentId pdbId = default)
        {
            var debug = new DebugDirectoryBuilder();
            if (pdbPath != null)
            {
                debug.AddCodeViewEntry(pdbPath, pdbId, 0x100);
            }
            var builder = new ManagedPEBuilder(new PEHeaderBuilder(),
                new MetadataRootBuilder(CreateAssemblyMetadata()), new BlobBuilder(),
                debugDirectoryBuilder: debug, flags: flags);
            var image = new BlobBuilder();
            builder.Serialize(image);
            File.WriteAllBytes(path, image.ToArray());
        }

        private static BlobContentId WritePortablePdb(string path)
        {
            var metadata = new MetadataBuilder();
            metadata.GetOrAddBlob(new byte[2048]);
            var builder = new PortablePdbBuilder(metadata, ImmutableArray.CreateRange(new int[64]), default,
                _ => new BlobContentId(Guid.NewGuid(), 0x11223344));
            var image = new BlobBuilder();
            var id = builder.Serialize(image);
            File.WriteAllBytes(path, image.ToArray());
            return id;
        }

        public void Dispose()
        {
            Directory.Delete(root, recursive: true);
        }

        private sealed class PointerServer : IAsyncDisposable
        {
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

            public string Url { get; }
            public Task<string> Request { get; }

            public PointerServer(string source)
            {
                listener.Start();
                Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
                Request = Serve(source);
            }

            private async Task<string> Serve(string source)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                string request = await reader.ReadLineAsync(cancellation.Token);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellation.Token)))
                {
                }
                byte[] body = Encoding.UTF8.GetBytes("PATH:" + source);
                byte[] header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, cancellation.Token);
                await stream.WriteAsync(body, cancellation.Token);
                return request.Split(' ')[1];
            }

            public async ValueTask DisposeAsync()
            {
                cancellation.Cancel();
                listener.Stop();
                try
                {
                    await Request;
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    cancellation.Dispose();
                }
            }
        }
    }
}

namespace Microsoft.Diagnostics.Runtime
{
    // The linked legacy stream only needs this ClrMD contract; these tests never read process memory.
    public interface IDataReader
    {
        bool ReadMemory(ulong address, byte[] buffer, int count, out int read);
    }
}
