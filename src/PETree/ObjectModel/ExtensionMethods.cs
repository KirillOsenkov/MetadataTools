using System;
using GuiLabs.Utilities;

namespace GuiLabs.FileFormat;

internal static class ExtensionMethods
{
    public static void ComputeUncoveredSpans(this Node node, Action<Span> collector)
    {
        if (!node.HasChildren)
        {
            return;
        }

        int index = node.Start;
        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            if (child.Start > index)
            {
                collector(new Span(index, child.Start - index));
            }

            ComputeUncoveredSpans(child, collector);

            index = child.End;
        }

        if (index < node.End)
        {
            collector(new Span(index, node.End - index));
        }
    }

    public static void FillWithPadding(this Node node)
    {
        node.ComputeUncoveredSpans(span =>
        {
            if (node.Buffer.IsZeroFilled(span))
            {
                var padding = new Padding
                {
                    Start = span.Start,
                    Length = span.Length
                };
                node.Add(padding);
            }
        });
    }

    public static bool IsZeroFilled(this ByteBuffer buffer, Span span)
    {
        for (int i = span.Start; i < span.End; i++)
        {
            if (buffer.ReadByte(i) != 0)
            {
                return false;
            }
        }

        return true;
    }
}
