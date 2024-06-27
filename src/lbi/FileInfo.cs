using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GuiLabs.Metadata;
using Mono.Cecil;

public class FileInfo
{
    public string Text { get; set; }

    public string FilePath { get; set; }

    // set by sn
    public string Signed { get; set; }
    public string FullSigned { get; set; }

    // set by corflags
    public string Architecture { get; set; }
    public string Platform { get; set; }

    public static FileInfo Get(string filePath, bool isConfirmedManagedAssembly = false)
    {
        var fileInfo = new FileInfo
        {
            FilePath = filePath
        };

        if (isConfirmedManagedAssembly)
        {
            fileInfo.isManagedAssembly = true;
        }

        return fileInfo;
    }

    private PEFile peFile;
    public PEFile PEFile
    {
        get
        {
            if (peFile == null)
            {
                peFile = PEFile.ReadInfo(FilePath);
            }

            return peFile;
        }
    }

    private bool? isManagedAssembly;
    public bool IsManagedAssembly
    {
        get
        {
            if (isManagedAssembly == null)
            {
                isManagedAssembly = IsManagedFileExtension(FilePath) && PEFile.ManagedAssembly;
            }

            return isManagedAssembly.Value;
        }
    }

    public static bool GetIsManagedAssembly(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        if (!IsManagedFileExtension(filePath))
        {
            return false;
        }

        if (!PEFile.IsManagedAssembly(filePath))
        {
            return false;
        }

        return true;
    }

