using Microsoft.Data.SqlClient;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer.Scaffolding;
using Xunit;

namespace Weasel.SqlServer.Tests.Scaffolding;

[Collection("integration")]
public class scaffolded_database_options: IAsyncLifetime
{
    private const string Source = "weasel_options_source";
    private const string Target = "weasel_options_target";

    public async ValueTask InitializeAsync()
    {
        await dropDatabases();
        await execute("master", $"create database [{Source}]; create database [{Target}];");
        await execute("master", $"alter database [{Source}] set allow_snapshot_isolation on;");
        await execute("master", $"alter database [{Source}] set read_committed_snapshot on with rollback immediate;");
        await execute(Source, "create schema opts;");
        await execute(Source, "create table opts.thing (id int not null constraint pk_thing primary key)");
    }

    public async ValueTask DisposeAsync() => await dropDatabases();

    [Fact]
    public async Task the_scaffolded_database_carries_and_applies_its_isolation_options()
    {
        ScaffoldResult result;
        await using (var conn = new SqlConnection(connectionString(Source)))
        {
            await conn.OpenAsync();
            result = await SqlServerScaffolder.ScaffoldAsync(conn,
                new SqlServerCatalogFilter { IncludeSchemas = ["opts"] },
                new ScaffoldEmissionOptions { Namespace = "Options.Generated" });
        }

        var target = ScaffoldCompilationHarness.BuildDatabase(
            result, "OptionsApply", connectionString(Target), "Options.Generated");
        (await target.CreateMigrationAsync()).Difference.ShouldBe(SchemaPatchDifference.Update);

        await target.ApplyAllConfiguredChangesToDatabaseAsync();

        (await readOptions(Target)).ShouldBe(((byte)1, true));
        (await target.CreateMigrationAsync()).Difference.ShouldBe(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task a_database_with_default_options_scaffolds_without_them()
    {
        ScaffoldResult result;
        await using (var conn = new SqlConnection(connectionString(Target)))
        {
            await conn.OpenAsync();
            await new SqlCommand("create schema plain; ", conn).ExecuteNonQueryAsync();
            await new SqlCommand("create table plain.thing (id int not null constraint pk_plain primary key)", conn)
                .ExecuteNonQueryAsync();
            result = await SqlServerScaffolder.ScaffoldAsync(conn,
                new SqlServerCatalogFilter { IncludeSchemas = ["plain"] },
                new ScaffoldEmissionOptions { Namespace = "Plain.Generated" });
        }

        result.Files["ScaffoldedDatabase.cs"].ShouldNotContain("DatabaseOptions");
    }

    private static async Task<(byte Snapshot, bool ReadCommittedSnapshot)> readOptions(string database)
    {
        await using var conn = new SqlConnection(connectionString("master"));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "select snapshot_isolation_state, is_read_committed_snapshot_on from sys.databases where name = @name", conn);
        cmd.Parameters.AddWithValue("name", database);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetByte(0), reader.GetBoolean(1));
    }

    private static string connectionString(string database)
        => new SqlConnectionStringBuilder(ConnectionSource.ConnectionString) { InitialCatalog = database }.ConnectionString;

    private static async Task execute(string database, string sql)
    {
        await using var conn = new SqlConnection(connectionString(database));
        await conn.OpenAsync();
        await new SqlCommand(sql, conn).ExecuteNonQueryAsync();
    }

    private static async Task dropDatabases()
    {
        SqlConnection.ClearAllPools();
        foreach (var name in new[] { Source, Target })
        {
            await execute("master",
                $"if db_id(N'{name}') is not null begin alter database [{name}] set single_user with rollback immediate; drop database [{name}]; end");
        }
    }
}
