using JasperFx;
using Shouldly;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql.Tables;
using Xunit;

namespace Weasel.Postgresql.Tests.Tables;

[Collection("add_only_migrator")]
public class add_only_migrator: IntegrationContext
{
    public add_only_migrator(): base("add_only_migrator")
    {
    }

    public override ValueTask InitializeAsync() => new(ResetSchema());

    private static Table newerTable()
    {
        var table = new Table(new PostgresqlObjectName("add_only_migrator", "orders"));
        table.AddColumn<Guid>("id").AsPrimaryKey();
        table.AddColumn<string>("customer");
        table.AddColumn<string>("total_amount");
        table.Indexes.Add(new IndexDefinition("idx_orders_total_amount") { Columns = ["total_amount"] });
        return table;
    }

    private DatabaseWithTables olderDatabase(bool addOnly, params string[] extraColumns)
    {
        var database = new DatabaseWithTables("add_only_migrator", theDataSource, AutoCreate.CreateOrUpdate);
        database.Migrator.AddOnlyMigrations = addOnly;

        var table = database.AddTable(new PostgresqlObjectName("add_only_migrator", "orders"));
        table.AddPrimaryKeyColumn("id", typeof(Guid));
        table.AddColumn("customer", typeof(string));
        foreach (var column in extraColumns) table.AddColumn(column, typeof(string));

        return database;
    }

    private async Task<Table> existingOrders()
        => (await newerTable().FetchExistingAsync(theConnection))!;

    [Fact]
    public async Task setup_leaves_undeclared_columns_and_indexes_in_place()
    {
        await CreateSchemaObjectInDatabase(newerTable());

        await olderDatabase(addOnly: true, "shipped_on").ApplyAllConfiguredChangesToDatabaseAsync();

        var actual = await existingOrders();
        actual.HasColumn("total_amount").ShouldBeTrue();
        actual.IndexFor("idx_orders_total_amount").ShouldNotBeNull();
        actual.HasColumn("shipped_on").ShouldBeTrue();
    }

    [Fact]
    public async Task the_assert_accepts_undeclared_columns_and_indexes()
    {
        await CreateSchemaObjectInDatabase(newerTable());

        await olderDatabase(addOnly: true).AssertDatabaseMatchesConfigurationAsync();
    }

    [Fact]
    public async Task the_assert_still_refuses_a_missing_declared_column()
    {
        await CreateSchemaObjectInDatabase(newerTable());

        await Should.ThrowAsync<DatabaseValidationException>(
            () => olderDatabase(addOnly: true, "shipped_on").AssertDatabaseMatchesConfigurationAsync());
    }

    [Fact]
    public async Task without_the_flag_setup_drops_them()
    {
        await CreateSchemaObjectInDatabase(newerTable());

        await olderDatabase(addOnly: false).ApplyAllConfiguredChangesToDatabaseAsync();

        var actual = await existingOrders();
        actual.HasColumn("total_amount").ShouldBeFalse();
        actual.IndexFor("idx_orders_total_amount").ShouldBeNull();
    }
}
