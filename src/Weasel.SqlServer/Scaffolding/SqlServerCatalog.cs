using Weasel.Core;

namespace Weasel.SqlServer.Scaffolding;

public sealed record SqlServerCatalog
{
    public IReadOnlyList<DbObjectName> Tables { get; init; } = [];
    public IReadOnlyList<DbObjectName> Views { get; init; } = [];
    public IReadOnlyList<DbObjectName> Sequences { get; init; } = [];
    public IReadOnlyList<DbObjectName> Functions { get; init; } = [];
    public IReadOnlyList<DbObjectName> Procedures { get; init; } = [];
    public IReadOnlyList<DbObjectName> TableTypes { get; init; } = [];
    public IReadOnlyList<DbObjectName> Synonyms { get; init; } = [];

    public IReadOnlyList<DbObjectName> Triggers { get; init; } = [];

    public IEnumerable<DbObjectName> All()
        => Tables.Concat(Views).Concat(Sequences).Concat(Functions).Concat(Procedures).Concat(TableTypes)
            .Concat(Synonyms).Concat(Triggers);

    public IReadOnlyList<string> SchemaNames()
        => All().Select(x => x.Schema).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();

    public int Count => All().Count();
}
