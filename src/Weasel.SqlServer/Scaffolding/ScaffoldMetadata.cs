using System.Data.Common;
using Microsoft.Data.SqlClient;
using Weasel.Core;

namespace Weasel.SqlServer.Scaffolding;

internal sealed record TriggerRow(DbObjectName Identifier, DbObjectName Target, bool IsDisabled, string? Definition);

internal sealed record SequenceRow(DbObjectName Identifier, long StartWith, long IncrementBy);

internal sealed class ScaffoldMetadata
{
    public Dictionary<string, Dictionary<string, string>> Collations { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> ClusteredIndexesBesideNonclusteredKey { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Table, string Index, string Type)> UnmodelledIndexes { get; } = [];
    public List<ScaffoldNote> Notes { get; } = [];
    public List<TriggerRow> Triggers { get; } = [];
    public List<SequenceRow> Sequences { get; } = [];
    public List<(DbObjectName Referencing, DbObjectName Referenced)> Dependencies { get; } = [];

    private const string Sql = @"
declare @collation sysname = convert(sysname, databasepropertyex(db_name(), 'Collation'));

select s.name, t.name, c.name, c.collation_name
from sys.columns c
    inner join sys.tables t on t.object_id = c.object_id
    inner join sys.schemas s on s.schema_id = t.schema_id
where c.collation_name is not null and c.collation_name <> @collation;

select s.name, t.name, i.name
from sys.indexes i
    inner join sys.tables t on t.object_id = i.object_id
    inner join sys.schemas s on s.schema_id = t.schema_id
where i.type_desc = 'CLUSTERED' and i.is_primary_key = 0
  and exists (select 1 from sys.indexes pk where pk.object_id = i.object_id and pk.is_primary_key = 1);

select s.name, t.name, i.name, i.type_desc
from sys.indexes i
    inner join sys.tables t on t.object_id = i.object_id
    inner join sys.schemas s on s.schema_id = t.schema_id
where i.type_desc not in ('HEAP', 'CLUSTERED', 'NONCLUSTERED', 'JSON');

select category, item from (
    select 'FOREIGN KEY created WITH NOCHECK is recreated as trusted' as category,
           s.name + '.' + t.name + '.' + fk.name as item
    from sys.foreign_keys fk inner join sys.tables t on t.object_id = fk.parent_object_id inner join sys.schemas s on s.schema_id = t.schema_id
    where fk.is_not_trusted = 1 and fk.is_disabled = 0
    union all
    select 'disabled FOREIGN KEY (NOCHECK CONSTRAINT) is recreated enabled', s.name + '.' + t.name + '.' + fk.name
    from sys.foreign_keys fk inner join sys.tables t on t.object_id = fk.parent_object_id inner join sys.schemas s on s.schema_id = t.schema_id
    where fk.is_disabled = 1
    union all
    select 'FOREIGN KEY NOT FOR REPLICATION is recreated without it', s.name + '.' + t.name + '.' + fk.name
    from sys.foreign_keys fk inner join sys.tables t on t.object_id = fk.parent_object_id inner join sys.schemas s on s.schema_id = t.schema_id
    where fk.is_not_for_replication = 1
    union all
    select 'CHECK constraint created WITH NOCHECK is recreated as trusted', s.name + '.' + t.name + '.' + cc.name
    from sys.check_constraints cc inner join sys.tables t on t.object_id = cc.parent_object_id inner join sys.schemas s on s.schema_id = t.schema_id
    where cc.is_not_trusted = 1 or cc.is_disabled = 1
    union all
    select 'NONCLUSTERED PRIMARY KEY is recreated CLUSTERED', s.name + '.' + t.name + '.' + i.name
    from sys.indexes i inner join sys.tables t on t.object_id = i.object_id inner join sys.schemas s on s.schema_id = t.schema_id
    where i.is_primary_key = 1 and i.type_desc <> 'CLUSTERED'
    union all
    select 'UNIQUE constraint is recreated as a unique index', s.name + '.' + t.name + '.' + kc.name
    from sys.key_constraints kc inner join sys.tables t on t.object_id = kc.parent_object_id inner join sys.schemas s on s.schema_id = t.schema_id
    where kc.type = 'UQ'
    union all
    select 'disabled index is recreated enabled', s.name + '.' + t.name + '.' + i.name
    from sys.indexes i inner join sys.tables t on t.object_id = i.object_id inner join sys.schemas s on s.schema_id = t.schema_id
    where i.is_disabled = 1
    union all
    select 'IGNORE_DUP_KEY index option is not carried', s.name + '.' + t.name + '.' + i.name
    from sys.indexes i inner join sys.tables t on t.object_id = i.object_id inner join sys.schemas s on s.schema_id = t.schema_id
    where i.ignore_dup_key = 1
    union all
    select 'ROWGUIDCOL is not carried', s.name + '.' + t.name + '.' + c.name
    from sys.columns c inner join sys.tables t on t.object_id = c.object_id inner join sys.schemas s on s.schema_id = t.schema_id
    where c.is_rowguidcol = 1
    union all
    select 'SPARSE is not carried', s.name + '.' + t.name + '.' + c.name
    from sys.columns c inner join sys.tables t on t.object_id = c.object_id inner join sys.schemas s on s.schema_id = t.schema_id
    where c.is_sparse = 1
    union all
    select 'IDENTITY seed and increment are recreated as (1,1)',
           s.name + '.' + t.name + '.' + c.name + ' IDENTITY(' + convert(varchar(40), c.seed_value) + ',' + convert(varchar(40), c.increment_value) + ')'
    from sys.identity_columns c inner join sys.tables t on t.object_id = c.object_id inner join sys.schemas s on s.schema_id = t.schema_id
    where convert(bigint, c.seed_value) <> 1 or convert(bigint, c.increment_value) <> 1
    union all
    select 'named DEFAULT constraint is recreated with a system-generated name', s.name + '.' + t.name + '.' + dc.name
    from sys.default_constraints dc inner join sys.tables t on t.object_id = dc.parent_object_id inner join sys.schemas s on s.schema_id = t.schema_id
    where dc.is_system_named = 0
    union all
    select 'view WITH SCHEMABINDING is recreated without it', s.name + '.' + v.name
    from sys.views v inner join sys.schemas s on s.schema_id = v.schema_id inner join sys.sql_modules m on m.object_id = v.object_id
    where m.is_schema_bound = 1
    union all
    select 'index on a view is not carried', s.name + '.' + v.name + '.' + i.name
    from sys.indexes i inner join sys.views v on v.object_id = i.object_id inner join sys.schemas s on s.schema_id = v.schema_id
    union all
    select 'module without a readable definition (WITH ENCRYPTION) is skipped', s.name + '.' + o.name
    from sys.objects o inner join sys.schemas s on s.schema_id = o.schema_id left join sys.sql_modules m on m.object_id = o.object_id
    where o.type in ('V', 'FN', 'IF', 'TF', 'P', 'TR') and o.is_ms_shipped = 0 and m.definition is null
    union all
    select 'table type key, index or constraint is not carried', s.name + '.' + tt.name + '.' + o.name
    from sys.table_types tt inner join sys.schemas s on s.schema_id = tt.schema_id inner join sys.objects o on o.parent_object_id = tt.type_table_object_id
    union all
    select 'extended properties are not carried', isnull(s.name + '.' + o.name, '(database)') + isnull('.' + c.name, '') + ' ' + ep.name
    from sys.extended_properties ep
        left join sys.objects o on ep.class = 1 and o.object_id = ep.major_id
        left join sys.schemas s on s.schema_id = o.schema_id
        left join sys.columns c on ep.class = 1 and c.object_id = ep.major_id and c.column_id = ep.minor_id and ep.minor_id > 0
    where ep.class in (0, 1) and (ep.class = 0 or o.is_ms_shipped = 0)
    union all
    select 'temporal (system-versioned) table is recreated as an ordinary table', s.name + '.' + t.name
    from sys.tables t inner join sys.schemas s on s.schema_id = t.schema_id
    where t.temporal_type <> 0
    union all
    select 'alias data type is not scaffolded; columns using it are emitted with its name', s.name + '.' + ty.name
    from sys.types ty inner join sys.schemas s on s.schema_id = ty.schema_id
    where ty.is_user_defined = 1 and ty.is_table_type = 0
) notes
order by category, item;

select ts.name, tr.name, ts.name, t.name, tr.is_disabled, m.definition
from sys.triggers tr
    inner join sys.tables t on t.object_id = tr.parent_id
    inner join sys.schemas ts on ts.schema_id = t.schema_id
    left join sys.sql_modules m on m.object_id = tr.object_id
where tr.is_ms_shipped = 0;

select s.name, q.name, convert(bigint, q.start_value), convert(bigint, q.increment)
from sys.sequences q
    inner join sys.schemas s on s.schema_id = q.schema_id;

select distinct
    isnull(object_schema_name(tr.parent_id), object_schema_name(d.referencing_id)), object_name(d.referencing_id),
    isnull(object_schema_name(rtr.parent_id), object_schema_name(d.referenced_id)), object_name(d.referenced_id)
from sys.sql_expression_dependencies d
    inner join sys.objects target on target.object_id = d.referenced_id
    left join sys.triggers tr on tr.object_id = d.referencing_id
    left join sys.triggers rtr on rtr.object_id = d.referenced_id
where d.referenced_id is not null and d.referencing_id <> d.referenced_id
  and d.referenced_class = 1;
";

    public static async Task<ScaffoldMetadata> ReadAsync(SqlConnection conn, CancellationToken ct)
    {
        var metadata = new ScaffoldMetadata();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var table = Key(reader.GetString(0), reader.GetString(1));
            if (!metadata.Collations.TryGetValue(table, out var columns))
            {
                columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                metadata.Collations[table] = columns;
            }

            columns[reader.GetString(2)] = reader.GetString(3);
        }

        await reader.NextResultAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var table = Key(reader.GetString(0), reader.GetString(1));
            if (!metadata.ClusteredIndexesBesideNonclusteredKey.TryGetValue(table, out var indexes))
            {
                indexes = [];
                metadata.ClusteredIndexesBesideNonclusteredKey[table] = indexes;
            }

            indexes.Add(reader.GetString(2));
        }