    public static bool IsManagedFileExtension(string filePath)
    {
        return
            filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".winmd", StringComparison.OrdinalIgnoreCase);
    }

    private string assemblyName;
    public string AssemblyName
    {
        get
        {
            if (assemblyName == null)
            {
                if (IsManagedAssembly)
                {
                    assemblyName = ListBinaryInfo.GetAssemblyNameText(FilePath);
                }
                else
                {
                    assemblyName = ListBinaryInfo.NotAManagedAssembly;
                }
            }

            return assemblyName;
        }
    }

    private string targetFramework = null;
    public string TargetFramework
    {
        get
        {
            ReadModule();
            return targetFramework;
        }
    }

    private string version = null;
    public string Version
    {
        get
        {
            ReadModule();
            return version;
        }
    }

    private string fileVersion = null;
    public string FileVersion
    {
        get
        {
            ReadModule();
            return fileVersion;
        }
    }

    private string informationalVersion = null;
    public string InformationalVersion
    {
        get
        {
            ReadModule();
            return informationalVersion;
        }
    }

    private bool readModule = false;
    private void ReadModule()
    {
        if (readModule)
        {
            return;
        }

        lock (this)
        {
            if (readModule)
            {
                return;
            }

            readModule = true;

            try
            {
                ReadModuleInfo();
            }
            catch
            {
            }
        }
    }

    private void ReadModuleInfo()
    {
        if (!IsManagedAssembly)
        {
            return;
        }

        string filePath = FilePath;

        var parameters = new ReaderParameters(ReadingMode.Deferred);
        using (var module = ModuleDefinition.ReadModule(filePath, parameters))
        {
            version = module.Assembly.Name.Version.ToString();

            var flags = module.Attributes;
            if ((flags & ModuleAttributes.Required32Bit) != 0)
            {
                Architecture = "x86";
            }
            else
            {
                Architecture = "Any CPU";
            }

            if ((flags & ModuleAttributes.Preferred32Bit) != 0)
            {
                Platform = "32BITPREF : 1";
                Architecture = "Any CPU";
            }
            else
            {
                Platform = "32BITPREF : 0";
            }

            bool hasStrongNameDataDirectory = HasStrongName(module);
            bool hasStrongNameFlag = (flags & ModuleAttributes.StrongNameSigned) != 0;
            if (hasStrongNameDataDirectory)
            {
                FullSigned = "Full-signed";
            }
            else
            {
                if (hasStrongNameFlag)
                {
                    FullSigned = "Public signed";
                }
                else
                {
                    FullSigned = "Delay-signed or test-signed";
                }
            }

            if (hasStrongNameFlag)
            {
                Signed = "Signed";
            }
            else
            {
                Signed = "Unsigned";
            }

            var customAttributes = module.GetCustomAttributes().ToArray();

            var targetFrameworkAttribute = customAttributes.FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute");
            if (targetFrameworkAttribute != null)
            {
                var value = targetFrameworkAttribute.ConstructorArguments[0].Value;
                string tf = ShortenTargetFramework(value.ToString());
                targetFramework = tf;
            }

            var assemblyFileVersion = customAttributes.FirstOrDefault(a => a.AttributeType.FullName ==
                "System.Reflection.AssemblyFileVersionAttribute");
            if (assemblyFileVersion != null)
            {
                var value = assemblyFileVersion.ConstructorArguments[0].Value;
                fileVersion = value.ToString();
            }

            var assemblyInformationalVersion = customAttributes.FirstOrDefault(a => a.AttributeType.FullName ==
                "System.Reflection.AssemblyInformationalVersionAttribute");
            if (assemblyInformationalVersion != null)
            {
                var value = assemblyInformationalVersion.ConstructorArguments[0].Value;
                informationalVersion = value.ToString();
            }
        }
    }

    private static readonly FieldInfo imageField =
        typeof(ModuleDefinition).GetField("Image", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Type imageType =
        typeof(ModuleDefinition).Assembly.GetType("Mono.Cecil.PE.Image");
    private static readonly FieldInfo strongNameField = imageType.GetField("StrongName");
    private static readonly Type dataDirectoryType =
        typeof(ModuleDefinition).Assembly.GetType("Mono.Cecil.PE.DataDirectory");
    private static readonly FieldInfo virtualAddressField = dataDirectoryType.GetField("VirtualAddress");
    private static readonly FieldInfo sizeField = dataDirectoryType.GetField("Size");
    private static readonly MethodInfo getReaderAt =
        imageType.GetMethod("GetReaderAt", BindingFlags.NonPublic | BindingFlags.Instance);

    private bool HasStrongName(ModuleDefinition module)
    {
        object image = imageField.GetValue(module);
        if (image == null)
        {
            return false;
        }

        object strongName = strongNameField.GetValue(image);
        if (strongName == null)
        {
            return false;
        }

        uint virtualAddress = (uint)virtualAddressField.GetValue(strongName);
        uint size = (uint)sizeField.GetValue(strongName);
        if (virtualAddress == 0 || size == 0)
        {
            return false;
        }

        var binaryReader = (BinaryReader)getReaderAt.Invoke(image, new object[] { virtualAddress });
        var bytes = binaryReader.ReadBytes((int)size);
        StrongNameBytes = bytes;

        if (bytes.All(b => b == 0))
        {
            return false;
        }

        return true;
    }

    private static readonly Dictionary<string, string> targetFrameworkNames = new Dictionary<string, string>()
    {
        { ".NETFramework,Version=v", "net" },
        { ".NETCoreApp,Version=v", "netcoreapp" },
        { ".NETStandard,Version=v", "netstandard" }
    };

    private static string ShortenTargetFramework(string name)
    {
        foreach (var kvp in targetFrameworkNames)
        {
            if (name.StartsWith(kvp.Key))
            {
                var shortened = name.Substring(kvp.Key.Length);
                if (kvp.Value == "net")
                {
                    shortened = shortened.Replace(".", "");
                }

                return kvp.Value + shortened;
            }
        }

        return name;
    }

    private string signedText = null;
    private bool readSignedText;
    private readonly object snLock = new object();
    public string GetSignedText(bool printSn, bool validateSn)
    {
        if (!printSn)
        {
            return null;
        }

        if (!readSignedText)
        {
            lock (snLock)
            {
                if (!readSignedText)
                {
                    readSignedText = true;

                    if (IsManagedAssembly)
                    {
                        if (validateSn)
                        {
                            ListBinaryInfo.CheckSigned(this);
                        }

                        signedText = FullSigned ?? "";
                    }
                }
            }
        }

        return signedText;
    }

    private string platformText = null;
    private bool readPlatformText;
    private readonly object corflagsLock = new object();
    public string PlatformText
    {
        get
        {
            if (!readPlatformText)
            {
                lock (corflagsLock)
                {
                    if (!readPlatformText)
                    {
                        readPlatformText = true;

                        if (IsManagedAssembly)
                        {
                            //ListBinaryInfo.CheckPlatform(this);

                            platformText = Architecture;
                            if (Platform != "32BITPREF : 0" && Platform != null)
                            {
                                platformText += "(" + Platform + ")";
                            }
                        }
                    }
                }
            }

            return platformText;
        }
    }

    private string sha;
    public string Sha
    {
        get
        {
            if (sha == null)
            {
                sha = Utilities.SHA1Hash(FilePath);
            }

            return sha;
        }
    }

    private long fileSize = -1;
    public long FileSize
    {
        get
        {
            if (fileSize == -1)
            {
                fileSize = new System.IO.FileInfo(FilePath).Length;
            }

            return fileSize;
        }
    }

    public byte[] StrongNameBytes { get; private set; }
}
