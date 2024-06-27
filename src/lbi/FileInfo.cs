using System;
using System.Collections.Generic;
using System.Linq;
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

    private bool? isManagedAssembly;
    public bool IsManagedAssembly
    {
        get
        {
            if (isManagedAssembly == null)
            {
                isManagedAssembly = GetIsManagedAssembly(FilePath);
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

        if (!filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            !filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !filePath.EndsWith(".winmd", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!GuiLabs.Metadata.PEFileReader.IsManagedAssembly(filePath))
        {
            return false;
        }

        return true;
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
    public string SignedText
    {
        get
        {
            if (!readSignedText)
            {
                lock (snLock)
                {
                    if (!readSignedText)
                    {
                        readSignedText = true;

                        if (IsManagedAssembly)
                        {
                            ListBinaryInfo.CheckSigned(this);

                            signedText = FullSigned ?? "";
                            if (Signed != "Signed" && Signed != null)
                            {
                                signedText += "(" + Signed + ")";
                            }
                        }
                    }
                }
            }

            return signedText;
        }
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
                            ListBinaryInfo.CheckPlatform(this);

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
}
