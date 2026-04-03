using System.Collections.Generic;
using System.Text;

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

            if (type == 2)
            {
                Certificate = new Asn1Node { Length = dataLength };
                Add(Certificate);
            }
            else
            {
                Certificate = AddBytes(dataLength, typeText);
            }

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

public class Asn1Node : Node
{
    public override void Parse()
    {
        int offset = Start;
        byte tag = Buffer.ReadByte(offset);
        offset++;

        // Handle high-tag-number form (tag >= 31)
        if ((tag & 0x1F) == 0x1F)
        {
            while ((Buffer.ReadByte(offset) & 0x80) != 0)
            {
                offset++;
            }

            offset++;
        }

        int headerStart = Start;

        // Read length
        byte lengthByte = Buffer.ReadByte(offset);
        offset++;
        int contentLength;
        if (lengthByte < 0x80)
        {
            contentLength = lengthByte;
        }
        else
        {
            int numLengthBytes = lengthByte & 0x7F;
            contentLength = 0;
            for (int i = 0; i < numLengthBytes; i++)
            {
                contentLength = (contentLength << 8) | Buffer.ReadByte(offset);
                offset++;
            }
        }

        int headerLength = offset - Start;
        int totalLength = headerLength + contentLength;
        if (Length == 0)
        {
            Length = totalLength;
        }

        string tagName = Asn1Tags.GetTagName(tag);
        bool isConstructed = (tag & 0x20) != 0;

        Header = AddBytes(headerLength, $"Tag: {tagName}, Length: {contentLength}");

        if (isConstructed && contentLength > 0)
        {
            // Parse children
            int childEnd = offset + contentLength;
            while (LastChildEnd < childEnd)
            {
                if (LastChildEnd + 2 > childEnd)
                {
                    break;
                }

                var child = new Asn1Node();
                Add(child);
            }

            Text = tagName;
        }
        else
        {
            // Leaf node — try to display meaningful text
            string valueText = ReadAsn1Value(tag, offset, contentLength);
            if (valueText != null)
            {
                Text = $"{tagName}: {valueText}";
            }
            else
            {
                Text = tagName;
            }

            if (contentLength > 0)
            {
                AddBytes(contentLength, "Value");
            }
        }
    }

    private string ReadAsn1Value(byte tag, int offset, int length)
    {
        byte tagNumber = (byte)(tag & 0x1F);
        byte tagClass = (byte)(tag >> 6);

        if (length == 0)
        {
            return null;
        }

        // Universal class
        if (tagClass == 0)
        {
            switch (tagNumber)
            {
                case 0x02: // INTEGER
                    if (length <= 4)
                    {
                        int value = 0;
                        for (int i = 0; i < length; i++)
                        {
                            value = (value << 8) | Buffer.ReadByte(offset + i);
                        }

                        return value.ToString();
                    }

                    return null;

                case 0x03: // BIT STRING
                    return null;

                case 0x04: // OCTET STRING
                    return null;

                case 0x05: // NULL
                    return "NULL";

                case 0x06: // OID
                    return ReadOid(offset, length);

                case 0x0C: // UTF8String
                    return ReadUtf8(offset, length);

                case 0x13: // PrintableString
                    return ReadUtf8(offset, length);

                case 0x16: // IA5String
                    return ReadUtf8(offset, length);

                case 0x17: // UTCTime
                    return ReadUtf8(offset, length);

                case 0x18: // GeneralizedTime
                    return ReadUtf8(offset, length);

                case 0x1E: // BMPString (UTF-16BE)
                    return ReadBmpString(offset, length);
            }
        }

        return null;
    }

    private string ReadUtf8(int offset, int length)
    {
        var bytes = Buffer.ReadBytes(offset, length);
        return Encoding.UTF8.GetString(bytes);
    }

    private string ReadBmpString(int offset, int length)
    {
        var bytes = Buffer.ReadBytes(offset, length);
        return Encoding.BigEndianUnicode.GetString(bytes);
    }

