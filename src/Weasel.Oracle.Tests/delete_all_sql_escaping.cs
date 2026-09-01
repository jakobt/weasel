using Shouldly;
using Weasel.Core;
using Xunit;

namespace Weasel.Oracle.Tests;

public class delete_all_sql_escaping
{
    private const string Victim = "DELETE_ALL_VICTIM";
    private const string Real = "DELETE_ALL_REAL";
    private const string Payload = $"{Real}; DROP TABLE weasel.{Victim}; --";

#pragma warning disable CS0618
    private static DbObjectName Name(string schema, string name, bool asProviderName)
        => asProviderName ? new OracleObjectName(schema, name) : new DbObjectName(schema, name);
#pragma warning restore CS0618

    [Fact]
    public void oracle_renders_a_provider_typed_qualified_name_bare()
    {
        new OracleObjectName("weasel", Payload).QualifiedName.ShouldBe($"weasel.{Payload}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void a_semicolon_bearing_name_is_quoted(bool asProviderName)
    {
        var sql = new OracleMigrator().GenerateDeleteAllSql([Name("weasel", Payload, asProviderName)]);

        sql.ShouldNotContain($"DELETE FROM weasel.{Real}; DROP");
        sql.ShouldContain($"DELETE FROM weasel.\"{Payload.ToUpperInvariant()}\";");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void a_quote_bearing_name_is_escaped(bool asProviderName)
    {
        var sql = new OracleMigrator()
            .GenerateDeleteAllSql([Name("weasel", $"T\"; DROP TABLE weasel.{Victim}; --", asProviderName)]);

        sql.ShouldContain($"\"T\"\"; DROP TABLE WEASEL.{Victim}; --\"");
    }
}
