using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Weasel.Core;
using Weasel.SqlServer.Synonyms;
using Weasel.SqlServer.Tables;
using Weasel.SqlServer.Views;

namespace Weasel.SqlServer.Scaffolding;

public sealed record ScaffoldResult
{
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<ScaffoldNote> Notes { get; init; } = [];

    public int TableCount { get; init; }

    public int OtherObjectCount { get; init; }
}

/// <remarks>
///     Tables are hydrated through <see cref="Table.FetchExistingAsync" />, the reader delta detection
///     uses, so the generated model matches what <c>db-assert</c> will later read back.
/// </remarks>
public static class SqlServerScaffolder
{
    private static readonly SqlServerMigrator Validator = new();

    public static async Task<ScaffoldResult> ScaffoldAsync(
        SqlConnection conn,
        SqlServerCatalogFilter? filter = null,
        ScaffoldEmissionOptions? options = null,
        CancellationToken ct = default
    )
    {
        options ??= new ScaffoldEmissionOptions();

        var catalog = await conn.ReadCatalogAsync(filter, ct).ConfigureAwait(false);
        var metadata = await ScaffoldMetadata.ReadAsync(conn, ct).ConfigureAwait(false);

        var schemas = catalog.SchemaNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notes = metadata.Notes
            .Where(x => x.Object.StartsWith("(") || schemas.Contains(x.Object.Split('.')[0]))
            .ToList();

        var tables = new List<ScaffoldedTable>();
        foreach (var name in catalog.Tables)
        {
            var table = await new Table(name).FetchExistingAsync(conn, ct).ConfigureAwait(false);
            if (table != null)
            {
                tables.Add(prepare(table, metadata, notes));
            }
        }

        var ordered = SortByDependency(tables.Select(x => x.Table).ToList())
            .Select(t => tables.Single(x => ReferenceEquals(x.Table, t)))
            .ToList();

        var others = await hydrateOtherObjectsAsync(conn, catalog, metadata, notes, ct).ConfigureAwait(false);

        var naming = ScaffoldNaming.Build(options);
        naming.Allocate(ordered.Select(x => x.Table.Identifier), ScaffoldKind.Table);
        naming.Allocate(others.Views.Select(x => x.Identifier), ScaffoldKind.View);
        naming.Allocate(others.Sequences.Select(x => x.Identifier), ScaffoldKind.Sequence);
        naming.Allocate(others.Synonyms.Select(x => x.Identifier), ScaffoldKind.Synonym);
        naming.Allocate(others.Functions.Select(x => x.Identifier), ScaffoldKind.Function);
        naming.Allocate(others.Procedures.Select(x => x.Identifier), ScaffoldKind.Procedure);
        naming.Allocate(others.TableTypes.Select(x => x.Identifier), ScaffoldKind.TableType);
        naming.Allocate(others.Triggers.Select(x => x.Identifier), ScaffoldKind.Trigger);

        var files = new Dictionary<string, string>();

        foreach (var table in ordered)
        {
            var className = naming[table.Table.Identifier, ScaffoldKind.Table];
            files[$"{ScaffoldKind.Table.Folder}/{className}.cs"] =
                SchemaCodeEmitter.EmitTable(table, options, className);
        }

        var factories = new List<(DbObjectName Identifier, ScaffoldKind Kind, string ClassName)>();

        void emit(DbObjectName identifier, ScaffoldKind kind, string returnType, string body, string? sql,
            params string[] usings)
        {
            var className = naming[identifier, kind];
            if (sql != null)
            {
                files[$"{kind.Folder}/{className}.sql"] = sql;
            }

            files[$"{kind.Folder}/{className}.cs"] =
                SchemaCodeEmitter.EmitObjectFile(className, returnType, body, options, usings);
            factories.Add((identifier, kind, className));
        }

        string resource(DbObjectName identifier, ScaffoldKind kind) => $"{kind.Folder}.{naming[identifier, kind]}.sql";

        foreach (var sequence in others.Sequences)
        {
            emit(sequence.Identifier, ScaffoldKind.Sequence, "Sequence", SchemaCodeEmitter.SequenceBody(sequence), null);
        }

        foreach (var synonym in others.Synonyms)
        {
            emit(synonym.Identifier, ScaffoldKind.Synonym, "Synonym", SchemaCodeEmitter.SynonymBody(synonym), null,
                "Weasel.SqlServer.Synonyms");
        }

        foreach (var tableType in others.TableTypes)
        {
            emit(tableType.Identifier, ScaffoldKind.TableType, "TableType", SchemaCodeEmitter.TableTypeBody(tableType), null);
        }

        foreach (var view in others.Views)
        {
            emit(view.Identifier, ScaffoldKind.View, "View",
                SchemaCodeEmitter.ViewBody(view, resource(view.Identifier, ScaffoldKind.View)), view.ViewSql,
                "Weasel.SqlServer.Views");
        }

        foreach (var function in others.Functions)
        {
            emit(function.Identifier, ScaffoldKind.Function, "Function",
                SchemaCodeEmitter.FunctionBody(function, resource(function.Identifier, ScaffoldKind.Function)),
                function.CreateSql, "Weasel.SqlServer.Functions");
        }

        foreach (var procedure in others.Procedures)
        {
            emit(procedure.Identifier, ScaffoldKind.Procedure, "StoredProcedure",
                SchemaCodeEmitter.ProcedureBody(procedure, resource(procedure.Identifier, ScaffoldKind.Procedure)),
                procedure.CreateSql, "Weasel.SqlServer.Procedures");
        }

        foreach (var trigger in others.Triggers)
        {
            emit(trigger.Identifier, ScaffoldKind.Trigger, "Trigger",
                SchemaCodeEmitter.TriggerBody(trigger, resource(trigger.Identifier, ScaffoldKind.Trigger)),
                trigger.Body, "Weasel.SqlServer.Triggers");
        }

        if (others.Triggers.Any())
        {
            files[$"{SchemaCodeEmitter.TriggerClassName}.cs"] = SchemaCodeEmitter.EmitTriggerClass(options);
        }

        var creationOrder = OrderByDependency(factories, metadata.Dependencies);

        if (creationOrder.Count > 0)
        {
            files[$"{options.OtherObjectsClassName}.cs"] =
                SchemaCodeEmitter.EmitObjectRegistry(creationOrder, options);
            files["ScaffoldedSql.cs"] = SchemaCodeEmitter.EmitSqlReader(options);
        }

        files[$"{options.DatabaseClassName}.cs"] = emitDatabase(ordered, naming, creationOrder.Count > 0, options);

        if (options.EmitProjectFile)
        {
            files[$"{options.ResolvedProjectName}.csproj"] = SchemaCodeEmitter.EmitProjectFile(options);
        }

        return new ScaffoldResult
        {
            Files = files,
            Notes = notes.OrderBy(x => x.Category, StringComparer.Ordinal)
                .ThenBy(x => x.Object, StringComparer.OrdinalIgnoreCase).ToList(),
            TableCount = ordered.Count,
            OtherObjectCount = others.Count
        };
    }

