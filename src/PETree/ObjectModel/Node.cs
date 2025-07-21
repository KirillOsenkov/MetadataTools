using System.Collections.Generic;
using System.IO;
using System.Text;
using GuiLabs.Utilities;

namespace GuiLabs.FileFormat;

public class Node
{
    public Node()
    {
    }

    public Node(ByteBuffer buffer, int start)
    {
        Buffer = buffer;
        Start = start;
    }

    public ByteBuffer Buffer { get; set; }
    public int Start { get; set; }
    public int Length { get; set; }
    public int End => Start + Length;
    internal Span Span => new Span(Start, Length);

    public int OffsetRelativeToParent
    {
        get
        {
            if (Parent == null)
            {
                return Start;
            }

            return Start - Parent.Start;
        }
    }

    public Node Parent { get; set; }

    public Node Root
    {
        get
        {
            var current = this;
            while (current.Parent != null)
            {
                current = current.Parent;
            }

            return current;
        }
    }

    private string text;
    public string Text
    {
        get
        {
            if (text == null)
            {
                text = ToString();
            }

            return text;
        }

        set => text = value;
    }

    public int LastChildEnd
    {
        get
        {
            int end = Start;

            if (HasChildren)
            {
                end = Children[Children.Count - 1].End;
            }

            return end;
        }
    }

    protected List<Node> children;
    public List<Node> Children => children ??= [];

    public bool HasChildren => children != null && children.Count > 0;

    public virtual void Parse()
    {
    }

    public Node Find(int offset)
    {
        if (offset < Start || offset >= End)
        {
            return null;
        }

        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (offset < child.Start)
            {
                return null;
            }

            if (offset >= child.End)
            {
                continue;
            }

            var found = child.Find(offset);
            if (found != null)
            {
                return found;
            }
        }

