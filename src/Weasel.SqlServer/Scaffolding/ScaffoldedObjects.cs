using Weasel.Core;
using Weasel.SqlServer.Synonyms;
using Weasel.SqlServer.Tables;
using Weasel.SqlServer.Views;

namespace Weasel.SqlServer.Scaffolding;

public sealed record SqlModule(DbObjectName Identifier, string CreateSql);

public sealed record ScaffoldedTrigger(
    DbObjectName Identifier,
    DbObjectName Target,
    TriggerTiming Timing,
    TriggerEvents Events,
    string Header,
    string Body,
    bool IsDisabled);

public sealed record ScaffoldNote(string Category, string Object);

public sealed class ScaffoldedTable
{
    public ScaffoldedTable(Table table) => Table = table;

    public Table Table { get; }

    public Dictionary<string, string> Collations { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool EmitPrimaryKeyName { get; set; } = true;

    /// <summary>
    ///     A non-default collation rides in the type string, since the published model has no
    ///     collation property. Comparison reads the type up to its '(' and the length inside it, so the
    ///     suffix is invisible to drift detection.
    /// </summary>
    public string TypeFor(TableColumn column)
        => Collations.TryGetValue(column.Name, out var collation)
            ? $"{column.Type} collate {collation}"
            : column.Type;
}

public sealed record ScaffoldedObjects
{
    public IReadOnlyList<View> Views { get; init; } = [];
    public IReadOnlyList<Sequence> Sequences { get; init; } = [];
    public IReadOnlyList<Synonym> Synonyms { get; init; } = [];
    public IReadOnlyList<SqlModule> Functions { get; init; } = [];
    public IReadOnlyList<SqlModule> Procedures { get; init; } = [];
    public IReadOnlyList<TableType> TableTypes { get; init; } = [];
    public IReadOnlyList<ScaffoldedTrigger> Triggers { get; init; } = [];

    public int Count => Views.Count + Sequences.Count + Synonyms.Count + Functions.Count + Procedures.Count +
                        TableTypes.Count + Triggers.Count;
}
