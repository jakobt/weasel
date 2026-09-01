using Shouldly;
using Weasel.Core;
using Xunit;

namespace Weasel.Postgresql.Tests;

public class delete_all_sql_escaping
{
    private const string Victim = "delete_all_escaping_victim";
    private const string Real = "delete_all_escaping_real";
    private const string Payload = $"{Real}; DROP TABLE public.{Victim}; --";

    private const string QuotePayload = $"t\"; DROP TABLE public.{Victim}; --";

#pragma warning disable CS0618
    private static DbObjectName Name(string schema, string name, bool asProviderName)
        => asProviderName
            ? new PostgresqlObjectName(schema, name, SchemaUtils.IdentifierUsage.General)
            : new DbObjectName(schema, name);
#pragma warning restore CS0618

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void a_semicolon_bearing_name_is_quoted(bool asProviderName)
    {
        var sql = new PostgresqlMigrator().GenerateDeleteAllSql([Name("public", Payload, asProviderName)]);

        sql.ShouldNotContain($"TRUNCATE TABLE public.{Real}; DROP");
        sql.ShouldContain($"TRUNCATE TABLE public.\"{Payload}\" RESTART IDENTITY CASCADE;");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void a_quote_bearing_name_is_escaped(bool asProviderName)
    {
        var sql = new PostgresqlMigrator().GenerateDeleteAllSql([Name("public", QuotePayload, asProviderName)]);

        sql.ShouldContain($"public.\"t\"\"; DROP TABLE public.{Victim}; --\"");
    }

    [Fact]
    public void an_ordinary_name_is_unchanged()
    {
        new PostgresqlMigrator()
            .GenerateDeleteAllSql([new PostgresqlObjectName("public", "users", SchemaUtils.IdentifierUsage.General)])
            .ShouldBe("TRUNCATE TABLE public.users RESTART IDENTITY CASCADE;");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task an_injected_table_name_does_not_execute_its_payload(bool asProviderName)
    {
        await using var conn = new Npgsql.NpgsqlConnection(ConnectionSource.ConnectionString);
        await conn.OpenAsync();

        await conn.CreateCommand($@"
            drop table if exists public.""{Payload}"";
            drop table if exists public.{Real};
            create table public.{Real} (id int);
            create table public.""{Payload}"" (id int);
            create table if not exists public.{Victim} (id int);
            insert into public.""{Payload}"" (id) values (1);")
            .ExecuteNonQueryAsync();

        try
        {
            await conn.CreateCommand(new PostgresqlMigrator().GenerateDeleteAllSql([Name("public", Payload, asProviderName)]))
                .ExecuteNonQueryAsync();
        }
        catch (Npgsql.PostgresException)
        {
        }

        var stillThere = await conn
            .CreateCommand($"select count(*) from pg_class where relname = '{Victim}' and relnamespace = 'public'::regnamespace")
            .ExecuteScalarAsync();
        stillThere.ShouldBe(1L);

        var remaining = await conn.CreateCommand($"select count(*) from public.\"{Payload}\"").ExecuteScalarAsync();
        remaining.ShouldBe(0L);

        await conn.CreateCommand($@"
            drop table if exists public.""{Payload}"";
            drop table if exists public.{Real};
            drop table if exists public.{Victim};").ExecuteNonQueryAsync();
    }
}
