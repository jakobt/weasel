using Shouldly;
using Weasel.Core;
using Xunit;

namespace Weasel.SqlServer.Tests;

public class delete_all_sql_escaping
{
    private const string Victim = "delete_all_escaping_victim";
    private const string Real = "delete_all_escaping_real";
    private const string Payload = $"{Real}; DROP TABLE dbo.{Victim}; --";

    private const string QuotePayload = $"t', RESEED, 0); DROP TABLE dbo.{Victim}; --";

#pragma warning disable CS0618
    private static DbObjectName Name(string schema, string name, bool asProviderName)
        => asProviderName ? new SqlServerObjectName(schema, name) : new DbObjectName(schema, name);
#pragma warning restore CS0618

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void a_semicolon_bearing_name_is_bracketed_in_the_delete(bool asProviderName)
    {
        var sql = new SqlServerMigrator().GenerateDeleteAllSql([Name("dbo", Payload, asProviderName)]);

        sql.ShouldNotContain($"DELETE FROM dbo.{Real}; DROP");
        sql.ShouldContain($"DELETE FROM dbo.[{Payload}];");
        sql.ShouldContain($"DBCC CHECKIDENT('dbo.[{Payload}]', RESEED, 0)");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void a_quote_bearing_name_is_escaped_inside_the_checkident_literal(bool asProviderName)
    {
        var sql = new SqlServerMigrator().GenerateDeleteAllSql([Name("dbo", QuotePayload, asProviderName)]);

        sql.ShouldContain($"DELETE FROM dbo.[{QuotePayload}];");
        sql.ShouldNotContain("DBCC CHECKIDENT('dbo.[t', RESEED");
        sql.ShouldContain($"DBCC CHECKIDENT('dbo.[t'', RESEED, 0); DROP TABLE dbo.{Victim}; --]', RESEED, 0)");
    }

    [Fact]
    public void an_ordinary_name_is_unchanged()
    {
        var sql = new SqlServerMigrator().GenerateDeleteAllSql([new SqlServerObjectName("dbo", "users")]);

        sql.ShouldContain("DELETE FROM dbo.users;");
        sql.ShouldContain("DBCC CHECKIDENT('dbo.users', RESEED, 0)");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task an_injected_table_name_does_not_execute_its_payload(bool asProviderName)
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(ConnectionSource.ConnectionString);
        await conn.OpenAsync();

        await conn.CreateCommand($@"
            drop table if exists dbo.[{Payload}];
            drop table if exists dbo.{Real};
            create table dbo.{Real} (id int);
            create table dbo.[{Payload}] (id int);
            if object_id('dbo.{Victim}') is null create table dbo.{Victim} (id int);
            insert into dbo.[{Payload}] (id) values (1);")
            .ExecuteNonQueryAsync();

        try
        {
            await conn.CreateCommand(new SqlServerMigrator().GenerateDeleteAllSql([Name("dbo", Payload, asProviderName)]))
                .ExecuteNonQueryAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
        }

        var stillThere = await conn.CreateCommand($"select object_id('dbo.{Victim}')").ExecuteScalarAsync();
        stillThere.ShouldNotBe(DBNull.Value);

        var remaining = await conn.CreateCommand($"select count(*) from dbo.[{Payload}]").ExecuteScalarAsync();
        remaining.ShouldBe(0);

        await conn.CreateCommand($@"
            drop table if exists dbo.[{Payload}];
            drop table if exists dbo.{Real};
            drop table if exists dbo.{Victim};").ExecuteNonQueryAsync();
    }
}
