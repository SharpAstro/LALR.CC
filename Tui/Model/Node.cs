using CL = global::Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;

namespace LALR.CC.Tui.Model;

/// <summary>
/// Generic tree node for the grammar/lexer trees. Holds a label (the plain-
/// text content, used for status-line breadcrumb), an optional secondary tag
/// rendered dimmed on the right, and a styled glyph that decorates the row.
/// Children are eagerly populated by the builder classes — these trees are
/// small (dozens to hundreds of nodes) so lazy loading isn't needed.
/// </summary>
internal sealed class Node : CL.ITreeNode<Node>
{
    public Node(string label, NodeKind kind, IReadOnlyList<Node>? children = null, string? rightTag = null)
    {
        Label = label;
        Kind = kind;
        Children = children ?? [];
        RightTag = rightTag;
    }

    public string Label { get; }
    public NodeKind Kind { get; }
    public string? RightTag { get; }
    public IReadOnlyList<Node> Children { get; }
    public bool HasChildren => Children.Count > 0;

    /// <summary>Plain-text label used by the status bar (no escape sequences).</summary>
    public string PlainTitle => RightTag is null ? Label : $"{Label}    {RightTag}";

    /// <summary>
    /// Layout: <c>[label]  [right tag]</c>, the label taking the slack.
    /// <para>
    /// The old string form hand-rolled the whole thing -- rightLen / gap / labelMax / pad, plus a
    /// manual truncation and a separately-styled padding run -- which is precisely what the layout
    /// engine does. The drop-the-tag-before-the-label priority survives as
    /// <see cref="Layout.Node.CollapseBelow"/> on the tag: a Stack child whose arranged extent falls
    /// under the threshold is dropped whole (no paint, no hit, no gap) rather than clipped into an
    /// unreadable fragment.
    /// </para>
    /// <para>
    /// One deliberate cosmetic loss: an over-long label is now CLIPPED by the engine rather than
    /// truncated with an ellipsis, because <c>Layout.Content.Text</c> has no ellipsize option. Adding
    /// one belongs in DIR.Lib, and pulling DIR.Lib into this release wave to buy a single glyph was not
    /// worth it.
    /// </para>
    /// </summary>
    public Layout.Node BuildNodeContent(in CL.RowContext context)
    {
        // Colour the primary label by node kind. Selected rows always get the bright variant on a
        // non-black background to make the cursor obvious.
        var (fg, bg) = StyleFor(Kind, context.Selected);

        var label = Layout.Builder.Text(Label, 1f, Rgba(fg)).WStar().HStar();

        if (RightTag is not { Length: > 0 } right)
        {
            return Layout.Builder.HStack(label).Bg(Rgba(bg));
        }

        // The two-space gap rides inside the tag cell so the tag and its gap collapse as one unit --
        // a separate spacer would linger after the tag was dropped.
        const int GapColumns = 2;
        var tagColumns = right.Length + GapColumns;

        return Layout.Builder.HStack(
                label,
                Layout.Builder.Text($"  {right}", 1f, Rgba(CL.SgrColor.BrightBlack))
                    .WFixed(tagColumns).HStar()
                    .CollapseBelow(tagColumns))
            .Bg(Rgba(bg));
    }

    // An alias (CL = Console.Lib) does not bring extension methods into scope, and this file aliases
    // deliberately, so ToRgba is reached in its explicit static form -- named once here rather than
    // spelled out at every colour.
    private static RGBAColor32 Rgba(CL.SgrColor color) => CL.SgrColorExtensions.ToRgba(color);

    private static (CL.SgrColor Fg, CL.SgrColor Bg) StyleFor(NodeKind kind, bool isSelected)
    {
        if (isSelected) return (CL.SgrColor.BrightWhite, CL.SgrColor.Blue);
        return kind switch
        {
            NodeKind.Group       => (CL.SgrColor.BrightYellow, CL.SgrColor.Black),
            NodeKind.Production  => (CL.SgrColor.BrightWhite,  CL.SgrColor.Black),
            NodeKind.LhsSymbol   => (CL.SgrColor.BrightCyan,   CL.SgrColor.Black),
            NodeKind.RhsTerminal => (CL.SgrColor.BrightGreen,  CL.SgrColor.Black),
            NodeKind.RhsNonTerm  => (CL.SgrColor.BrightCyan,   CL.SgrColor.Black),
            NodeKind.LexerState  => (CL.SgrColor.BrightYellow, CL.SgrColor.Black),
            NodeKind.LexerRule   => (CL.SgrColor.BrightWhite,  CL.SgrColor.Black),
            NodeKind.Symbol      => (CL.SgrColor.White,        CL.SgrColor.Black),
            _                    => (CL.SgrColor.White,        CL.SgrColor.Black),
        };
    }
}

internal enum NodeKind
{
    Group,
    Production,
    LhsSymbol,
    RhsTerminal,
    RhsNonTerm,
    LexerState,
    LexerRule,
    Symbol,
}
