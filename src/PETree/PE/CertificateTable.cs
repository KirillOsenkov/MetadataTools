namespace GuiLabs.FileFormat.PE;

public class CertificateTable : Node
{
    public CertificateTable()
    {
        Text = "Certificate table";
    }

    public override void Parse()
    {
        while (LastChildEnd < End)
        {
            var remaining = End - LastChildEnd;
            if (remaining < 8)
            {
                AddRemainingPadding();
                break;
            }

            Add<WinCertificate>();
        }
    }
}

public class WinCertificate : Node
{
    public WinCertificate()
    {
        Text = "WIN_CERTIFICATE";
    }

    public override void Parse()
    {
        DwLength = AddFourBytes("dwLength");
        int length = DwLength.Value;
        Length = length;

        WRevision = AddTwoBytes("wRevision");
        WCertificateType = AddTwoBytes("wCertificateType");

        int dataLength = length - 8;
        if (dataLength > 0)
        {
            short type = WCertificateType.Value;
            string typeText = type switch
            {
                1 => "X.509 certificate",
                2 => "PKCS#7 SignedData",
                3 => "Reserved",
                4 => "Terminal server protocol stack certificate",
                _ => $"Certificate type {type}"
            };

            Certificate = AddBytes(dataLength, typeText);
            Text = typeText;
        }

        // WIN_CERTIFICATE entries are aligned to 8-byte boundaries
        int aligned = (length + 7) & ~7;
        if (aligned > length)
        {
            Length = aligned;
            AddRemainingPadding();
        }
    }

    public FourBytes DwLength { get; set; }
    public TwoBytes WRevision { get; set; }
    public TwoBytes WCertificateType { get; set; }
    public Node Certificate { get; set; }
}
