﻿// Heavily inspired by 
// https://github.com/dotnet/roslyn/blob/1ead9e9c4dab6dc5092c1690b560dd2f113a236c/src/Compilers/Core/Portable/StrongName/CryptoBlobParser.cs

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace GuiLabs.Metadata
{
    public static class CryptoBlobParser
    {
        private enum AlgorithmClass
        {
            Signature = 1,
            Hash = 4,
        }

        private enum AlgorithmSubId
        {
            Sha1Hash = 4,
            MacHash = 5,
            RipeMdHash = 6,
            RipeMd160Hash = 7,
            Ssl3ShaMD5Hash = 8,
            HmacHash = 9,
            Tls1PrfHash = 10,
            HashReplacOwfHash = 11,
            Sha256Hash = 12,
            Sha384Hash = 13,
            Sha512Hash = 14,
        }

        private struct AlgorithmId
        {
            // From wincrypt.h
            private const int AlgorithmClassOffset = 13;
            private const int AlgorithmClassMask = 0x7;
            private const int AlgorithmSubIdOffset = 0;
            private const int AlgorithmSubIdMask = 0x1ff;

            private readonly uint _flags;

            public const int RsaSign = 0x00002400;
            public const int Sha = 0x00008004;

            public bool IsSet
            {
                get { return _flags != 0; }
            }

            public AlgorithmClass Class
            {
                get { return (AlgorithmClass)((_flags >> AlgorithmClassOffset) & AlgorithmClassMask); }
            }

            public AlgorithmSubId SubId
            {
                get { return (AlgorithmSubId)((_flags >> AlgorithmSubIdOffset) & AlgorithmSubIdMask); }
            }

            public AlgorithmId(uint flags)
            {
                _flags = flags;
            }
        }

        // From ECMAKey.h
        private static readonly byte[] s_ecmaKey = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0 };

        private const int SnPublicKeyBlobSize = 13;

        // From wincrypt.h
        private const byte PublicKeyBlobId = 0x06;
        private const byte PrivateKeyBlobId = 0x07;

        // internal for testing
        internal const int s_publicKeyHeaderSize = SnPublicKeyBlobSize - 1;

        // From StrongNameInternal.cpp
        // Checks to see if a public key is a valid instance of a PublicKeyBlob as
        // defined in StongName.h
        internal static bool IsValidPublicKey(byte[] blob)
        {
            // The number of public key bytes must be at least large enough for the header and one byte of data.
            if (blob == null || blob.Length < s_publicKeyHeaderSize + 1)
            {
                return false;
            }

            BinaryReader blobReader = new BinaryReader(new MemoryStream(blob));

            // Signature algorithm ID
            var sigAlgId = blobReader.ReadUInt32();
            // Hash algorithm ID
            var hashAlgId = blobReader.ReadUInt32();
            // Size of public key data in bytes, not including the header
            var publicKeySize = blobReader.ReadUInt32();
            // publicKeySize bytes of public key data
            var publicKey = blobReader.ReadByte();

            // The number of public key bytes must be the same as the size of the header plus the size of the public key data.
            if (blob.Length != s_publicKeyHeaderSize + publicKeySize)
            {
                return false;
            }

            // Check for the ECMA key, which does not obey the invariants checked below.
            if (Enumerable.SequenceEqual(blob, s_ecmaKey))
            {
                return true;
            }

            // The public key must be in the wincrypto PUBLICKEYBLOB format
            if (publicKey != PublicKeyBlobId)
            {
                return false;
            }

            var signatureAlgorithmId = new AlgorithmId(sigAlgId);
            if (signatureAlgorithmId.IsSet && signatureAlgorithmId.Class != AlgorithmClass.Signature)
            {
                return false;
            }

            var hashAlgorithmId = new AlgorithmId(hashAlgId);
            if (hashAlgorithmId.IsSet && (hashAlgorithmId.Class != AlgorithmClass.Hash || hashAlgorithmId.SubId < AlgorithmSubId.Sha1Hash))
            {
                return false;
            }

            return true;
        }

        private const int BlobHeaderSize = sizeof(byte) + sizeof(byte) + sizeof(ushort) + sizeof(uint);

        private const int RsaPubKeySize = sizeof(uint) + sizeof(uint) + sizeof(uint);

        private const UInt32 RSA1 = 0x31415352;
        private const UInt32 RSA2 = 0x32415352;

        // In wincrypt.h both public and private key blobs start with a
        // PUBLICKEYSTRUC and RSAPUBKEY and then start the key data
        private const int s_offsetToKeyData = BlobHeaderSize + RsaPubKeySize;

        private static byte[] CreateSnPublicKeyBlob(
            byte type,
            byte version,
            uint algId,
            uint magic,
            uint bitLen,
            uint pubExp,
            byte[] pubKeyData)
        {
            var stream = new MemoryStream(3 * sizeof(uint) + s_offsetToKeyData + pubKeyData.Length);
            var w = new StreamWriter(stream);
            w.Write((uint)AlgorithmId.RsaSign);
            w.Write((uint)AlgorithmId.Sha);
            w.Write((uint)(s_offsetToKeyData + pubKeyData.Length));

            stream.WriteByte(type);
            stream.WriteByte(version);
            stream.WriteByte((byte)0 /* 16 bits of reserved space in the spec */);
            stream.WriteByte((byte)0 /* 16 bits of reserved space in the spec */);
            w.Write((uint)algId);

            w.Write((uint)magic);
            w.Write((uint)bitLen);

            // re-add padding for exponent
            w.Write((uint)pubExp);

            stream.Write(pubKeyData, 0, pubKeyData.Length);

            return stream.ToArray();
        }

        /// <summary>
        /// Try to retrieve the public key from a crypto blob.
        /// </summary>
        /// <remarks>
        /// Can be either a PUBLICKEYBLOB or PRIVATEKEYBLOB. The BLOB must be unencrypted.
        /// </remarks>
        public static bool TryParseKey(byte[] blob, out byte[] snKey, out RSAParameters? privateKey)
        {
            privateKey = null;
            snKey = null;

            var asArray = blob;

            if (IsValidPublicKey(blob))
            {
                snKey = blob;
                return true;
            }

            if (blob.Length < BlobHeaderSize + RsaPubKeySize)
            {
                return false;
            }

            try
            {
                using (MemoryStream memStream = new MemoryStream(asArray))
                using (BinaryReader br = new BinaryReader(new MemoryStream(asArray)))
                {
                    byte bType = br.ReadByte();    // BLOBHEADER.bType: Expected to be 0x6 (PUBLICKEYBLOB) or 0x7 (PRIVATEKEYBLOB), though there's no check for backward compat reasons. 
                    byte bVersion = br.ReadByte(); // BLOBHEADER.bVersion: Expected to be 0x2, though there's no check for backward compat reasons.
                    br.ReadUInt16();               // BLOBHEADER.wReserved
                    uint algId = br.ReadUInt32();  // BLOBHEADER.aiKeyAlg
                    uint magic = br.ReadUInt32();  // RSAPubKey.magic: Expected to be 0x31415352 ('RSA1') or 0x32415352 ('RSA2') 
                    var bitLen = br.ReadUInt32();  // Bit Length for Modulus
                    var pubExp = br.ReadUInt32();  // Exponent 
                    var modulusLength = (int)(bitLen / 8);

                    if (blob.Length - s_offsetToKeyData < modulusLength)
                    {
                        return false;
                    }

                    var modulus = br.ReadBytes(modulusLength);

                    if (!(bType == PrivateKeyBlobId && magic == RSA2) && !(bType == PublicKeyBlobId && magic == RSA1))
                    {
                        return false;
                    }

                    if (bType == PrivateKeyBlobId)
                    {
                        privateKey = ToRSAParameters(asArray, true);
                        // For snKey, rewrite some of the the parameters
                        algId = AlgorithmId.RsaSign;
                        magic = RSA1;
                    }

                    snKey = CreateSnPublicKeyBlob(PublicKeyBlobId, bVersion, algId, RSA1, bitLen, pubExp, modulus);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Helper for RsaCryptoServiceProvider.ExportParameters()
        /// Copied from https://github.com/dotnet/corefx/blob/5fe5f9aae7b2987adc7082f90712b265bee5eefc/src/System.Security.Cryptography.Csp/src/System/Security/Cryptography/CapiHelper.Shared.cs
        /// </summary>
        internal static RSAParameters ToRSAParameters(this byte[] cspBlob, bool includePrivateParameters)
        {
            BinaryReader br = new BinaryReader(new MemoryStream(cspBlob));

            byte bType = br.ReadByte();    // BLOBHEADER.bType: Expected to be 0x6 (PUBLICKEYBLOB) or 0x7 (PRIVATEKEYBLOB), though there's no check for backward compat reasons. 
            byte bVersion = br.ReadByte(); // BLOBHEADER.bVersion: Expected to be 0x2, though there's no check for backward compat reasons.
            br.ReadUInt16();               // BLOBHEADER.wReserved
            int algId = br.ReadInt32();    // BLOBHEADER.aiKeyAlg

            int magic = br.ReadInt32();    // RSAPubKey.magic: Expected to be 0x31415352 ('RSA1') or 0x32415352 ('RSA2') 
            int bitLen = br.ReadInt32();   // RSAPubKey.bitLen

            int modulusLength = bitLen / 8;
            int halfModulusLength = (modulusLength + 1) / 2;

            uint expAsDword = br.ReadUInt32();

            RSAParameters rsaParameters = new RSAParameters();
            rsaParameters.Exponent = ExponentAsBytes(expAsDword);
            rsaParameters.Modulus = br.ReadReversed(modulusLength);
            if (includePrivateParameters)
            {
                rsaParameters.P = br.ReadReversed(halfModulusLength);
                rsaParameters.Q = br.ReadReversed(halfModulusLength);
                rsaParameters.DP = br.ReadReversed(halfModulusLength);
                rsaParameters.DQ = br.ReadReversed(halfModulusLength);
                rsaParameters.InverseQ = br.ReadReversed(halfModulusLength);
                rsaParameters.D = br.ReadReversed(modulusLength);
            }

            return rsaParameters;
        }

        /// <summary>
        /// Helper for converting a UInt32 exponent to bytes.
        /// Copied from https://github.com/dotnet/corefx/blob/5fe5f9aae7b2987adc7082f90712b265bee5eefc/src/System.Security.Cryptography.Csp/src/System/Security/Cryptography/CapiHelper.Shared.cs
        /// </summary>
        private static byte[] ExponentAsBytes(uint exponent)
        {
            if (exponent <= 0xFF)
            {
                return new[] { (byte)exponent };
            }
            else if (exponent <= 0xFFFF)
            {
                unchecked
                {
                    return new[]
                    {
                        (byte)(exponent >> 8),
                        (byte)(exponent)
                    };
                }
            }
            else if (exponent <= 0xFFFFFF)
            {
                unchecked
                {
                    return new[]
                    {
                        (byte)(exponent >> 16),
                        (byte)(exponent >> 8),
                        (byte)(exponent)
                    };
                }
            }
            else
            {
                return new[]
                {
                    (byte)(exponent >> 24),
                    (byte)(exponent >> 16),
                    (byte)(exponent >> 8),
                    (byte)(exponent)
                };
            }
        }


        /// <summary>
        /// Read in a byte array in reverse order.
        /// Copied from https://github.com/dotnet/corefx/blob/5fe5f9aae7b2987adc7082f90712b265bee5eefc/src/System.Security.Cryptography.Csp/src/System/Security/Cryptography/CapiHelper.Shared.cs
        /// </summary>
        private static byte[] ReadReversed(this BinaryReader br, int count)
        {
            byte[] data = br.ReadBytes(count);
            Array.Reverse(data);
            return data;
        }
    }
}