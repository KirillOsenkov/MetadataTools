﻿using System;
using System.IO;

namespace GuiLabs.Metadata
{
    public class LargeAddressAware
    {
        public static bool IsLargeAddressAware(string filePath)
        {
            bool isLargeAddressAware = false;
            PrepareStream(filePath, (stream, binaryReader) => isLargeAddressAware = (binaryReader.ReadInt16() & 0x20) != 0);
            return isLargeAddressAware;
        }

        public static void SetLargeAddressAware(string filePath)
        {
            PrepareStream(filePath, (stream, binaryReader) =>
            {
                var value = binaryReader.ReadInt16();
                if ((value & 0x20) == 0)
                {
                    value = (short)(value | 0x20);
                    stream.Position -= 2;
                    var binaryWriter = new BinaryWriter(stream);
                    binaryWriter.Write(value);
                    binaryWriter.Flush();
                }
            });
        }

        private static void PrepareStream(string filePath, Action<Stream, BinaryReader> action)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                if (stream.Length < 0x3C)
                {
                    return;
                }

                var binaryReader = new BinaryReader(stream);

                // MZ header
                if (binaryReader.ReadInt16() != 0x5A4D)
                {
                    return;
                }

                stream.Position = 0x3C;
                var peHeaderLocation = binaryReader.ReadInt32();

                stream.Position = peHeaderLocation;

                // PE header
                if (binaryReader.ReadInt32() != 0x4550)
                {
                    return;
                }

                stream.Position += 0x12;

                action(stream, binaryReader);
            }
        }
    }
}
