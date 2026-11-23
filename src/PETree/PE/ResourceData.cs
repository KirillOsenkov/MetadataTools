using System.Collections.Generic;

namespace GuiLabs.FileFormat.PE;

/// <summary>
/// Base of resource payloads that are read field by field. If the data ends
/// early or does not match the format, the rest is added as one range.
/// </summary>
public abstract class ResourceData : Node
{
    public override void Parse()
    {
        var cursor = new FieldCursor(Buffer, Start, this) { Limit = End };
        Read(cursor);
        if (Remaining > 0)
        {
            AddBytes(Remaining, cursor.Failed ? "Remaining bytes (not decoded)" : "Remaining bytes");
        }
    }

    protected abstract void Read(FieldCursor cursor);

    /// <summary>
    /// A field that is a UTF-16 string, or 0xFFFF followed by an ordinal, or
    /// a single zero word when absent.
    /// </summary>
    protected static string ReadStringOrOrdinal(FieldCursor cursor, string name)
    {
        if (cursor.Available < 2)
        {
            cursor.Fail();
            return null;
        }

        ushort first = cursor.Buffer.ReadUInt16(cursor.Position);
        if (first == 0)
        {
            cursor.UInt16($"{name}: none");
            return null;
        }

        if (first == 0xFFFF)
        {
            cursor.UInt16($"{name}: ordinal marker");
            int ordinal = cursor.UInt16($"{name} ordinal");
            return $"#{ordinal}";
        }

        return cursor.Utf16String(name);
    }
}

public class IconResourceData : ResourceData
{
    public bool IsCursor { get; set; }

    protected override void Read(FieldCursor cursor)
    {
        string kind = IsCursor ? "Cursor" : "Icon";
        if (IsCursor)
        {
            cursor.UInt16("Hotspot X");
            cursor.UInt16("Hotspot Y");
        }

        if (cursor.Available >= 24 && cursor.Buffer.ReadUInt32(cursor.Position) == 0x474E5089)
        {
            var header = cursor.Buffer.ReadBytes(cursor.Position + 16, 8);
            int width = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            int height = (header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];
            cursor.Bytes("PNG image", cursor.Available);
            Text = $"{kind}: PNG {width}x{height}";
            return;
        }

        var info = BitmapResourceData.ReadBitmapInfo(cursor);
        if (info.width > 0)
        {
            // The height of an icon bitmap covers the image and the mask
            Text = $"{kind}: {info.width}x{info.height / 2} {info.bitCount}bpp";
        }
    }
}

public class BitmapResourceData : ResourceData
{
    protected override void Read(FieldCursor cursor)
    {
        var info = ReadBitmapInfo(cursor);
        if (info.width > 0)
        {
            Text = $"Bitmap: {info.width}x{info.height} {info.bitCount}bpp";
        }
    }

    public static (int width, int height, int bitCount) ReadBitmapInfo(FieldCursor cursor)
    {
        if (cursor.Available < 12)
        {
            cursor.Fail();
            return default;
        }

        int headerSize = cursor.Buffer.ReadInt32(cursor.Position);
        int width;
        int height;
        int bitCount;
        int colorsUsed = 0;
        int colorEntrySize = 4;
        if (headerSize == 12)
        {
            cursor.UInt32("Header size");
            width = cursor.UInt16("Width");
            height = cursor.UInt16("Height");
            cursor.UInt16("Planes");
            bitCount = cursor.UInt16("Bit count");
            colorEntrySize = 3;
        }
        else if (headerSize >= 40 && headerSize <= 124 && headerSize <= cursor.Available)
        {
            cursor.UInt32("Header size");
            width = cursor.Int32("Width");
            height = System.Math.Abs(cursor.Int32("Height"));
            cursor.UInt16("Planes");
            bitCount = cursor.UInt16("Bit count");
            cursor.UInt32("Compression");
            cursor.UInt32("Image size");
            cursor.Int32("X pixels per meter");
            cursor.Int32("Y pixels per meter");
            colorsUsed = cursor.Int32("Colors used");
            cursor.UInt32("Important colors");
            if (headerSize > 40)
            {
                cursor.Bytes("Extended header fields", headerSize - 40);
            }
        }
        else
        {
            cursor.Fail();
            return default;
        }

        if (bitCount <= 8)
        {
            int colors = colorsUsed > 0 ? colorsUsed : 1 << bitCount;
            int tableSize = colors * colorEntrySize;
            if (tableSize <= cursor.Available)
            {
                cursor.Bytes($"Color table ({colors} colors)", tableSize);
            }
        }

        if (cursor.Available > 0)
        {
            cursor.Bytes("Pixel data", cursor.Available);
        }

        return (width, height, bitCount);
    }
}

public class GroupCursorData : ResourceData
{
    protected override void Read(FieldCursor cursor)
    {
        cursor.UInt16("Reserved");
        cursor.UInt16("Type");
        int count = cursor.UInt16("Count");
        for (int i = 0; i < count && !cursor.Failed; i++)
        {
            cursor.UInt16("Width");
            cursor.UInt16("Height");
            cursor.UInt16("Planes");
            cursor.UInt16("Bit count");
            cursor.UInt32("Bytes in resource");
            cursor.UInt16("Cursor id");
        }

        Text = $"Cursor group ({count})";
    }
}