    private string ReadOid(int offset, int length)
    {
        var components = new List<int>();
        int first = Buffer.ReadByte(offset);
        components.Add(first / 40);
        components.Add(first % 40);

        int i = 1;
        while (i < length)
        {
            int value = 0;
            byte b;
            do
            {
                b = Buffer.ReadByte(offset + i);
                value = (value << 7) | (b & 0x7F);
                i++;
            }
            while ((b & 0x80) != 0 && i < length);

            components.Add(value);
        }

        string oid = string.Join(".", components);

        if (Asn1Tags.KnownOids.TryGetValue(oid, out var name))
        {
            return $"{name} ({oid})";
        }

        return oid;
    }

    public Node Header { get; set; }
}

internal static class Asn1Tags
{
    public static string GetTagName(byte tag)
    {
        byte tagClass = (byte)(tag >> 6);
        bool isConstructed = (tag & 0x20) != 0;
        byte tagNumber = (byte)(tag & 0x1F);

        if (tagClass == 2) // Context-specific
        {
            return $"[{tagNumber}]{(isConstructed ? "" : " primitive")}";
        }

        if (tagClass == 0) // Universal
        {
            string name = tagNumber switch
            {
                0x01 => "BOOLEAN",
                0x02 => "INTEGER",
                0x03 => "BIT STRING",
                0x04 => "OCTET STRING",
                0x05 => "NULL",
                0x06 => "OBJECT IDENTIFIER",
                0x0C => "UTF8String",
                0x10 => "SEQUENCE",
                0x11 => "SET",
                0x13 => "PrintableString",
                0x16 => "IA5String",
                0x17 => "UTCTime",
                0x18 => "GeneralizedTime",
                0x1E => "BMPString",
                _ => $"Universal({tagNumber})"
            };
            return name;
        }

        return $"Tag(class={tagClass}, num={tagNumber})";
    }

    public static readonly Dictionary<string, string> KnownOids = new()
    {
        ["1.2.840.113549.1.1.1"] = "rsaEncryption",
        ["1.2.840.113549.1.1.5"] = "sha1WithRSAEncryption",
        ["1.2.840.113549.1.1.11"] = "sha256WithRSAEncryption",
        ["1.2.840.113549.1.1.12"] = "sha384WithRSAEncryption",
        ["1.2.840.113549.1.1.13"] = "sha512WithRSAEncryption",
        ["1.2.840.113549.1.7.1"] = "data",
        ["1.2.840.113549.1.7.2"] = "signedData",
        ["1.2.840.113549.1.9.3"] = "contentType",
        ["1.2.840.113549.1.9.4"] = "messageDigest",
        ["1.2.840.113549.1.9.5"] = "signingTime",
        ["1.2.840.113549.1.9.6"] = "countersignature",
        ["1.2.840.113549.1.9.16.1.4"] = "id-smime-ct-TSTInfo",
        ["1.2.840.113549.1.9.16.2.47"] = "id-smime-aa-signingCertificateV2",
        ["1.3.6.1.4.1.311.2.1.4"] = "SPC_INDIRECT_DATA",
        ["1.3.6.1.4.1.311.2.1.15"] = "SPC_PE_IMAGE_DATA",
        ["1.3.6.1.4.1.311.3.3.1"] = "ms-counter-sign",
        ["2.5.4.3"] = "commonName",
        ["2.5.4.6"] = "countryName",
        ["2.5.4.7"] = "localityName",
        ["2.5.4.8"] = "stateOrProvinceName",
        ["2.5.4.10"] = "organizationName",
        ["2.5.4.11"] = "organizationalUnitName",
        ["2.5.29.14"] = "subjectKeyIdentifier",
        ["2.5.29.15"] = "keyUsage",
        ["2.5.29.19"] = "basicConstraints",
        ["2.5.29.31"] = "cRLDistributionPoints",
        ["2.5.29.32"] = "certificatePolicies",
        ["2.5.29.35"] = "authorityKeyIdentifier",
        ["2.5.29.37"] = "extKeyUsage",
        ["2.16.840.1.101.3.4.2.1"] = "sha-256",
        ["2.16.840.1.101.3.4.2.2"] = "sha-384",
        ["2.16.840.1.101.3.4.2.3"] = "sha-512",
        ["1.3.6.1.5.5.7.1.1"] = "authorityInfoAccess",
        ["1.3.6.1.5.5.7.3.3"] = "codeSigning",
        ["1.3.6.1.5.5.7.48.1"] = "ocsp",
        ["1.3.6.1.5.5.7.48.2"] = "caIssuers",
    };
}
