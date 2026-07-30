using LALR.CC;
using CL = global::Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;

namespace LALR.CC.Tui.Model;

/// <summary>
/// One row of the parse-table view: the action/goto cells for a single LALR
/// state. Cells are pre-rendered to short strings (s12, r3, acc, …) and laid
/// out with a fixed-width state column followed by one fixed-width column per
/// grammar symbol (EOF lives in column 0). Trailing columns are dropped when the
/// viewport is narrower than the table.
/// </summary>
internal sealed class ParseTableRow : CL.IRowLayout
{
    public const int StateColWidth = 5;
    public const int CellWidth = 5;     // wide enough for "acc", "s99", "r99", or a 2-digit goto

    private readonly int _stateId;
    private readonly string[] _cells;     // cells[0] = EOF, cells[i+1] = symbol i
    private readonly bool[] _isShift;     // for tinting Shift cells differently from Reduce
    private readonly bool[] _isReduce;
    private readonly bool[] _isGoto;

    public ParseTableRow(int stateId, string[] cells, bool[] isShift, bool[] isReduce, bool[] isGoto)
    {
        _stateId = stateId;
        _cells = cells;
        _isShift = isShift;
        _isReduce = isReduce;
        _isGoto = isGoto;
    }

    public int StateId => _stateId;

    /// <summary>Builds an action label for one parse-table cell. Empty for Error cells.</summary>
    public static string FormatCell(Action a, bool isNonTerminal)
    {
        return a.ActionType switch
        {
            // Reducing by production 0 (the start production) is what triggers the
            // runtime accept; render it as "acc" so the table reads like a textbook
            // LALR table rather than confronting the user with a bare "r0".
            ActionType.Reduce when a.ActionParameter == 0 => "acc",
            ActionType.Reduce => "r" + a.ActionParameter,
            ActionType.Shift when isNonTerminal => a.ActionParameter.ToString(),  // goto on nonterminal column
            ActionType.Shift => "s" + a.ActionParameter,
            ActionType.ErrorRR => "RR",
            ActionType.ErrorSR => "SR",
            _ => "",
        };
    }

    /// <summary>
    /// One fixed-width cell per grammar symbol, after a fixed-width state column.
    /// <para>
    /// The trailing-column truncation the old string form did by hand
    /// (<c>used + CellWidth &lt;= width</c>) is now <see cref="Layout.Node.CollapseBelow"/> per cell:
    /// a cell that cannot get its full width is dropped whole rather than clipped into a partial
    /// number, which is what the manual loop was protecting against.
    /// </para>
    /// <para>
    /// The <c>PadLeft</c> stays, and is not the padding this refactor set out to remove: it
    /// right-aligns a value INSIDE its own fixed cell, and it has to keep matching
    /// <see cref="ParseTableView"/>'s header, which is a plain header string rather than a tree. Using
    /// <c>TextAlign.Far</c> across a 5-wide cell instead would shift every row one column against that
    /// header.
    /// </para>
    /// </summary>
    public Layout.Node BuildRow(in CL.RowContext context)
    {
        var selected = context.Selected;
        var bg = Rgba(selected ? CL.SgrColor.Blue : CL.SgrColor.Black);
        var fgState = Rgba(selected ? CL.SgrColor.BrightWhite : CL.SgrColor.BrightCyan);
        var fgPlain = Rgba(selected ? CL.SgrColor.BrightWhite : CL.SgrColor.White);
        var fgShift = Rgba(selected ? CL.SgrColor.BrightWhite : CL.SgrColor.BrightGreen);
        var fgReduce = Rgba(selected ? CL.SgrColor.BrightWhite : CL.SgrColor.BrightYellow);
        var fgGoto = Rgba(selected ? CL.SgrColor.BrightWhite : CL.SgrColor.BrightCyan);
        var fgErr = Rgba(selected ? CL.SgrColor.BrightWhite : CL.SgrColor.BrightRed);

        var children = new Layout.Node[_cells.Length + 1];
        children[0] = Layout.Builder
            .Text($" {_stateId.ToString().PadLeft(StateColWidth - 2)} ", 1f, fgState)
            .WFixed(StateColWidth).HStar();

        for (var i = 0; i < _cells.Length; i++)
        {
            var cell = _cells[i];
            var fg = cell.Length == 0 ? fgPlain
                : cell == "acc" ? fgReduce
                : cell is "RR" or "SR" ? fgErr
                : _isShift[i] ? fgShift
                : _isReduce[i] ? fgReduce
                : _isGoto[i] ? fgGoto
                : fgPlain;

            children[i + 1] = Layout.Builder
                .Text(cell.PadLeft(CellWidth - 1), 1f, fg)
                .WFixed(CellWidth).HStar()
                .CollapseBelow(CellWidth);
        }

        return Layout.Builder.HStack(children).Bg(bg);
    }

    // An alias (CL = Console.Lib) does not bring extension methods into scope, and this file aliases
    // deliberately, so ToRgba is reached in its explicit static form -- named once here rather than
    // spelled out at every colour.
    private static RGBAColor32 Rgba(CL.SgrColor color) => CL.SgrColorExtensions.ToRgba(color);
}