public class DialogResourceData : ResourceData
{
    protected override void Read(FieldCursor cursor)
    {
        if (cursor.Available < 18)
        {
            cursor.Fail();
            return;
        }

        bool isExtended = cursor.Buffer.ReadUInt16(Start) == 1 && cursor.Buffer.ReadUInt16(Start + 2) == 0xFFFF;
        uint style;
        int itemCount;
        if (isExtended)
        {
            cursor.UInt16("Version");
            cursor.UInt16("Signature");
            cursor.UInt32("Help id");
            cursor.UInt32("Extended style");
            style = cursor.UInt32("Style");
        }
        else
        {
            style = cursor.UInt32("Style");
            cursor.UInt32("Extended style");
        }

        itemCount = cursor.UInt16("Item count");
        cursor.UInt16("X");
        cursor.UInt16("Y");
        cursor.UInt16("Width");
        cursor.UInt16("Height");
        ReadStringOrOrdinal(cursor, "Menu");
        ReadStringOrOrdinal(cursor, "Window class");
        string title = cursor.Utf16String("Title");

        const uint SetFont = 0x40;
        if ((style & SetFont) != 0)
        {
            cursor.UInt16("Font point size");
            if (isExtended)
            {
                cursor.UInt16("Font weight");
                cursor.Byte("Font italic");
                cursor.Byte("Font character set");
            }

            cursor.Utf16String("Font typeface");
        }

        for (int i = 0; i < itemCount && !cursor.Failed; i++)
        {
            cursor.Align(4, Start);
            if (isExtended)
            {
                cursor.UInt32("Item help id");
                cursor.UInt32("Item extended style");
                cursor.UInt32("Item style");
            }
            else
            {
                cursor.UInt32("Item style");
                cursor.UInt32("Item extended style");
            }

            cursor.UInt16("Item X");
            cursor.UInt16("Item Y");
            cursor.UInt16("Item width");
            cursor.UInt16("Item height");
            if (isExtended)
            {
                cursor.UInt32("Item id");
            }
            else
            {
                cursor.UInt16("Item id");
            }

            ReadStringOrOrdinal(cursor, "Item window class");
            ReadStringOrOrdinal(cursor, "Item title");
            int extraCount = cursor.UInt16("Item creation data size");
            if (extraCount > 0)
            {
                cursor.Bytes("Item creation data", extraCount);
            }
        }

        Text = $"Dialog{(isExtended ? " (extended)" : "")}: {title} ({itemCount} items)";
    }
}

public class MenuResourceData : ResourceData
{
    protected override void Read(FieldCursor cursor)
    {
        int version = cursor.UInt16("Version");
        int headerSize = cursor.UInt16("Header size");
        int itemCount = 0;
        if (version == 0)
        {
            if (headerSize > 0)
            {
                cursor.Bytes("Header data", headerSize);
            }

            const int Popup = 0x10;
            while (cursor.Available >= 4 && !cursor.Failed)
            {
                int flags = cursor.UInt16("Item flags");
                if ((flags & Popup) == 0)
                {
                    cursor.UInt16("Item id");
                }

                cursor.Utf16String("Item text");
                itemCount++;
            }
        }
        else if (version == 1)
        {
            if (headerSize > 0)
            {
                cursor.Bytes("Header data", headerSize);
            }

            while (cursor.Available >= 16 && !cursor.Failed)
            {
                cursor.UInt32("Item type");
                cursor.UInt32("Item state");
                cursor.UInt32("Item id");
                int resourceInfo = cursor.UInt16("Item flags");
                cursor.Utf16String("Item text");
                cursor.Align(4, Start);
                const int IsPopup = 0x01;
                if ((resourceInfo & IsPopup) != 0)
                {
                    cursor.UInt32("Item help id");
                }

                itemCount++;
            }
        }
        else
        {
            cursor.Fail();
        }

        Text = $"Menu{(version == 1 ? " (extended)" : "")} ({itemCount} items)";
    }
}

public class MuiResourceData : ResourceData
{
    protected override void Read(FieldCursor cursor)
    {
        cursor.UInt32("Signature");
        cursor.UInt32("Size");
        cursor.UInt32("Version");
        cursor.UInt32("Path type");
        cursor.UInt32("File type");
        cursor.UInt32("System attributes");
        cursor.UInt32("Ultimate fallback location");
        cursor.Bytes("Service checksum", 16);
        cursor.Bytes("Checksum", 16);
        cursor.Bytes("Reserved", 24);
        string[] names =
        [
            "Main name types", "Main id types", "MUI name types", "MUI id types", "Language name",
            "Fallback language name", "Ultimate fallback language name"
        ];
        foreach (var name in names)
        {
            cursor.UInt32($"{name} offset");
            cursor.UInt32($"{name} size");
        }

        Text = "MUI resource configuration";
    }
}
