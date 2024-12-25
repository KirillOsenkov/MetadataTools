namespace GuiLabs.Utilities;

internal record struct Span(int Start, int Length)
{
    public readonly int End => Start + Length;
}
