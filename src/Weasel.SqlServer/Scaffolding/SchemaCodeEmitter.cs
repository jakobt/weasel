using JasperFx.Core;
using Weasel.Core;
using Weasel.SqlServer.Tables;

namespace Weasel.SqlServer.Scaffolding;

/// <remarks>
///     Everything emitted here must compile against the published Weasel.SqlServer package, so it may
///     only use public API. Store types are always explicit: <c>AddColumn&lt;decimal&gt;</c> maps through
///     <see cref="SqlServerProvider.GetDatabaseType" />, which would silently rewrite the column's type.
/// </remarks>
internal static class SchemaCodeEmitter
{
    public const string TriggerClassName = "ScaffoldedTrigger";

    public static string EmitTable(ScaffoldedTable scaffolded, ScaffoldEmissionOptions options, string className)
    {
        var table = scaffolded.Table;

        // CascadeAction is written fully qualified: Weasel.Core and Weasel.SqlServer both declare one.
        var writer = new CSharpSourceWriter()
            .UsingNamespace("Weasel.Core", "Weasel.SqlServer", "Weasel.SqlServer.Tables");

        writer.Block($"public partial class {className}: Table", t =>
        {
            t.Block($"public {className}(): base({objectName(table.Identifier)})",
                ctor =>
                {
                    foreach (var column in table.Columns)
                    {
                        writeColumn(ctor, column, scaffolded);
                    }

                    writePrimaryKey(ctor, scaffolded);
                    writeIndexes(ctor, table);
                    writeForeignKeys(ctor, table);
                    writeCheckConstraints(ctor, table);
                });
        });

        return writer.ToSourceFile(options.Namespace);
    }

    private static void writeColumn(CSharpSourceWriter writer, TableColumn column, ScaffoldedTable scaffolded)
    {
        var expression = $"AddColumn({CSharpSourceWriter.Quote(column.Name)}, {CSharpSourceWriter.Quote(scaffolded.TypeFor(column))})";

        if (column.ComputedExpression.IsNotEmpty())
        {
            writer.Line(
                $"{expression}.ComputedAs({CSharpSourceWriter.Quote(column.ComputedExpression!)}, {CSharpSourceWriter.Literal(column.ComputedColumnIsStored)});");
            return;
        }

        if (column.IsPrimaryKey)
        {
            expression += ".AsPrimaryKey()";
        }
        else
        {
            expression += column.AllowNulls ? ".AllowNulls()" : ".NotNull()";
        }

        if (column.IsAutoNumber)
        {
            expression += ".AutoIncrement()";
        }

        if (column.DefaultExpression.IsNotEmpty())
        {
            expression += $".DefaultValueByExpression({CSharpSourceWriter.Quote(column.DefaultExpression!)})";
        }

        writer.Line(expression + ";");
    }

    private static void writePrimaryKey(CSharpSourceWriter writer, ScaffoldedTable scaffolded)
    {
        var table = scaffolded.Table;
        if (!table.PrimaryKeyColumns.Any())
        {
            return;
        }

        writer.Line();

        if (scaffolded.EmitPrimaryKeyName)
        {
            writer.Line($"PrimaryKeyName = {CSharpSourceWriter.Quote(table.PrimaryKeyName)};");
        }

        var derived = table.Columns.Where(x => x.IsPrimaryKey).Select(x => x.Name).ToArray();
        if (!derived.SequenceEqual(table.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase))
        {
            writer.Line($"SetPrimaryKeyOrder({CSharpSourceWriter.StringArray(table.PrimaryKeyColumns)});");
        }
    }

