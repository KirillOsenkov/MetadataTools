using System.Collections.Generic;
using System;
using System.Linq;

public struct Span
{
    public int Start;
    public int Length;

    public static readonly Span Empty = new Span();

    public Span(int start, int length) : this()
    {
        Start = start;
        Length = length;
    }
}

public static class TextUtilities
{
    public enum LineEndingKind
    {
        None,
        CRLF,
        LF,
        CR,
        U0085, // <Next Line>
        U2028, // Line Separator
        U2029 // Paragraph Separator
    }

    public static IReadOnlyList<string> GetLines(this string text, bool includeLineBreak = false)
    {
        if (text == null)
        {
            return Array.Empty<string>();
        }

        if (text == "")
        {
            return EmptyStringList;
        }

        return GetLineSpans(text, includeLineBreakInSpan: includeLineBreak)
            .Select(span => text.Substring(span.Start, span.Length))
            .ToArray();
    }

    public static IReadOnlyList<Span> GetLineSpans(this string text, bool includeLineBreakInSpan = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            return EmptySpanList;
        }

        var result = new List<Span>();
        text.CollectLineSpans(result, includeLineBreakInSpan);
        return result.ToArray();
    }

    private static readonly IReadOnlyList<Span> EmptySpanList = new Span[] { Span.Empty };
    private static readonly IReadOnlyList<string> EmptyStringList = new[] { "" };

    public static void CollectLineSpans(this string text, ICollection<Span> spans, bool includeLineBreakInSpan = true)
    {
        if (text == null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (spans == null)
        {
            throw new ArgumentNullException(nameof(spans));
        }

        CollectLineSpans(text, (span, lineEndingKind) => spans.Add(span), includeLineBreakInSpan);
    }

    public static void CollectLineSpans(
        IEnumerable<char> chars,
        Action<Span, LineEndingKind> spanCollector,
        bool includeLineBreakInSpan = true)
    {
        if (chars == null)
        {
            throw new ArgumentNullException(nameof(chars));
        }

        if (spanCollector == null)
        {
            throw new ArgumentNullException(nameof(spanCollector));
        }

        int currentPosition = 0;
        int currentLineLength = 0;
        bool previousWasCarriageReturn = false;

        foreach (char currentChar in chars)
        {
            if (currentChar == '\r')
            {
                if (previousWasCarriageReturn)
                {
                    int lineLengthIncludingLineBreak = currentLineLength;
                    if (!includeLineBreakInSpan)
                    {
                        currentLineLength--;
                    }

                    spanCollector(new Span(currentPosition, currentLineLength), LineEndingKind.CR);

                    currentPosition += lineLengthIncludingLineBreak;
                    currentLineLength = 1;
                }
                else
                {
                    currentLineLength++;
                    previousWasCarriageReturn = true;
                }
            }
            else if (currentChar == '\n')
            {
                var lineEndingKind = LineEndingKind.LF;
                var lineLength = currentLineLength;
                if (previousWasCarriageReturn)
                {
                    lineLength--;
                    lineEndingKind = LineEndingKind.CRLF;
                }

                currentLineLength++;
                previousWasCarriageReturn = false;
                if (includeLineBreakInSpan)
                {
                    lineLength = currentLineLength;
                }

                spanCollector(new Span(currentPosition, lineLength), lineEndingKind);
                currentPosition += currentLineLength;
                currentLineLength = 0;
            }
            else
            {
                if (previousWasCarriageReturn)
                {
                    var lineLength = currentLineLength;
                    if (!includeLineBreakInSpan)
                    {
                        lineLength--;
                    }

                    spanCollector(new Span(currentPosition, lineLength), LineEndingKind.CR);
                    currentPosition += currentLineLength;
                    currentLineLength = 0;
                }

                currentLineLength++;
                previousWasCarriageReturn = false;
            }
        }

        var finalLength = currentLineLength;
        if (previousWasCarriageReturn && !includeLineBreakInSpan)
        {
            finalLength--;
        }

        spanCollector(new Span(currentPosition, finalLength), LineEndingKind.None);

        if (previousWasCarriageReturn)
        {
            spanCollector(new Span(currentPosition, 0), LineEndingKind.None);
        }
    }

    public static string QuoteIfNeeded(this string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return filePath;
        }

        if (filePath.StartsWith("\"") && filePath.EndsWith("\""))
        {
            return filePath;
        }

        if (filePath.Contains(' '))
        {
            filePath = "\"" + filePath + "\"";
        }

        return filePath;
    }
}