        return this;
    }

    public virtual Node Add(Node node)
    {
        bool inserted = false;
        int count = Children.Count;
        int nodeStart = node.Start;

        if (nodeStart != 0 && count > 0)
        {
            Node childToInsertInto = null;
            int insertAtIndex = 0;

            int lo = 0;
            int hi = count - 1;
            while (true)
            {
                int index = (lo + hi) >> 1;
                var child = Children[index];
                var childStart = child.Start;
                if (nodeStart < childStart)
                {
                    hi = index - 1;
                    if (hi < lo)
                    {
                        inserted = true;
                        insertAtIndex = index;
                        break;
                    }
                }
                else
                {
                    if (nodeStart < childStart + child.Length)
                    {
                        childToInsertInto = child;
                        break;
                    }

                    lo = index + 1;
                    if (lo > hi)
                    {
                        if (lo < count)
                        {
                            inserted = true;
                            insertAtIndex = lo;
                        }

                        break;
                    }
                }
            }

            if (childToInsertInto != null)
            {
                return childToInsertInto.Add(node);
            }

            if (inserted)
            {
                Children.Insert(insertAtIndex, node);
            }
        }

        if (!inserted)
        {
            if (node.Start == 0)
            {
                // This needs to run before we add this node to Children,
                // so we can access the previous child
                node.Start = LastChildEnd;
            }

            Children.Add(node);
        }

        node.Buffer = Buffer;
        node.Parent = this;

        node.Parse();

        // if Length has been preset previously, keep the old value
        int newLength = LastChildEnd - Start;
        if (newLength > Length)
        {
            Length = newLength;
        }

        if (node.Start < Start || node.End > End || node.Length > Length)
        {
            throw new System.Exception($"Child node {node.Text} span {node.Span} is outside the parent node ({this.Text}) span ({this.Span})");
        }

        if (node.End > Buffer.Length)
        {
            throw new System.Exception($"Node {node.Text} end {node.End} is outside the underlying buffer ({Buffer.Length})");
        }

        return node;
    }

    public Padding AddPadding(int length)
    {
        var node = new Padding { Length = length };
        Add(node);
        return node;
    }

    public Padding AddAlignedPadding(int alignment)
    {
        int lastChildEnd = LastChildEnd;
        int bytesNeeded = alignment - (lastChildEnd % alignment);
        if (bytesNeeded == alignment)
        {
            return null;
        }

        return AddPadding(bytesNeeded);
    }

    public Padding AddRemainingPadding()
    {
        int lastChildEnd = LastChildEnd;
        var span = new Span(lastChildEnd, End - lastChildEnd);
        if (span.Length > 0 && Buffer.IsZeroFilled(span))
        {
            return AddPadding(span.Length);
        }

        return null;
    }

    public T FindAncestor<T>() where T : Node => Parent == null ? null : Parent is T t ? t : Parent.FindAncestor<T>();

    public OneByte AddOneByte(string text = null) => Add<OneByte>(text);
    public TwoBytes AddTwoBytes(string text = null) => Add<TwoBytes>(text);
    public ThreeBytes AddThreeBytes(string text = null) => Add<ThreeBytes>(text);
    public FourBytes AddFourBytes(string text = null) => Add<FourBytes>(text);
    public EightBytes AddEightBytes(string text = null) => Add<EightBytes>(text);

    public Node AddBytes(int bytes, string text = null)
    {
        if (bytes == 0)
        {
            return null;
        }

        var node = new Node { Length = bytes };
        if (text != null)
        {
            node.Text = text;
        }

        Add(node);
        return node;
    }

    public byte[] ReadBytes(int offset, int length) => Buffer.ReadBytes(offset, length);

    public string GetHexText(int columns = 4, int columnSize = 8, int byteSpacing = 1, int columnSpacing = 2)
    {
        var sb = new StringBuilder();
        var writer = new StringWriter(sb);

        WriteHexText(writer, columns, columnSize, byteSpacing, columnSpacing);

        return sb.ToString();
    }

    private void WriteHexText(TextWriter writer, int columns = 4, int columnSize = 8, int byteSpacing = 1, int columnSpacing = 2, string prefix = null)
    {
        var charsPerLine = columns * columnSize;
        bool needsPrefix = true;

        for (int i = Start; i < End; i++)
        {
            if (needsPrefix && prefix != null)
            {
                writer.Write(prefix);
                needsPrefix = false;
            }

            var b = Buffer.ReadByte(i);
            var chars = b.ToHexChars();
            writer.Write(chars.upper);
            writer.Write(chars.lower);
            int index = (i - Start) % charsPerLine;
            if (index == charsPerLine - 1)
            {
                writer.WriteLine();
                needsPrefix = true;
                continue;
            }

            int spaces = 0;
            if (index % columnSize == columnSize - 1)
            {
                spaces = columnSpacing;
            }
            else
            {
                spaces = byteSpacing;
            }

            for (int j = 0; j < spaces; j++)
            {
                writer.Write(' ');
            }
        }
    }

    public T Add<T>(string text = null) where T : Node, new()
    {
        int start = LastChildEnd;
        var result = new T();
        result.Buffer = Buffer;
        result.Start = start;
        if (text != null)
        {
            result.Text = text;
        }

        Add(result);
        return result;
    }

    public BytesNode AddFourOrEightBytes(bool eight)
    {
        if (eight)
        {
            return AddEightBytes();
        }
        else
        {
            return AddFourBytes();
        }
    }

    public void Write(TextWriter writer, string indent)
    {
        writer.Write(indent);
        writer.WriteLine(Text);

        if (HasChildren)
        {
            indent += "    ";
            foreach (var child in Children)
            {
                child.Write(writer, indent);
            }
        }
        else
        {
            indent += "    ║ ";
            WriteHexText(writer, prefix: indent);
            writer.WriteLine();
        }
    }

    public virtual void Write(Stream stream)
    {
        if (HasChildren)
        {
            foreach (var child in Children)
            {
                child.Write(stream);
            }
        }
        else
        {
            for (int i = Start; i < End; i++)
            {
                var b = Buffer.ReadByte(i);
                stream.WriteByte(b);
            }
        }
    }

    public override string ToString()
    {
        if (Length <= 32)
        {
            var bytes = Buffer.ReadBytes(Start, Length);
            return GetType().Name + " " + bytes.ToHexString();
        }

        return $"{GetType().Name}";
    }
}