    private static string? identifierProblem(string name)
    {
        try
        {
            Validator.AssertValidLocalIdentifier(name);
            return null;
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }
    }

    private static ScaffoldedTable prepare(Table table, ScaffoldMetadata metadata, List<ScaffoldNote> notes)
    {
        var scaffolded = new ScaffoldedTable(table);
        var key = ScaffoldMetadata.Key(table.Identifier);
        var qualified = table.Identifier.QualifiedName;

        if (metadata.Collations.TryGetValue(key, out var collations))
        {
            foreach (var (name, collation) in collations)
            {
                if (table.ColumnFor(name) is not { ComputedExpression: null } column)
                {
                    continue;
                }

                if (column.Type.Contains('('))
                {
                    scaffolded.Collations[name] = collation;
                }
                else
                {
                    notes.Add(new ScaffoldNote("column COLLATE on a type without a length is not carried",
                        $"{qualified}.{name} {column.Type} COLLATE {collation}"));
                }
            }
        }

        if (metadata.ClusteredIndexesBesideNonclusteredKey.TryGetValue(key, out var clustered))
        {
            foreach (var name in clustered)
            {
                ignoreIndex(table, name);
                notes.Add(new ScaffoldNote(
                    "CLUSTERED index beside a NONCLUSTERED primary key is left unmanaged (the published model cannot make the key NONCLUSTERED)",
                    $"{qualified}.{name}"));
            }
        }

        foreach (var (_, name, type) in metadata.UnmodelledIndexes.Where(x => x.Table.EqualsIgnoreCase(key)))
        {
            ignoreIndex(table, name);
            notes.Add(new ScaffoldNote($"{type} index is left unmanaged", $"{qualified}.{name}"));
        }

        foreach (var index in table.Indexes.ToArray())
        {
            if (identifierProblem(index.Name) is { } problem)
            {
                ignoreIndex(table, index.Name);
                notes.Add(new ScaffoldNote("index whose name Weasel refuses is left unmanaged", $"{qualified}.{index.Name} ({problem})"));
            }
        }

        foreach (var index in table.Indexes.Where(x => x is not IndexDefinition).ToArray())
        {
            ignoreIndex(table, index.Name);
            notes.Add(new ScaffoldNote("JSON index is left unmanaged", $"{qualified}.{index.Name}"));
        }

        if (table.PrimaryKeyColumns.Any() && identifierProblem(table.PrimaryKeyName) is { } pkProblem)
        {
            scaffolded.EmitPrimaryKeyName = false;
            notes.Add(new ScaffoldNote("PRIMARY KEY whose name Weasel refuses is recreated under Weasel's default name",
                $"{qualified}.{table.PrimaryKeyName} ({pkProblem})"));
        }

        foreach (var fk in table.ForeignKeys.ToArray())
        {
            if (identifierProblem(fk.Name) is { } problem)
            {
                table.ForeignKeys.Remove(fk);
                notes.Add(new ScaffoldNote("FOREIGN KEY whose name Weasel refuses is not scaffolded", $"{qualified}.{fk.Name} ({problem})"));
            }
        }

        foreach (var check in table.CheckConstraints.ToArray())
        {
            if (identifierProblem(check.Name) is { } problem)
            {
                table.CheckConstraints.Remove(check);
                notes.Add(new ScaffoldNote("CHECK constraint whose name Weasel refuses is not scaffolded", $"{qualified}.{check.Name} ({problem})"));
            }
        }

        return scaffolded;
    }