    private static void writeIndexes(CSharpSourceWriter writer, Table table)
    {
        foreach (var ignored in table.IgnoredIndexes.OrderBy(x => x, StringComparer.Ordinal))
        {
            writer.Line();
            writer.Line($"IgnoreIndex({CSharpSourceWriter.Quote(ignored)});");
        }

        foreach (var index in table.Indexes.OfType<IndexDefinition>())
        {
            writer.Line();

            var initializers = new List<string> { $"Columns = {CSharpSourceWriter.StringArray(index.Columns)}" };

            if (index.IncludedColumns.Any())
            {
                initializers.Add($"IncludedColumns = {CSharpSourceWriter.StringArray(index.IncludedColumns)}");
            }

            if (index.IsUnique)
            {
                initializers.Add("IsUnique = true");
            }

            if (index.IsClustered)
            {
                initializers.Add("IsClustered = true");
            }

            if (index.DescendingColumns.Any())
            {
                var descending = index.Columns.Where(index.DescendingColumns.Contains);
                initializers.Add("SortOrder = SortOrder.Desc");
                initializers.Add(
                    $"DescendingColumns = {{ {descending.Select(CSharpSourceWriter.Quote).Join(", ")} }}");
            }

            if (index.Predicate.IsNotEmpty())
            {
                initializers.Add($"Predicate = {CSharpSourceWriter.Quote(index.Predicate!)}");
            }

            if (index.FillFactor is > 0)
            {
                initializers.Add($"FillFactor = {CSharpSourceWriter.Literal(index.FillFactor.Value)}");
            }

            writer.Line(
                $"Indexes.Add(new IndexDefinition({CSharpSourceWriter.Quote(index.Name)}) {{ {initializers.Join(", ")} }});");
        }
    }

    private static void writeForeignKeys(CSharpSourceWriter writer, Table table)
    {
        foreach (var fk in table.ForeignKeys)
        {
            writer.Line();
            writer.Line($"ForeignKeys.Add(new ForeignKey({CSharpSourceWriter.Quote(fk.Name)})");
            writer.Block("", body =>
            {
                body.Line($"LinkedTable = {objectName(fk.LinkedTable!)},");
                body.Line($"ColumnNames = {CSharpSourceWriter.StringArray(fk.ColumnNames)},");
                body.Line($"LinkedNames = {CSharpSourceWriter.StringArray(fk.LinkedNames)},");
                body.Line($"DeleteAction = Weasel.Core.CascadeAction.{fk.DeleteAction},");
                body.Line($"UpdateAction = Weasel.Core.CascadeAction.{fk.UpdateAction}");
            }, "});");
        }
    }

    private static void writeCheckConstraints(CSharpSourceWriter writer, Table table)
    {
        foreach (var check in table.CheckConstraints)
        {
            writer.Line();
            writer.Line(
                $"CheckConstraints.Add(new TableCheckConstraint({CSharpSourceWriter.Quote(check.Name)}, {CSharpSourceWriter.Quote(check.Expression)}));");
        }
    }

    public static string EmitObjectFile(string className, string kindType, string body,
        ScaffoldEmissionOptions options, params string[] extraUsings)
    {
        var writer = new CSharpSourceWriter()
            .UsingNamespace("Weasel.Core", "Weasel.SqlServer", "Weasel.SqlServer.Tables");

        foreach (var ns in extraUsings)
        {
            writer.UsingNamespace(ns);
        }

        writer.Block($"public static class {className}", c =>
        {
            c.Line($"public static {kindType} Build()");
            foreach (var line in body.Split('\n'))
            {
                c.Line("    " + line);
            }
        });

        return writer.ToSourceFile(options.Namespace);
    }

    public static string ViewBody(Views.View view, string resource)
        => $"=> new View({objectName(view.Identifier)}, ScaffoldedSql.Read({CSharpSourceWriter.Quote(resource)}));";

    public static string SequenceBody(Sequence sequence)
    {
        var properties = new List<string>();
        if (sequence.StartWith.HasValue)
        {
            properties.Add($"StartWith = {CSharpSourceWriter.Literal(sequence.StartWith.Value)}");
        }

        if (sequence.IncrementBy.HasValue)
        {
            properties.Add($"IncrementBy = {CSharpSourceWriter.Literal(sequence.IncrementBy.Value)}");
        }

        return properties.Count == 0
            ? $"=> new Sequence({objectName(sequence.Identifier)});"
            : $"=> new Sequence({objectName(sequence.Identifier)}) {{ {properties.Join(", ")} }};";
    }

