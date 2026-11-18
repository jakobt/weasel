using System.Text.RegularExpressions;

namespace Weasel.SqlServer;

/// <summary>
///     Splits rendered SQL Server DDL into the batches a <c>GO</c> line separates, so each one can be
///     sent as its own command. <c>GO</c> is a sqlcmd directive rather than T-SQL, and SqlClient will
///     raise "Incorrect syntax near 'GO'" if it is left in the text.
/// </summary>
/// <remarks>
///     The semantics are sqlcmd's: a line reading only <c>GO</c> ends the batch unless it lies inside
///     a string literal, a quoted or bracketed identifier, or a block comment. Like sqlcmd, block
///     comments do not nest, so the first <c>*/</c> closes one, and a <c>--</c> comment runs to the end
///     of its line. The optional repeat count is accepted and ignored, so the batch runs once: nothing
///     in Weasel emits a count, and repeating DDL is never what a migration means.
/// </remarks>
public static class SqlServerBatchSplitter
{
    /// <summary>
    ///     What <see cref="Split" /> splits on, for the DDL writers that have to end a batch
    ///     deliberately. Emitted on a line of its own; the splitter recognizes rather more than
    ///     this (a repeat count, a trailing semicolon, either case), but there is no reason for
    ///     Weasel to write anything but the plain word.
    /// </summary>
    public const string Separator = "GO";

    /// <summary>
    ///     A batch separator: a line whose entire content is <c>GO</c>, optionally followed by a repeat
    ///     count and/or a semicolon. Case insensitive because <c>go</c> is equally valid, and multiline
    ///     so that <c>^</c> and <c>$</c> mean line boundaries. The group around the count is
    ///     non-capturing on purpose: <see cref="Regex.Split(string)" /> injects captured groups into its
    ///     output, which would emit the count as if it were a batch.
    /// </summary>
    /// <remarks>
    ///     Every whitespace class here is horizontal, <c>[ \t]</c> rather than <c>\s</c>, so the whole
    ///     match stays on the separator's own line. With <c>\s</c> the optional count and the trailing
    ///     run could both cross a newline, and <c>GO</c> followed by a line reading only <c>5</c> would
    ///     swallow that line as if the digit were a repeat count on the <c>GO</c>. The explicit
    ///     <c>\r?</c> is what <c>\s</c> was quietly doing for CRLF input: <c>$</c> matches before the
    ///     <c>\n</c>, not before the <c>\r\n</c> pair, so the carriage return has to be consumed here.
    /// </remarks>
    private static readonly Regex _separator = new(
        @"^[ \t]*GO(?:[ \t]+\d+)?[ \t]*;?[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    ///     Does <paramref name="sql" /> contain a line that <see cref="Split" /> would treat as a batch
    ///     separator? Shares the one definition of a separator with <see cref="Split" /> on purpose: a
    ///     caller that refuses input on its own idea of what a <c>GO</c> line is could disagree with the
    ///     splitter, which is the only way such a refusal could be worse than no refusal at all.
    /// </summary>
    internal static bool ContainsSeparator(string sql) => SeparatorLines(sql).Any();

    /// <summary>
    ///     The non-empty batches of <paramref name="sql" />, in order, each trimmed of surrounding
    ///     whitespace but otherwise verbatim.
    /// </summary>
    public static IReadOnlyList<string> Split(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        var batches = new List<string>();
        var start = 0;

        foreach (var separator in SeparatorLines(sql).Append(new Range(sql.Length, sql.Length)))
        {
            var batch = sql[start..separator.Start].Trim();
            if (batch.Length > 0)
            {
                batches.Add(batch);
            }

            start = separator.End.Value;
        }

        return batches;
    }

    private static IEnumerable<Range> SeparatorLines(string sql)
    {
        var state = Lexical.Code;
        var lineStart = 0;

        while (lineStart < sql.Length)
        {
            var newline = sql.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? sql.Length : newline;

            if (state == Lexical.Code && _separator.IsMatch(sql[lineStart..lineEnd]))
            {
                yield return new Range(lineStart, newline < 0 ? sql.Length : newline + 1);
            }
            else
            {
                state = Advance(sql, lineStart, lineEnd, state);
            }

            lineStart = lineEnd + 1;
        }
    }

    private static Lexical Advance(string sql, int from, int to, Lexical state)
    {
        for (var i = from; i < to; i++)
        {
            var c = sql[i];
            var next = i + 1 < to ? sql[i + 1] : '\0';

            switch (state)
            {
                case Lexical.BlockComment when c == '*' && next == '/':
                    state = Lexical.Code;
                    i++;
                    break;
                case Lexical.String when c == '\'':
                case Lexical.QuotedIdentifier when c == '"':
                case Lexical.BracketedIdentifier when c == ']':
                    if (next == c)
                    {
                        i++;
                    }
                    else
                    {
                        state = Lexical.Code;
                    }

                    break;
                case Lexical.Code when c == '-' && next == '-':
                    return Lexical.Code;
                case Lexical.Code when c == '/' && next == '*':
                    state = Lexical.BlockComment;
                    i++;
                    break;
                case Lexical.Code when c == '\'':
                    state = Lexical.String;
                    break;
                case Lexical.Code when c == '"':
                    state = Lexical.QuotedIdentifier;
                    break;
                case Lexical.Code when c == '[':
                    state = Lexical.BracketedIdentifier;
                    break;
            }
        }

        return state;
    }

    private enum Lexical
    {
        Code,
        String,
        QuotedIdentifier,
        BracketedIdentifier,
        BlockComment
    }
}
