using Weasel.Core;

namespace Weasel.SqlServer.Scaffolding;

internal sealed record ParsedTrigger(TriggerTiming Timing, TriggerEvents Events, string Header, string Body);

internal static class TriggerDefinitionParser
{
    public static ParsedTrigger? Parse(string definition, out string? problem)
    {
        problem = null;
        var reader = new Reader(definition);

        if (!reader.Keyword("CREATE"))
        {
            problem = "the definition does not start with CREATE";
            return null;
        }

        if (reader.Keyword("OR") && !reader.Keyword("ALTER"))
        {
            problem = "CREATE OR is not followed by ALTER";
            return null;
        }

        if (!reader.Keyword("TRIGGER") || !reader.MultipartName() || !reader.Keyword("ON") || !reader.MultipartName())
        {
            problem = "the CREATE TRIGGER header could not be read";
            return null;
        }

        if (reader.Peek("WITH"))
        {
            problem = "it declares WITH options, which Weasel's Trigger cannot express";
            return null;
        }

        TriggerTiming timing;
        if (reader.Keyword("FOR") || reader.Keyword("AFTER"))
        {
            timing = TriggerTiming.After;
        }
        else if (reader.Keyword("INSTEAD"))
        {
            if (!reader.Keyword("OF"))
            {
                problem = "INSTEAD is not followed by OF";
                return null;
            }

            timing = TriggerTiming.InsteadOf;
        }
        else
        {
            problem = "it fires on database or server events";
            return null;
        }

        var events = TriggerEvents.None;
        do
        {
            if (reader.Keyword("INSERT")) events |= TriggerEvents.Insert;
            else if (reader.Keyword("UPDATE")) events |= TriggerEvents.Update;
            else if (reader.Keyword("DELETE")) events |= TriggerEvents.Delete;
            else
            {
                problem = "its event list could not be read";
                return null;
            }
        } while (reader.Symbol(','));

        if (reader.Keyword("WITH") && !reader.Keyword("APPEND"))
        {
            problem = "it declares WITH options, which Weasel's Trigger cannot express";
            return null;
        }

        if (reader.Peek("NOT"))
        {
            problem = "it is NOT FOR REPLICATION, which Weasel's Trigger cannot express";
            return null;
        }

        if (!reader.Keyword("AS"))
        {
            problem = "the AS that ends the header was not found";
            return null;
        }

        return new ParsedTrigger(timing, events, definition[..reader.Position], definition[reader.Position..]);
    }

    private sealed class Reader(string text)
    {
        public int Position { get; private set; }

        public bool Peek(string keyword)
        {
            var start = Position;
            var found = Keyword(keyword);
            Position = start;
            return found;
        }

        public bool Keyword(string keyword)
        {
            skipTrivia();

            var end = Position + keyword.Length;
            if (end > text.Length ||
                string.Compare(text, Position, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0 ||
                (end < text.Length && isWordCharacter(text[end])))
            {
                return false;
            }

            Position = end;
            return true;
        }

        public bool Symbol(char symbol)
        {
            skipTrivia();
            if (Position < text.Length && text[Position] == symbol)
            {
                Position++;
                return true;
            }

            return false;
        }

        public bool MultipartName()
        {
            do
            {
                if (!identifier())
                {
                    return false;
                }
            } while (Symbol('.'));

            return true;
        }

        private bool identifier()
        {
            skipTrivia();
            if (Position >= text.Length)
            {
                return false;
            }

            var open = text[Position];
            if (open is '[' or '"')
            {
                var close = open == '[' ? ']' : '"';
                for (var i = Position + 1; i < text.Length; i++)
                {
                    if (text[i] != close)
                    {
                        continue;
                    }

                    if (i + 1 < text.Length && text[i + 1] == close)
                    {
                        i++;
                        continue;
                    }

                    Position = i + 1;
                    return true;
                }

                return false;
            }

            var start = Position;
            while (Position < text.Length && isWordCharacter(text[Position]))
            {
                Position++;
            }

            return Position > start;
        }

        private static bool isWordCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

        private void skipTrivia()
        {
            while (Position < text.Length)
            {
                if (char.IsWhiteSpace(text[Position]))
                {
                    Position++;
                }
                else if (startsWith("--"))
                {
                    var newline = text.IndexOf('\n', Position);
                    Position = newline < 0 ? text.Length : newline + 1;
                }
                else if (startsWith("/*"))
                {
                    skipBlockComment();
                }
                else
                {
                    return;
                }
            }
        }

        private void skipBlockComment()
        {
            var depth = 0;
            while (Position < text.Length)
            {
                if (startsWith("/*"))
                {
                    depth++;
                    Position += 2;
                }
                else if (startsWith("*/"))
                {
                    depth--;
                    Position += 2;
                    if (depth == 0)
                    {
                        return;
                    }
                }
                else
                {
                    Position++;
                }
            }
        }

        private bool startsWith(string value)
            => string.CompareOrdinal(text, Position, value, 0, value.Length) == 0;
    }
}
