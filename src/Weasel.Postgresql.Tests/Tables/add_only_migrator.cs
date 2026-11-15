using JasperFx;
using Npgsql;
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

    private static Table customersTable()
    {
        var table = new Table(new PostgresqlObjectName("add_only_migrator", "customers"));
        table.AddColumn<string>("name").AsPrimaryKey();
        return table;
    }

    private static Table newerTableWithForeignKey()
    {
        var table = newerTable();
        table.ForeignKeys.Add(new ForeignKey("fk_orders_customer")
        {
            ColumnNames = ["customer"],
            LinkedNames = ["name"],
            LinkedTable = new PostgresqlObjectName("add_only_migrator", "customers")
        });
        return table;
    }

    private static Table olderTable()
    {
        var table = new Table(new PostgresqlObjectName("add_only_migrator", "orders"));
        table.AddColumn<Guid>("id").AsPrimaryKey();
        table.AddColumn<string>("customer");
        return table;
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

    [Fact]
    public async Task setup_and_the_assert_leave_an_undeclared_foreign_key_in_place()
    {
        await CreateSchemaObjectInDatabase(customersTable());
        await CreateSchemaObjectInDatabase(newerTableWithForeignKey());

        var database = olderDatabase(addOnly: true);
        await database.ApplyAllConfiguredChangesToDatabaseAsync();
        await database.AssertDatabaseMatchesConfigurationAsync();

        (await existingOrders()).ForeignKeys.ShouldContain(x => x.Name == "fk_orders_customer");
    }

    [Fact]
    public async Task without_the_flag_setup_drops_an_undeclared_foreign_key()
    {
        await CreateSchemaObjectInDatabase(customersTable());
        await CreateSchemaObjectInDatabase(newerTableWithForeignKey());

        await olderDatabase(addOnly: false).ApplyAllConfiguredChangesToDatabaseAsync();

        (await existingOrders()).ForeignKeys.ShouldBeEmpty();
    }

    [Fact]
    public async Task ensuring_one_features_storage_leaves_undeclared_columns_and_indexes_in_place()
    {
        await CreateSchemaObjectInDatabase(newerTable());

        var database = new SingleFeatureDatabase(theDataSource, olderTable(), addOnly: true);
        await database.EnsureStorageExistsAsync(typeof(SingleFeatureDatabase));

        var actual = await existingOrders();
        actual.HasColumn("total_amount").ShouldBeTrue();
        actual.IndexFor("idx_orders_total_amount").ShouldNotBeNull();
    }

    [Fact]
    public async Task without_the_flag_ensuring_one_features_storage_drops_them()
    {
        await CreateSchemaObjectInDatabase(newerTable());

        var database = new SingleFeatureDatabase(theDataSource, olderTable(), addOnly: false);
        await database.EnsureStorageExistsAsync(typeof(SingleFeatureDatabase));

        (await existingOrders()).HasColumn("total_amount").ShouldBeFalse();
    }

    [Fact]
    public async Task the_flag_stays_on_the_migrator_and_a_table_that_allows_drops_still_drops_without_it()
    {
        await CreateSchemaObjectInDatabase(newerTable());

        var table = olderTable();
        table.AddOnlyMigrations = false;

        var addOnly = new DatabaseWithTables("add_only_migrator", theDataSource, AutoCreate.CreateOrUpdate);
        addOnly.Migrator.AddOnlyMigrations = true;
        addOnly.AddTable(table);
        await addOnly.ApplyAllConfiguredChangesToDatabaseAsync();

        table.AddOnlyMigrations.ShouldBeFalse();
        (await existingOrders()).HasColumn("total_amount").ShouldBeTrue();

        var allowsDrops = new DatabaseWithTables("add_only_migrator", theDataSource, AutoCreate.CreateOrUpdate);
        allowsDrops.AddTable(table);
        await allowsDrops.ApplyAllConfiguredChangesToDatabaseAsync();

        (await existingOrders()).HasColumn("total_amount").ShouldBeFalse();
    }

    private class SingleFeatureDatabase: PostgresqlDatabase
    {
        private readonly IFeatureSchema _feature;

        public SingleFeatureDatabase(NpgsqlDataSource dataSource, Table table, bool addOnly)
            : base(new DefaultMigrationLogger(), AutoCreate.CreateOrUpdate, new PostgresqlMigrator(),
                "add_only_migrator", dataSource, false)
        {
            Migrator.AddOnlyMigrations = addOnly;
            _feature = new TableFeature(Migrator, table);
        }

        public override IFeatureSchema[] BuildFeatureSchemas() => [_feature];

        public override IFeatureSchema FindFeature(Type featureType)
            => featureType == _feature.StorageType ? _feature : base.FindFeature(featureType);
    }

    private class TableFeature(Migrator migrator, Table table): FeatureSchemaBase("Orders", migrator)
    {
        public override Type StorageType => typeof(SingleFeatureDatabase);
        protected override IEnumerable<ISchemaObject> schemaObjects() => [table];
    }
}
