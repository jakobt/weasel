using JasperFx;
using JasperFx.Core;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Core.CommandLine;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Weasel.Postgresql.Tests;
using Xunit;

namespace Weasel.CommandLine.Tests;

public class resources_setup_global_lock: IntegrationContext, IAsyncLifetime
{
    private const string SchemaName = "setup_lock";

    public async ValueTask InitializeAsync()
    {
        await DropSchema(SchemaName);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task setup_does_not_migrate_while_another_node_holds_the_lock()
    {
        var database = new GloballyLockedDatabase(7_200_001, TimeSpan.Zero);
        database.Features["One"].AddTable(SchemaName, "held");

        await using var otherNode = await holdLockAsync(database.LockId);

        var resource = new DatabaseResource(database, "weasel://db".ToUri());

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => resource.Setup(CancellationToken.None));
        ex.Message.ShouldContain("Unable to attain the global lock");

        (await tableExistsAsync("held")).ShouldBeFalse();
    }

    [Fact]
    public async Task setup_migrates_once_the_other_node_releases_the_lock()
    {
        var database = new GloballyLockedDatabase(7_200_002, 30.Seconds());
        database.Features["One"].AddTable(SchemaName, "waited");

        var otherNode = await holdLockAsync(database.LockId);

        var resource = new DatabaseResource(database, "weasel://db".ToUri());
        var setup = resource.Setup(CancellationToken.None);

        await database.FirstRefusal.WaitAsync(10.Seconds());
        (await tableExistsAsync("waited")).ShouldBeFalse();

        await otherNode.DisposeAsync();
        await setup.WaitAsync(30.Seconds());

        (await tableExistsAsync("waited")).ShouldBeTrue();

        await using var probe = await holdLockAsync(database.LockId);
    }

    private static async Task<HeldLock> holdLockAsync(int lockId)
    {
        var conn = new NpgsqlConnection(ConnectionSource.ConnectionString);
        await conn.OpenAsync();
        (await conn.TryGetGlobalLock(lockId)).Succeeded.ShouldBeTrue();
        return new HeldLock(conn, lockId);
    }

    private sealed class HeldLock(NpgsqlConnection conn, int lockId): IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await conn.ReleaseGlobalLock(lockId);
            await conn.DisposeAsync();
        }
    }

    private static async Task<bool> tableExistsAsync(string tableName)
    {
        await using var conn = new NpgsqlConnection(ConnectionSource.ConnectionString);
        await conn.OpenAsync();
        var found = await conn.CreateCommand("select to_regclass(:name) is not null")
            .With("name", $"{SchemaName}.{tableName}")
            .ExecuteScalarAsync();
        return (bool)found!;
    }
}

internal class GloballyLockedDatabase: TestDatabaseWithTables, IDatabaseWithGlobalLock, IGlobalLock<NpgsqlConnection>
{
    private readonly TimeSpan _timeout;
    private readonly TaskCompletionSource _firstRefusal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GloballyLockedDatabase(int lockId, TimeSpan timeout): base(AutoCreate.CreateOrUpdate, "setup_lock")
    {
        LockId = lockId;
        _timeout = timeout;
    }

    public int LockId { get; }

    public Task FirstRefusal => _firstRefusal.Task;

    public Task<SchemaPatchDifference> ApplyAllConfiguredChangesUnderGlobalLockAsync(CancellationToken token)
    {
        return ApplyAllConfiguredChangesToDatabaseAsync(this, ct: token);
    }

    public async Task<AttainLockResult> TryAttainLock(NpgsqlConnection conn, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + _timeout;
        while (true)
        {
            var result = await conn.TryGetGlobalLock(LockId, ct);
            if (result.Succeeded || result.ShouldReconnect) return result;

            _firstRefusal.TrySetResult();
            if (DateTimeOffset.UtcNow >= deadline) return result;

            await Task.Delay(50, ct);
        }
    }

    public Task ReleaseLock(NpgsqlConnection conn, CancellationToken ct = default)
    {
        return conn.ReleaseGlobalLock(LockId, cancellation: ct);
    }
}