    private static void ignoreIndex(Table table, string name)
    {
        foreach (var index in table.Indexes.Where(x => x.Name.EqualsIgnoreCase(name)).ToArray())
        {
            table.Indexes.Remove(index);
        }

        table.IgnoreIndex(name);
    }

    private static async Task<ScaffoldedObjects> hydrateOtherObjectsAsync(
        SqlConnection conn,
        SqlServerCatalog catalog,
        ScaffoldMetadata metadata,
        List<ScaffoldNote> notes,
        CancellationToken ct)
    {
        var views = new List<View>();
        foreach (var name in catalog.Views)
        {
            var view = await View.FetchExistingAsync(conn, name, ct).ConfigureAwait(false);
            if (view != null)
            {
                views.Add(view);
            }
        }

        var sequences = metadata.Sequences
            .Where(x => catalog.Sequences.Any(n => sameName(n, x.Identifier)))
            .Select(x => new Sequence(x.Identifier) { StartWith = x.StartWith, IncrementBy = x.IncrementBy })
            .ToList();

        var synonyms = new List<Synonym>();
        foreach (var name in catalog.Synonyms)
        {
            var target = await new Synonym(name, "unknown").FetchExistingTargetAsync(conn, ct).ConfigureAwait(false);
            if (target != null)
            {
                synonyms.Add(new Synonym(name, target));
            }
        }

        // Read straight from sys.sql_modules: Function.FetchExistingAsync hands back the rendered
        // create statement, already wrapped in sp_executesql, and feeding that to new Function(...)
        // wraps it twice.
        var functions = new List<SqlModule>();
        foreach (var name in catalog.Functions)
        {
            var body = await readModuleDefinitionAsync(conn, name, ct).ConfigureAwait(false);
            if (body.IsNotEmpty())
            {
                functions.Add(new SqlModule(name, body!));
            }
        }

        var procedures = new List<SqlModule>();
        foreach (var name in catalog.Procedures)
        {
            var body = await readModuleDefinitionAsync(conn, name, ct).ConfigureAwait(false);
            if (body.IsEmpty())
            {
                continue;
            }

            if (SqlServerBatchSplitter.ContainsSeparator(body!))
            {
                notes.Add(new ScaffoldNote("stored procedure with a line consisting only of GO is not scaffolded",
                    name.QualifiedName));
                continue;
            }

            procedures.Add(new SqlModule(name, body!));
        }

        var tableTypes = new List<TableType>();
        foreach (var name in catalog.TableTypes)
        {
            var tableType = await new TableType(name).FetchExistingAsync(conn, ct).ConfigureAwait(false);
            if (tableType != null)
            {
                tableTypes.Add(tableType);
            }
        }

        var triggers = new List<ScaffoldedTrigger>();
        foreach (var row in metadata.Triggers.Where(x => catalog.Triggers.Any(n => sameName(n, x.Identifier))))
        {
            if (row.Definition == null)
            {
                continue;
            }

            var parsed = TriggerDefinitionParser.Parse(row.Definition, out var problem);
            if (parsed == null)
            {
                notes.Add(new ScaffoldNote("trigger is not scaffolded", $"{row.Identifier.QualifiedName}: {problem}"));
                continue;
            }

            triggers.Add(new ScaffoldedTrigger(row.Identifier, row.Target, parsed.Timing, parsed.Events, parsed.Header, parsed.Body,
                row.IsDisabled));
        }

        return new ScaffoldedObjects
        {
            Views = views,
            Sequences = sequences,
            Synonyms = synonyms,
            Functions = functions,
            Procedures = procedures,
            TableTypes = tableTypes,
            Triggers = triggers
        };
    }

