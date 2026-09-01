using Shouldly;
using Weasel.Core;
using Weasel.Postgresql.Tables;
using Xunit;

namespace Weasel.Postgresql.Tests.Tables;

[Collection("domains")]
public class domain_typed_columns: IntegrationContext
{
    public domain_typed_columns(): base("domains")
    {
    }

    private async Task prepareSchemas()
    {
        await ResetSchema();
        await theConnection.ResetSchemaAsync("otherdomains");
        await theConnection.CreateCommand("drop domain if exists public.weasel_public_domain cascade")
            .ExecuteNonQueryAsync();
    }

    private Task createDomain(string declaration)
        => theConnection.CreateCommand($"create domain {declaration}").ExecuteNonQueryAsync();

    [Fact]
    public async Task read_a_domain_column_back_as_the_domain_and_not_its_base_type()
    {
        await prepareSchemas();
        await createDomain("domains.film_year as integer constraint film_year_check check (value >= 1901)");

        var table = new Table("domains.film");
        table.AddColumn<int>("id").AsPrimaryKey();
        table.AddColumn("release_year", "domains.film_year");

        await CreateSchemaObjectInDatabase(table);

        var existing = await table.FetchExistingAsync(theConnection);

        existing.ShouldNotBeNull();
        existing.ColumnFor("release_year")!.Type.ShouldBe("domains.film_year");
    }

    [Fact]
    public async Task a_model_declaring_a_domain_converges()
    {
        await prepareSchemas();
        await createDomain("domains.film_year as integer constraint film_year_check check (value >= 1901)");

        var table = new Table("domains.film");
        table.AddColumn<int>("id").AsPrimaryKey();
        table.AddColumn("release_year", "domains.film_year");

        await CreateSchemaObjectInDatabase(table);

        (await table.FindDeltaAsync(theConnection)).Difference.ShouldBe(SchemaPatchDifference.None);

        await CreateSchemaObjectInDatabase(table);

        (await table.FindDeltaAsync(theConnection)).Difference.ShouldBe(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task read_a_domain_declared_in_another_schema_schema_qualified()
    {
        await prepareSchemas();
        await createDomain("otherdomains.postal_code as varchar(10)");

        var table = new Table("domains.address");
        table.AddColumn<int>("id").AsPrimaryKey();
        table.AddColumn("zip", "otherdomains.postal_code");

        await CreateSchemaObjectInDatabase(table);

        var existing = await table.FetchExistingAsync(theConnection);

        existing.ShouldNotBeNull();
        existing.ColumnFor("zip")!.Type.ShouldBe("otherdomains.postal_code");

        (await table.FindDeltaAsync(theConnection)).Difference.ShouldBe(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task a_domain_over_an_enum_reads_back_as_the_domain()
    {
        await prepareSchemas();
        await theConnection.CreateCommand("create type domains.mood as enum ('sad', 'ok', 'happy')")
            .ExecuteNonQueryAsync();
        await createDomain("domains.settled_mood as domains.mood not null");

        var table = new Table("domains.person");
        table.AddColumn<int>("id").AsPrimaryKey();
        table.AddColumn("disposition", "domains.settled_mood");

        await CreateSchemaObjectInDatabase(table);

        var existing = await table.FetchExistingAsync(theConnection);

        existing.ShouldNotBeNull();
        existing.ColumnFor("disposition")!.Type.ShouldBe("domains.settled_mood");
    }

    [Fact]
    public async Task a_domain_in_the_public_schema_converges_when_declared_unqualified()
    {
        await prepareSchemas();
        await createDomain("public.weasel_public_domain as text");

        var table = new Table("domains.note");
        table.AddColumn<int>("id").AsPrimaryKey();
        table.AddColumn("body", "weasel_public_domain");

        await CreateSchemaObjectInDatabase(table);

        var existing = await table.FetchExistingAsync(theConnection);

        existing.ShouldNotBeNull();
        existing.ColumnFor("body")!.Type.ShouldBe("weasel_public_domain");

        (await table.FindDeltaAsync(theConnection)).Difference.ShouldBe(SchemaPatchDifference.None);
    }
}
