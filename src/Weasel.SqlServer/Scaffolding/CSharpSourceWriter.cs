using System.Globalization;
using System.Text;
using JasperFx.Core;

namespace Weasel.SqlServer.Scaffolding;

internal sealed class CSharpSourceWriter
{
    private const int IndentWidth = 4;

    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while"
    };

    private readonly StringBuilder _body = new();
    private readonly SortedSet<string> _usings = new(StringComparer.Ordinal);
    private int _level;

    public CSharpSourceWriter UsingNamespace(params string[] namespaces)
    {
        foreach (var ns in namespaces)
        {
            _usings.Add(ns);
        }

        return this;
    }

    public CSharpSourceWriter Line(string text = "")
    {
        if (text.IsEmpty())
        {
            _body.AppendLine();
        }
        else
        {
            _body.Append(' ', _level * IndentWidth).AppendLine(text);
        }

        return this;
    }

    public CSharpSourceWriter Block(string opening, Action<CSharpSourceWriter> body, string closing = "}")
    {
        if (opening.IsNotEmpty())
        {
            Line(opening);
        }

        Line("{");
        _level++;
        body(this);
        _level--;
        Line(closing);
        return this;
    }

    public string ToSourceFile(string @namespace)
    {
        var file = new StringBuilder();
        foreach (var ns in _usings)
        {
            file.AppendLine($"using {ns};");
        }

        if (_usings.Count > 0)
        {
            file.AppendLine();
        }

        file.AppendLine($"namespace {@namespace};");
        file.AppendLine();
        file.Append(_body);

        return file.ToString();
    }


    public static string TypeName(string sqlName, string suffix = "")
    {
        var builder = new StringBuilder();
        var capitalize = true;

        foreach (var c in sqlName)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(capitalize ? char.ToUpperInvariant(c) : c);
                capitalize = false;
            }
            else
            {
                capitalize = true;
            }
        }

        var name = builder.ToString();
        if (name.IsEmpty() || !char.IsLetter(name[0]))
        {
            name = "_" + name;
        }

        name += suffix;

        return ReservedWords.Contains(name) ? "@" + name : name;
    }

    public static bool IsValidIdentifier(string name)
    {
        if (name.IsEmpty())
        {
            return false;
        }

        if (!char.IsLetter(name[0]) && name[0] != '_')
        {
            return false;
        }

        return name.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    public static string SanitizeIdentifier(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        if (builder.Length == 0 || (!char.IsLetter(builder[0]) && builder[0] != '_'))
        {
            builder.Insert(0, '_');
        }

        var result = builder.ToString();
        return ReservedWords.Contains(result) ? "@" + result : result;
    }

    public static string Quote(string value)
        => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n")}\"";

    public static string Verbatim(string value)
        => $"@\"{value.Replace("\"", "\"\"")}\"";

    public static string StringArray(IEnumerable<string> values)
        => $"[{values.Select(Quote).Join(", ")}]";

    public static string Literal(bool value) => value ? "true" : "false";

    public static string Literal(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Literal(int value) => value.ToString(CultureInfo.InvariantCulture);
}