    private static bool sameName(DbObjectName a, DbObjectName b)
        => a.Schema.EqualsIgnoreCase(b.Schema) && a.Name.EqualsIgnoreCase(b.Name);

    /// <remarks>
    ///     Returned verbatim: module bodies are compared as text, and trimming a trailing blank line is
    ///     enough to report the object as changed.
    /// </remarks>
    private static async Task<string?> readModuleDefinitionAsync(SqlConnection conn, DbObjectName name,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
select sm.definition
from sys.sql_modules sm
    inner join sys.objects o on o.object_id = sm.object_id
    inner join sys.schemas s on s.schema_id = o.schema_id
where o.name = @name and s.name = @schema;";
        cmd.Parameters.AddWithValue("name", name.Name);
        cmd.Parameters.AddWithValue("schema", name.Schema);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string;
    }

    private static readonly ScaffoldKind[] KindOrder =
    [
        ScaffoldKind.Sequence, ScaffoldKind.Synonym, ScaffoldKind.TableType, ScaffoldKind.Function,
        ScaffoldKind.View, ScaffoldKind.Procedure, ScaffoldKind.Trigger
    ];

    /// <remarks>
    ///     A cycle leaves the rest in kind-then-name order rather than failing.
    /// </remarks>
    internal static IReadOnlyList<string> OrderByDependency(
        IReadOnlyList<(DbObjectName Identifier, ScaffoldKind Kind, string ClassName)> objects,
        IReadOnlyList<(DbObjectName Referencing, DbObjectName Referenced)> dependencies)
    {
        var byName = objects
            .GroupBy(x => ScaffoldMetadata.Key(x.Identifier), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var pending = objects.ToDictionary(x => x.ClassName, _ => 0);
        var dependants = objects.ToDictionary(x => x.ClassName, _ => new List<string>());

        foreach (var (referencing, referenced) in dependencies)
        {
            if (!byName.TryGetValue(ScaffoldMetadata.Key(referencing), out var from) ||
                !byName.TryGetValue(ScaffoldMetadata.Key(referenced), out var to))
            {
                continue;
            }

            foreach (var f in from)
            foreach (var t in to.Where(t => t.ClassName != f.ClassName))
            {
                dependants[t.ClassName].Add(f.ClassName);
                pending[f.ClassName]++;
            }
        }

        var rank = objects.ToDictionary(x => x.ClassName,
            x => (Array.IndexOf(KindOrder, x.Kind), x.Identifier.QualifiedName));
        var comparer = Comparer<(int, string)>.Create((a, b) =>
        {
            var byKind = a.Item1.CompareTo(b.Item1);
            return byKind != 0 ? byKind : StringComparer.OrdinalIgnoreCase.Compare(a.Item2, b.Item2);
        });

        var ready = new PriorityQueue<string, (int, string)>(comparer);
        foreach (var o in objects.Where(x => pending[x.ClassName] == 0))
        {
            ready.Enqueue(o.ClassName, rank[o.ClassName]);
        }

        var ordered = new List<string>();
        var done = new HashSet<string>();
        while (ordered.Count < objects.Count)
        {
            if (ready.Count == 0)
            {
                var next = objects.Where(x => !done.Contains(x.ClassName)).MinBy(x => rank[x.ClassName], comparer);
                ready.Enqueue(next.ClassName, rank[next.ClassName]);
            }

            var name = ready.Dequeue();
            if (!done.Add(name))
            {
                continue;
            }

            ordered.Add(name);
            foreach (var dependant in dependants[name])
            {
                if (--pending[dependant] == 0 && !done.Contains(dependant))
                {
                    ready.Enqueue(dependant, rank[dependant]);
                }
            }
        }

        return ordered;
    }

    private static string emitDatabase(
        IReadOnlyList<ScaffoldedTable> tables,
        ScaffoldNaming naming,
        bool hasOtherObjects,
        ScaffoldEmissionOptions options)
    {
        var writer = new CSharpSourceWriter()
            .UsingNamespace("JasperFx", "Weasel.Core.Migrations", "Weasel.SqlServer");

        writer.Block($"public partial class {options.DatabaseClassName}: DatabaseWithTables", t =>
        {
            t.Line(
                $"public {options.DatabaseClassName}(string connectionString, AutoCreate autoCreate = AutoCreate.CreateOrUpdate)");
            t.Line($"    : base({CSharpSourceWriter.Quote(options.DatabaseClassName)}, connectionString, autoCreate)");
            t.Block("", body =>
            {
                foreach (var table in tables)
                {
                    body.Line($"AddTable(new {naming[table.Table.Identifier, ScaffoldKind.Table]}());");
                }
            });

            if (!hasOtherObjects)
            {
                return;
            }

            t.Line();
            t.Line("public override IFeatureSchema[] BuildFeatureSchemas()");
            t.Line($"    => [..base.BuildFeatureSchemas(), new {options.OtherObjectsClassName}Feature(Migrator)];");
        });

        return writer.ToSourceFile(options.Namespace);
    }

    internal static IReadOnlyList<Table> SortByDependency(IReadOnlyList<Table> tables)
    {
        var byName = tables.ToDictionary(x => x.Identifier.QualifiedName, StringComparer.OrdinalIgnoreCase);

        var dependencies = tables.ToDictionary(
            t => t.Identifier.QualifiedName,
            t => t.ForeignKeys
                .Select(fk => fk.LinkedTable?.QualifiedName)
                .Where(name => name != null
                               && byName.ContainsKey(name)
                               && !name.EqualsIgnoreCase(t.Identifier.QualifiedName))
                .Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var sorted = new List<Table>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = new List<Table>(tables);

        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(t => dependencies[t.Identifier.QualifiedName].All(done.Contains))
                .ToArray();

            if (ready.Length == 0)
            {
                sorted.AddRange(remaining);
                break;
            }

            sorted.AddRange(ready);
            foreach (var table in ready)
            {
                remaining.Remove(table);
                done.Add(table.Identifier.QualifiedName);
            }
        }

        return sorted;
    }
}
