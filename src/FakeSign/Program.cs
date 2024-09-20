using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class Program
{
    private const int OffsetFromStartOfCorHeaderToFlags = 16;

    private static bool ExecuteCore(string assemblyPath, bool unSign = false, bool force = false)
    {
        if (Directory.Exists(assemblyPath))
        {
            Console.Error.WriteLine($"Expected file, not a directory: {assemblyPath}");
            return false;
        }
        if (!File.Exists(assemblyPath))
        {
            Console.Error.WriteLine($"File not found: {assemblyPath}");
            return false;
        }
        checked
        {
            using (FileStream fileStream = OpenFile(assemblyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                using PEReader pEReader = new PEReader((Stream)fileStream);
                using BinaryWriter binaryWriter = new BinaryWriter(fileStream);
                MetadataReader metadataReader = ValidateManagedAssemblyAndGetMetadataReader(pEReader);
                if (metadataReader == null)
                {
                    Console.Error.WriteLine(string.Format("Cannot {0} {1}.", unSign ? "un-sign" : "sign", assemblyPath));
                    return false;
                }
                if (!force && !Validate(pEReader, metadataReader, unSign))
                {
                    Console.Error.WriteLine(string.Format("Use the -f (force) option to {0} {1} anyway.", unSign ? "un-sign" : "sign", assemblyPath));
                    return false;
                }
                fileStream.Position = pEReader.PEHeaders.CorHeaderStartOffset + 16;
                CorFlags flags = pEReader.PEHeaders.CorHeader.Flags;
                flags = ((!unSign) ? (flags | CorFlags.StrongNameSigned) : (flags & ~CorFlags.StrongNameSigned));
                binaryWriter.Write((uint)flags);
            }
            return true;
        }
    }

    private static MetadataReader ValidateManagedAssemblyAndGetMetadataReader(PEReader peReader)
    {
        if (!peReader.HasMetadata)
        {
            Console.Error.WriteLine("PE file is not a managed module.");
            return null;
        }
        MetadataReader metadataReader = peReader.GetMetadataReader();
        if (!metadataReader.IsAssembly)
        {
            Console.Error.WriteLine("PE file is not an assembly.");
            return null;
        }
        return metadataReader;
    }

    private static bool Validate(PEReader peReader, MetadataReader mdReader, bool unSign)
    {
        CorHeader corHeader = peReader.PEHeaders.CorHeader;
        CorFlags corFlags = (unSign ? CorFlags.StrongNameSigned : ((CorFlags)0));
        CorFlags corFlags2 = corHeader.Flags & CorFlags.StrongNameSigned;
        if (corFlags != corFlags2)
        {
            Console.Error.WriteLine(string.Format("PE file is {0} strong-name signed.", unSign ? "not" : "already"));
            return false;
        }
        if (corHeader.StrongNameSignatureDirectory.Size <= 0 || mdReader.GetAssemblyDefinition().PublicKey.IsNil)
        {
            Console.Error.WriteLine("PE file is not a delay-signed assembly.");
            return false;
        }
        return true;
    }

    private static FileStream OpenFile(string path, FileMode mode, FileAccess access, FileShare share)
    {
        try
        {
            return new FileStream(path, mode, access, share);
        }
        catch (ArgumentException ex)
        {
            throw new IOException(ex.Message, ex);
        }
        catch (UnauthorizedAccessException ex2)
        {
            throw new IOException(ex2.Message, ex2);
        }
        catch (NotSupportedException ex3)
        {
            throw new IOException(ex3.Message, ex3);
        }
    }

    internal static int Main(string[] args)
    {
        string text = null;
        bool unSign = false;
        bool force = false;
        int num = 0;
        while (true)
        {
            if (num < args.Length)
            {
                string text2 = args[num];
                if (text2.Length >= 2 && (text2[0] == '-' || text2[0] == '/'))
                {
                    switch (text2[1])
                    {
                        case 'U':
                        case 'u':
                            unSign = true;
                            goto IL_00cb;
                        case 'F':
                        case 'f':
                            force = true;
                            goto IL_00cb;
                        default:
                            Console.Error.WriteLine($"Unrecognized switch {text2}");
                            break;
                        case '?':
                            break;
                    }
                    break;
                }
                if (text != null)
                {
                    Console.Error.WriteLine("Too many arguments.");
                    break;
                }
                text = text2;
                goto IL_00cb;
            }
            if (text == null)
            {
                Console.Error.WriteLine("Missing assemblyPath.");
                break;
            }
            return (!ExecuteCore(text, unSign, force)) ? 1 : 0;
        IL_00cb:
            num++;
        }
        Console.Error.Write("Sets or removes the \"strong name signed\" flag in a managed assembly. This\r\ncreates an assembly that can be loaded in full trust without registering for\r\nverification skipping.\r\n\r\nFakeSign [-u] [-f] assemblyPath\r\n    -u (unsign) Clears the strong name flag (default is to set the flag).\r\n    -f (force) Updates even if nothing would change.\r\n");
        return 1;
    }
}