    public static string SynonymBody(Synonyms.Synonym synonym)
        => $"=> new Synonym({objectName(synonym.Identifier)}, {CSharpSourceWriter.Quote(synonym.Target)});";

    public static string FunctionBody(SqlModule module, string resource)
        => $"=> new Function({objectName(module.Identifier)}, ScaffoldedSql.Read({CSharpSourceWriter.Quote(resource)}));";

    public static string ProcedureBody(SqlModule module, string resource)
        => $"=> new StoredProcedure({objectName(module.Identifier)}, ScaffoldedSql.Read({CSharpSourceWriter.Quote(resource)}));";

    public static string TriggerBody(ScaffoldedTrigger trigger, string resource)
        => $"=> new {TriggerClassName}({objectName(trigger.Identifier)}, {objectName(trigger.Target)},\n"
           + $"    ScaffoldedSql.Read({CSharpSourceWriter.Quote(resource)}),\n"
           + $"    {CSharpSourceWriter.Quote(trigger.Header)}, isDisabled: {CSharpSourceWriter.Literal(trigger.IsDisabled)})\n"
           + "{\n"
           + $"    Timing = TriggerTiming.{trigger.Timing},\n"
           + $"    Events = {eventFlags(trigger.Events)}\n"
           + "};";

    private static string eventFlags(TriggerEvents events)
        => Enum.GetValues<TriggerEvents>()
            .Where(x => x != TriggerEvents.None && events.HasFlag(x))
            .Select(x => $"TriggerEvents.{x}")
            .Join(" | ");

    /// <summary>
    ///     A <see cref="Triggers.Trigger" /> that also treats the source database's own spelling of the
    ///     trigger as unchanged. The published Trigger compares the stored text against its own
    ///     rendering, so a trigger written <c>FOR INSERT</c> or with bracketed names would otherwise
    ///     report drift against the very database it was scaffolded from. It also reproduces a
    ///     disabled trigger, which the published model has no flag for.
    /// </summary>
    public static string EmitTriggerClass(ScaffoldEmissionOptions options)
    {
        var writer = new CSharpSourceWriter()
            .UsingNamespace("System.Data.Common", "System.IO", "System.Threading", "System.Threading.Tasks",
                "Weasel.Core", "Weasel.SqlServer.Triggers");

        writer.Block(
            $"public class {TriggerClassName}(DbObjectName identifier, DbObjectName target, string body, string sourceHeader, bool isDisabled): Trigger(identifier, target, body)",
            c =>
            {
                c.Line("public bool IsDisabled { get; } = isDisabled;");
                c.Line();
                c.Block("public override void WriteCreateStatement(Migrator migrator, TextWriter writer)", m =>
                {
                    m.Line("base.WriteCreateStatement(migrator, writer);");
                    m.Line("if (IsDisabled)");
                    m.Line("{");
                    m.Line("    writer.WriteLine($\"DISABLE TRIGGER {Identifier} ON {Target};\");");
                    m.Line("}");
                });
                c.Line();
                c.Block("public override async Task<ISchemaObjectDelta> CreateDeltaAsync(DbDataReader reader, CancellationToken ct = default)", m =>
                {
                    m.Line("if (!await reader.ReadAsync(ct) || await reader.IsDBNullAsync(0, ct))");
                    m.Line("{");
                    m.Line("    return new SchemaObjectDelta(this, SchemaPatchDifference.Create);");
                    m.Line("}");
                    m.Line();
                    m.Line("var existing = NormalizeBody(await reader.GetFieldValueAsync<string>(0, ct));");
                    m.Line("if (existing.Length == 0)");
                    m.Line("{");
                    m.Line("    return new SchemaObjectDelta(this, SchemaPatchDifference.Create);");
                    m.Line("}");
                    m.Line();
                    m.Line("return existing == NormalizeBody(CreateStatement()) || existing == NormalizeBody(sourceHeader + Body)");
                    m.Line("    ? new SchemaObjectDelta(this, SchemaPatchDifference.None)");
                    m.Line("    : new SchemaObjectDelta(this, SchemaPatchDifference.Update);");
                });
            });

        return writer.ToSourceFile(options.Namespace);
    }

