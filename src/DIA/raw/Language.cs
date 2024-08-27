using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VisualStudioProvider.PDB.raw
{
    public enum Language
    {
        C = 0x00,
        CXX = 0x01,
        FORTRAN = 0x02,
        MASM = 0x03,
        PASCAL = 0x04,
        BASIC = 0x05,
        COBOL = 0x06,
        LINK = 0x07,
        CVTRES = 0x08,
        CVTPGD = 0x09,
        CSHARP = 0x0A,
        VB = 0x0B,
        ILASM = 0x0C,
        JAVA = 0x0D,
        JSCRIPT = 0x0E,
        MSIL = 0x0F,
        HLSL = 0x10
    }
}
