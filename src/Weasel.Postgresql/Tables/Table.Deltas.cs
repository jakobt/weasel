using System.Data.Common;
using Npgsql;
using Weasel.Core;

namespace Weasel.Postgresql.Tables;

public partial class Table
{
    public override Task<ISchemaObjectDelta> CreateDeltaAsync(DbDataReader reader, CancellationToken ct = default)
        => CreateDeltaAsync(reader, false, ct);

    public override async Task<ISchemaObjectDelta> CreateDeltaAsync(DbDataReader reader, bool addOnlyMigrations,
        CancellationToken ct = default)
    {
        var existing = await readExistingAsync(reader, ct).ConfigureAwait(false);
        return new TableDelta(this, existing, addOnlyMigrations);
    }

    public async Task<TableDelta> FindDeltaAsync(NpgsqlConnection conn, CancellationToken ct = default)
    {
        var actual = await FetchExistingAsync(conn, ct).ConfigureAwait(false);
        return new TableDelta(this, actual);
    }
}
