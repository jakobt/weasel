using System.Data.Common;
using Weasel.Core;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Weasel.SqlServer;

public class DatabaseOptions: ISchemaObject
{
    public bool? AllowSnapshotIsolation { get; set; }

    public bool? ReadCommittedSnapshot { get; set; }

    public DbObjectName Identifier { get; } = new SqlServerObjectName("sys", "databases");

    public void WriteCreateStatement(Migrator migrator, TextWriter writer)
    {
        if (AllowSnapshotIsolation.HasValue)
        {
            writer.WriteLine($"ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION {onOff(AllowSnapshotIsolation.Value)};");
        }

        if (ReadCommittedSnapshot.HasValue)
        {
            writer.WriteLine(
                $"ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT {onOff(ReadCommittedSnapshot.Value)} WITH ROLLBACK IMMEDIATE;");
        }
    }

    public void WriteDropStatement(Migrator rules, TextWriter writer)
    {
    }

    public void ConfigureQueryCommand(DbCommandBuilder builder)
    {
        builder.Append(
            "select snapshot_isolation_state, is_read_committed_snapshot_on from sys.databases where database_id = db_id();");
    }

    public async Task<ISchemaObjectDelta> CreateDeltaAsync(DbDataReader reader, CancellationToken ct = default)
    {
        await reader.ReadAsync(ct).ConfigureAwait(false);
        var snapshotState = await reader.GetFieldValueAsync<byte>(0, ct).ConfigureAwait(false);
        var readCommittedSnapshot = await reader.GetFieldValueAsync<bool>(1, ct).ConfigureAwait(false);

        var same = pinnedMatch(AllowSnapshotIsolation, snapshotState is 1 or 3)
                   && pinnedMatch(ReadCommittedSnapshot, readCommittedSnapshot);

        return new SchemaObjectDelta(this, same ? SchemaPatchDifference.None : SchemaPatchDifference.Update);
    }

    public IEnumerable<DbObjectName> AllNames() => [];

    private static bool pinnedMatch(bool? expected, bool actual) => expected is null || expected == actual;

    private static string onOff(bool value) => value ? "ON" : "OFF";
}
