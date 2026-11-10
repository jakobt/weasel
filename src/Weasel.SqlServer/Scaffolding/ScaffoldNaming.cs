using JasperFx.Core;
using Weasel.Core;

namespace Weasel.SqlServer.Scaffolding;

internal sealed record ScaffoldKind(string Folder, string Suffix)
{
    public static readonly ScaffoldKind Table = new("Tables", "Table");
    public static readonly ScaffoldKind View = new("Views", "View");
    public static readonly ScaffoldKind Function = new("Functions", "Function");
    public static readonly ScaffoldKind Procedure = new("Procedures", "Procedure");
    public static readonly ScaffoldKind Sequence = new("Sequences", "Sequence");
    public static readonly ScaffoldKind TableType = new("TableTypes", "TableType");
    public static readonly ScaffoldKind Synonym = new("Synonyms", "Synonym");
    public static readonly ScaffoldKind Trigger = new("Triggers", "Trigger");
}

internal sealed class ScaffoldNaming
{
    private readonly Dictionary<string, string> _assigned = new(StringComparer.OrdinalIgnoreCase);
    private readonly ScaffoldEmissionOptions _options;
    private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);

    private ScaffoldNaming(ScaffoldEmissionOptions options) => _options = options;

    public static ScaffoldNaming Build(ScaffoldEmissionOptions options)
    {
        var naming = new ScaffoldNaming(options);

        naming._taken.Add(options.DatabaseClassName);
        naming._taken.Add(options.OtherObjectsClassName);
        naming._taken.Add(options.OtherObjectsClassName + "Feature");

        return naming;
    }

    public void Allocate(IEnumerable<DbObjectName> identifiers, ScaffoldKind kind)
    {
        foreach (var identifier in identifiers.OrderBy(x => x.QualifiedName, StringComparer.OrdinalIgnoreCase))
        {
            var name = preferred(identifier, kind);

            if (!_taken.Add(name))
            {
                name = disambiguate(identifier, kind);
            }

            _assigned[Key(identifier, kind)] = name;
        }
    }

    public string this[DbObjectName identifier, ScaffoldKind kind] => _assigned[Key(identifier, kind)];

    private static string Key(DbObjectName identifier, ScaffoldKind kind)
        => $"{kind.Folder}|{identifier.QualifiedName}";

    private string suffixFor(ScaffoldKind kind)
        // Tables honor the configurable suffix; the other kinds keep theirs, since they exist to
        // separate a view named Foo from a table named Foo.
        => kind == ScaffoldKind.Table ? _options.ClassSuffix : kind.Suffix;

    private string preferred(DbObjectName identifier, ScaffoldKind kind)
    {
        var prefix = identifier.Schema.EqualsIgnoreCase(SqlServerProvider.Instance.DefaultDatabaseSchemaName)
            ? ""
            : CSharpSourceWriter.TypeName(identifier.Schema);

        return prefix + CSharpSourceWriter.TypeName(identifier.Name, suffixFor(kind));
    }

    private string disambiguate(DbObjectName identifier, ScaffoldKind kind)
    {
        // SanitizeIdentifier keeps underscores, which is exactly what TypeName dropped, so this
        // separates Report_Parameter from ReportParameter without inventing a counter.
        var prefix = identifier.Schema.EqualsIgnoreCase(SqlServerProvider.Instance.DefaultDatabaseSchemaName)
            ? ""
            : CSharpSourceWriter.SanitizeIdentifier(identifier.Schema) + "_";

        var candidate = prefix + CSharpSourceWriter.SanitizeIdentifier(identifier.Name) + suffixFor(kind);

        if (_taken.Add(candidate))
        {
            return candidate;
        }

        for (var i = 2; ; i++)
        {
            var numbered = candidate + i;
            if (_taken.Add(numbered))
            {
                return numbered;
            }
        }
    }
}
