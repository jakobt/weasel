using Shouldly;
using Weasel.SqlServer.Scaffolding;
using Weasel.SqlServer.Tables;
using Xunit;

namespace Weasel.SqlServer.Tests.Scaffolding;

public class SqlServerSchemaCatalogTests: IntegrationContext
{
    public SqlServerSchemaCatalogTests(): base("catalog")
    {
    }

    private async Task withSampleObjects()
    {
        await ResetSchema();

        var parent = new Table("catalog.parent");
        parent.AddColumn<int>("id").AsPrimaryKey();
        parent.AddColumn<string>("name");
        await CreateSchemaObjectInDatabase(parent);

        var child = new Table("catalog.child");
        child.AddColumn<int>("id").AsPrimaryKey();
        child.AddColumn<int>("parent_id").ForeignKeyTo(parent, "id");
        await CreateSchemaObjectInDatabase(child);

        await theConnection.CreateCommand(
                "create view catalog.parent_names as select id, name from catalog.parent")
            .ExecuteNonQueryAsync();

        await theConnection.CreateCommand("create sequence catalog.counter as int start with 1")
            .ExecuteNonQueryAsync();

        await theConnection.CreateCommand(
                "create procedure catalog.do_nothing as begin select 1 end")
            .ExecuteNonQueryAsync();

        await theConnection.CreateCommand(
                "create function catalog.double_it(@value int) returns int as begin return @value * 2 end")
            .ExecuteNonQueryAsync();

        await theConnection.CreateCommand("create type catalog.id_list as table (id int)")
            .ExecuteNonQueryAsync();

        await theConnection.CreateCommand("create synonym catalog.parent_alias for catalog.parent")
            .ExecuteNonQueryAsync();

        await theConnection.CreateCommand(
                "create trigger catalog.child_insert on catalog.child after insert as begin set nocount on end")
            .ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task finds_every_kind_of_object()
    {
        await withSampleObjects();

        var catalog = await theConnection.ReadCatalogAsync(new SqlServerCatalogFilter { IncludeSchemas = ["catalog"] });

        catalog.Tables.Select(x => x.Name).ShouldBe(["child", "parent"]);
        catalog.Views.Select(x => x.Name).ShouldBe(["parent_names"]);
        catalog.Sequences.Select(x => x.Name).ShouldBe(["counter"]);
        catalog.Functions.Select(x => x.Name).ShouldBe(["double_it"]);
        catalog.Procedures.Select(x => x.Name).ShouldBe(["do_nothing"]);
        catalog.TableTypes.Select(x => x.Name).ShouldBe(["id_list"]);
        catalog.Synonyms.Select(x => x.Name).ShouldBe(["parent_alias"]);
        catalog.Triggers.Select(x => x.Name).ShouldBe(["child_insert"]);
    }

    [Fact]
    public async Task tables_and_views_are_reported_separately()
    {
        await withSampleObjects();

        var catalog = await theConnection.ReadCatalogAsync(new SqlServerCatalogFilter { IncludeSchemas = ["catalog"] });

        // information_schema.tables lumps views in with tables; the catalog must not.
        catalog.Tables.Select(x => x.Name).ShouldNotContain("parent_names");
    }

    [Fact]
    public async Task every_name_carries_its_schema()
    {
        await withSampleObjects();

        var catalog = await theConnection.ReadCatalogAsync(new SqlServerCatalogFilter { IncludeSchemas = ["catalog"] });

        catalog.All().ShouldAllBe(x => x.Schema == "catalog");
        catalog.SchemaNames().ShouldBe(["catalog"]);
    }

    [Fact]
    public async Task include_schemas_excludes_everything_else()
    {
        await withSampleObjects();
        await theConnection.ResetSchemaAsync("catalog_other");

        var other = new Table("catalog_other.elsewhere");
        other.AddColumn<int>("id").AsPrimaryKey();
        await CreateSchemaObjectInDatabase(other);

        var catalog = await theConnection.ReadCatalogAsync(new SqlServerCatalogFilter { IncludeSchemas = ["catalog"] });

        catalog.Tables.Select(x => x.Name).ShouldNotContain("elsewhere");

        var both = await theConnection.ReadCatalogAsync(
            new SqlServerCatalogFilter { IncludeSchemas = ["catalog", "catalog_other"] });

        both.Tables.Select(x => x.QualifiedName).ShouldContain("catalog_other.elsewhere");
    }

    [Fact]
    public async Task exclude_schemas_drops_that_schema()
    {
        await withSampleObjects();

        var catalog = await theConnection.ReadCatalogAsync(new SqlServerCatalogFilter { ExcludeSchemas = ["catalog"] });

        catalog.Tables.Select(x => x.Schema).ShouldNotContain("catalog");
    }

    [Fact]
    public async Task name_pattern_filters_by_object_name()
    {
        await withSampleObjects();

        var catalog = await theConnection.ReadCatalogAsync(new SqlServerCatalogFilter
        {
            IncludeSchemas = ["catalog"], NameLikePattern = "par%"
        });

        catalog.Tables.Select(x => x.Name).ShouldBe(["parent"]);
        catalog.Views.Select(x => x.Name).ShouldBe(["parent_names"]);
        catalog.Sequences.ShouldBeEmpty();
    }

    [Fact]
    public async Task default_filter_skips_the_built_in_role_schemas()
    {
        await withSampleObjects();

        var catalog = await theConnection.ReadCatalogAsync();

        catalog.SchemaNames().ShouldNotContain("sys");
        catalog.SchemaNames().ShouldNotContain("db_owner");
        catalog.SchemaNames().ShouldContain("catalog");
    }

    [Fact]
    public async Task tooling_tables_are_excluded_unless_asked_for()
    {
        await ResetSchema();

        await theConnection.CreateCommand(
                "create table catalog.__EFMigrationsHistory (MigrationId nvarchar(150) not null primary key)")
            .ExecuteNonQueryAsync();

        var filter = new SqlServerCatalogFilter { IncludeSchemas = ["catalog"] };
        var catalog = await theConnection.ReadCatalogAsync(filter);
        catalog.Tables.ShouldBeEmpty();

        var withSystem = await theConnection.ReadCatalogAsync(filter with { IncludeSystemObjects = true });
        withSystem.Tables.Select(x => x.Name).ShouldBe(["__EFMigrationsHistory"]);
    }

    [Fact]
    public async Task empty_schema_yields_an_empty_catalog()
    {
        await ResetSchema();

        var catalog = await theConnection.ReadCatalogAsync(new SqlServerCatalogFilter { IncludeSchemas = ["catalog"] });

        catalog.Count.ShouldBe(0);
        catalog.All().ShouldBeEmpty();
    }
}
