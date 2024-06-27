using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace GuiLabs.Metadata.Tests
{
    public class Tests
    {
        [Fact]
        public void ScanFileSystem()
        {
            var roots = Directory.GetDirectories("C:\\", "*", SearchOption.TopDirectoryOnly);
            foreach (var root in roots)
            {
                try
                {
                    var files = Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        try
                        {
                            var isManaged = PEFile.IsManagedAssembly(file);
                            var expected = TryGetAssemblyName(file) != null;
                            if (expected != isManaged)
                            {
                                Console.WriteLine(file);
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private AssemblyName TryGetAssemblyName(string filePath)
        {
            try
            {
                return AssemblyName.GetAssemblyName(filePath);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