        await reader.NextResultAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            metadata.UnmodelledIndexes.Add((Key(reader.GetString(0), reader.GetString(1)), reader.GetString(2),
                reader.GetString(3)));
        }

        await reader.NextResultAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            metadata.Notes.Add(new ScaffoldNote(reader.GetString(0), reader.GetString(1)));
        }

        await reader.NextResultAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            metadata.Triggers.Add(new TriggerRow(
                new SqlServerObjectName(reader.GetString(0), reader.GetString(1)),
                new SqlServerObjectName(reader.GetString(2), reader.GetString(3)),
                reader.GetBoolean(4),
                await reader.IsDBNullAsync(5, ct).ConfigureAwait(false) ? null : reader.GetString(5)));
        }

        await reader.NextResultAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            metadata.Sequences.Add(new SequenceRow(
                new SqlServerObjectName(reader.GetString(0), reader.GetString(1)),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        await reader.NextResultAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (await anyNullAsync(reader, ct).ConfigureAwait(false))
            {
                continue;
            }

            metadata.Dependencies.Add((
                new SqlServerObjectName(reader.GetString(0), reader.GetString(1)),
                new SqlServerObjectName(reader.GetString(2), reader.GetString(3))));
        }

        return metadata;
    }

    private static async Task<bool> anyNullAsync(DbDataReader reader, CancellationToken ct)
    {
        for (var i = 0; i < 4; i++)
        {
            if (await reader.IsDBNullAsync(i, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    public static string Key(string schema, string name) => $"{schema}.{name}";

    public static string Key(DbObjectName name) => Key(name.Schema, name.Name);
}