    public static string TableTypeBody(TableType tableType)
    {
        var lines = new List<string> { "{", $"    var tableType = new TableType({objectName(tableType.Identifier)});" };
        foreach (var column in tableType.Columns)
        {
            var nullability = column.AllowNulls ? "AllowNulls()" : "NotNull()";
            lines.Add(
                $"    tableType.AddColumn({CSharpSourceWriter.Quote(column.Name)}, {CSharpSourceWriter.Quote(column.DatabaseType)}).{nullability};");
        }

        lines.Add("    return tableType;");
        lines.Add("}");
        return string.Join("\n", lines);
    }

    public static string EmitObjectRegistry(IReadOnlyList<string> factoryClassNames, ScaffoldEmissionOptions options)
    {
        var writer = new CSharpSourceWriter()
            .UsingNamespace("System.Collections.Generic", "Weasel.Core", "Weasel.Core.Migrations");

        writer.Block($"public static class {options.OtherObjectsClassName}", c =>
        {
            c.Block("public static IEnumerable<ISchemaObject> All()", m =>
            {
                foreach (var name in factoryClassNames)
                {
                    m.Line($"yield return {name}.Build();");
                }
            });
        });

        writer.Line();
        writer.Block($"public class {options.OtherObjectsClassName}Feature: FeatureSchemaBase", c =>
        {
            c.Line($"public {options.OtherObjectsClassName}Feature(Migrator migrator)");
            c.Line($"    : base({CSharpSourceWriter.Quote(options.OtherObjectsClassName)}, migrator)");
            c.Block("", _ => { });
            c.Line();
            c.Line("protected override IEnumerable<ISchemaObject> schemaObjects()");
            c.Line($"    => {options.OtherObjectsClassName}.All();");
        });

        return writer.ToSourceFile(options.Namespace);
    }

    /// <remarks>
    ///     Module bodies are embedded resources rather than C# literals because a real database exceeds
    ///     the CLR's per-assembly user-string limit (CS8103).
    /// </remarks>
    public static string EmitSqlReader(ScaffoldEmissionOptions options)
    {
        var writer = new CSharpSourceWriter()
            .UsingNamespace("System", "System.IO", "System.Reflection");

        writer.Block("internal static class ScaffoldedSql", c =>
        {
            c.Line("private static readonly Assembly Assembly = typeof(ScaffoldedSql).Assembly;");
            c.Line();
            c.Block("public static string Read(string resourceName)", m =>
            {
                m.Line($"var full = {CSharpSourceWriter.Quote(options.Namespace + ".")} + resourceName;");
                m.Line("using var stream = Assembly.GetManifestResourceStream(full)");
                m.Line("    ?? throw new InvalidOperationException($\"Embedded SQL resource '{full}' was not found.\");");
                m.Line("using var reader = new StreamReader(stream);");
                m.Line("return reader.ReadToEnd();");
            });
        });

        return writer.ToSourceFile(options.Namespace);
    }

    public static string EmitProjectFile(ScaffoldEmissionOptions options)
        => $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>{options.TargetFramework}</TargetFramework>
                <Nullable>disable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <RootNamespace>{options.Namespace}</RootNamespace>
                <NoWarn>$(NoWarn);CS1591</NoWarn>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Weasel.SqlServer" Version="{options.WeaselVersion}" />
              </ItemGroup>

              <ItemGroup>
                <EmbeddedResource Include="**\*.sql" />
              </ItemGroup>

            </Project>

            """;

    private static string objectName(DbObjectName name)
        => $"new SqlServerObjectName({CSharpSourceWriter.Quote(name.Schema)}, {CSharpSourceWriter.Quote(name.Name)})";
}
