using System.Collections.Generic;
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

    private string text;
    public string Text
    {
        get => text ?? ToString();
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

    public virtual void Add(Node node)
    {
        bool inserted = false;
        if (node.Start != 0)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                if (child.Start > node.Start)
                {
                    Children.Insert(i, node);
                    inserted = true;
                    break;
                }
                else if (node.Start >= child.Start && node.Start < child.End)
                {
                    child.Add(node);
                    return;
                }
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

        var charsPerLine = columns * columnSize;

        for (int i = Start; i < End; i++)
        {
            var b = Buffer.ReadByte(i);
            var chars = b.ToHexChars();
            sb.Append(chars.upper);
            sb.Append(chars.lower);
            int index = (i - Start) % charsPerLine;
            if (index == charsPerLine - 1)
            {
                sb.AppendLine();
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
                sb.Append(' ');
            }
        }

        return sb.ToString();
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

    public override string ToString()
    {
        if (Length <= 32)
        {
            var bytes = Buffer.ReadBytes(Start, Length);
            return $"{GetType().Name} {bytes.ToHexString()}";
        }

        return $"{GetType().Name} {Start:x0} ({Length} bytes)";
    }
}
