using System.Data;
using System.Data.Common;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Weasel.Core;

namespace Weasel.SqlServer.Scaffolding;

public static class SqlServerSchemaCatalog
{
    private const string FunctionTypes = "'FN', 'IF', 'TF', 'FS', 'FT'";

    public static async Task<SqlServerCatalog> ReadCatalogAsync(
        this SqlConnection conn,
        SqlServerCatalogFilter? filter = null,
        CancellationToken ct = default
    )
    {
        filter ??= new SqlServerCatalogFilter();

        var shouldClose = false;
        if (conn.State != ConnectionState.Open)
        {
            shouldClose = true;
            await conn.OpenAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var builder = new CommandBuilder();

            var schemas = schemaPredicate(builder, filter);
            var shipped = filter.IncludeSystemObjects ? "" : " and {0}.is_ms_shipped = 0";

            builder.Append($@"
select s.name, t.name from sys.tables t
    inner join sys.schemas s on t.schema_id = s.schema_id
    where 1 = 1{string.Format(shipped, "t")}{schemas}{namePredicate(builder, filter, "t")}{excludedTablePredicate(builder, filter)}
    order by s.name, t.name;

select s.name, v.name from sys.views v
    inner join sys.schemas s on v.schema_id = s.schema_id
    where 1 = 1{string.Format(shipped, "v")}{schemas}{namePredicate(builder, filter, "v")}
    order by s.name, v.name;

select s.name, q.name from sys.sequences q
    inner join sys.schemas s on q.schema_id = s.schema_id
    where 1 = 1{string.Format(shipped, "q")}{schemas}{namePredicate(builder, filter, "q")}
    order by s.name, q.name;

select s.name, o.name from sys.objects o
    inner join sys.schemas s on o.schema_id = s.schema_id
    where o.type in ({FunctionTypes}){string.Format(shipped, "o")}{schemas}{namePredicate(builder, filter, "o")}
    order by s.name, o.name;

select s.name, p.name from sys.procedures p
    inner join sys.schemas s on p.schema_id = s.schema_id
    where 1 = 1{string.Format(shipped, "p")}{schemas}{namePredicate(builder, filter, "p")}
    order by s.name, p.name;

select s.name, tt.name from sys.table_types tt
    inner join sys.schemas s on tt.schema_id = s.schema_id
    where tt.is_user_defined = 1{schemas}{namePredicate(builder, filter, "tt")}
    order by s.name, tt.name;

select s.name, sy.name from sys.synonyms sy
    inner join sys.schemas s on sy.schema_id = s.schema_id
    where 1 = 1{string.Format(shipped, "sy")}{schemas}{namePredicate(builder, filter, "sy")}
    order by s.name, sy.name;

-- A trigger's schema_id is 0; it lives in the schema of the table it is attached to.
select s.name, tr.name from sys.triggers tr
    inner join sys.tables t on t.object_id = tr.parent_id
    inner join sys.schemas s on s.schema_id = t.schema_id
    where tr.is_ms_shipped = 0{schemas}{namePredicate(builder, filter, "tr")}
    order by s.name, tr.name;
");

            var cmd = builder.Compile();
            cmd.Connection = conn;

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            var tables = await readNamesAsync(reader, ct).ConfigureAwait(false);
            var views = await readNextAsync(reader, ct).ConfigureAwait(false);
            var sequences = await readNextAsync(reader, ct).ConfigureAwait(false);
            var functions = await readNextAsync(reader, ct).ConfigureAwait(false);
            var procedures = await readNextAsync(reader, ct).ConfigureAwait(false);
            var tableTypes = await readNextAsync(reader, ct).ConfigureAwait(false);
            var synonyms = await readNextAsync(reader, ct).ConfigureAwait(false);
            var triggers = await readNextAsync(reader, ct).ConfigureAwait(false);

            return new SqlServerCatalog
            {
                Tables = tables,
                Views = views,
                Sequences = sequences,
                Functions = functions,
                Procedures = procedures,
                TableTypes = tableTypes,
                Synonyms = synonyms,
                Triggers = triggers
            };
        }
        finally
        {
            if (shouldClose)
            {
                await conn.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static string schemaPredicate(CommandBuilder builder, SqlServerCatalogFilter filter)
    {
        if (filter.IncludeSchemas is { Length: > 0 })
        {
            return $" and s.name in ({inList(builder, filter.IncludeSchemas, "incSchema")})";
        }

        return filter.ExcludeSchemas is { Length: > 0 }
            ? $" and s.name not in ({inList(builder, filter.ExcludeSchemas, "excSchema")})"
            : "";
    }

    private static string namePredicate(CommandBuilder builder, SqlServerCatalogFilter filter, string alias)
    {
        if (filter.NameLikePattern.IsEmpty())
        {
            return "";
        }

        builder.AddNamedParameter("namePattern", filter.NameLikePattern!);
        return $" and {alias}.name like @namePattern";
    }

    private static string excludedTablePredicate(CommandBuilder builder, SqlServerCatalogFilter filter)
        => filter.IncludeSystemObjects
            ? ""
            : $" and t.name not in ({inList(builder, SqlServerCatalogFilter.DefaultExcludedTables, "excTable")})";

    private static string inList(CommandBuilder builder, string[] values, string prefix)
    {
        var names = new List<string>(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var name = prefix + i;
            builder.AddNamedParameter(name, values[i]);
            names.Add("@" + name);
        }

        return names.Join(", ");
    }

    private static async Task<IReadOnlyList<DbObjectName>> readNextAsync(DbDataReader reader, CancellationToken ct)
    {
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        return await readNamesAsync(reader, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<DbObjectName>> readNamesAsync(DbDataReader reader, CancellationToken ct)
    {
        var names = new List<DbObjectName>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            names.Add(new SqlServerObjectName(
                await reader.GetFieldValueAsync<string>(0, ct).ConfigureAwait(false),
                await reader.GetFieldValueAsync<string>(1, ct).ConfigureAwait(false)
            ));
        }

        return names;
    }
}
