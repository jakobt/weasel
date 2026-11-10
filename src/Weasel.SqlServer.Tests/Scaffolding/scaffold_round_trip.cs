using JasperFx;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer.Scaffolding;
using Xunit;

namespace Weasel.SqlServer.Tests.Scaffolding;

/// <summary>
///     The acceptance gate for the whole scaffolding feature: point it at a database, compile what it
///     writes, and require Weasel to see no difference between the generated model and the database
///     it was generated from. Anything the reader loses, the emitter garbles, or the comparison can't
///     express shows up here as drift.
/// </summary>
public class scaffold_round_trip: IntegrationContext
{
    public scaffold_round_trip(): base("roundtrip")
    {
    }

    // Each statement runs as its own batch: CREATE VIEW and CREATE PROCEDURE must be the first
    // statement in theirs, so they cannot be concatenated with the tables they depend on.
    private async Task<ScaffoldResult> scaffold(params string[] statements)
    {
        await ResetSchema();
        foreach (var statement in statements)
        {
            await theConnection.CreateCommand(statement).ExecuteNonQueryAsync();
        }

        return await SqlServerScaffolder.ScaffoldAsync(theConnection,
            new SqlServerCatalogFilter { IncludeSchemas = ["roundtrip"] },
            new ScaffoldEmissionOptions { Namespace = "RoundTrip.Generated" });
    }

    private async Task assertNoDrift(ScaffoldResult result, string assemblyName)
    {
        var database = ScaffoldCompilationHarness.BuildDatabase(
            result, assemblyName, ConnectionSource.ConnectionString, "RoundTrip.Generated");

        var migration = await database.CreateMigrationAsync();

        if (migration.Difference != SchemaPatchDifference.None)
        {
            var writer = new StringWriter();
            migration.WriteAllUpdates(writer, database.Migrator, AutoCreate.CreateOrUpdate);

            throw new Exception(
                $"Scaffolded model drifted from the database it was generated from:\n{writer}\n\n" +
                string.Join("\n\n", result.Files.Select(f => $"===== {f.Key} =====\n{f.Value}")));
        }

        migration.Difference.ShouldBe(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task generated_source_compiles()
    {
        var result = await scaffold(
            "create table roundtrip.simple (id int not null primary key, name varchar(100) not null)");

        Should.NotThrow(() => ScaffoldCompilationHarness.Compile(result, "Compiles", "RoundTrip.Generated"));
    }

    [Fact]
    public async Task a_simple_table_round_trips_with_no_drift()
    {
        var result = await scaffold(
            "create table roundtrip.simple (id int not null constraint pk_simple primary key, name varchar(100) not null)");

        await assertNoDrift(result, "SimpleRoundTrip");
    }

    [Fact]
    public async Task a_rich_table_round_trips_with_no_drift()
    {
        var result = await scaffold(@"
create table roundtrip.customer (
    id int not null identity(100,5) constraint pk_customer primary key,
    name nvarchar(200) not null,
    balance decimal(18,2) not null constraint df_balance default (0),
    created_at datetime2(3) null,
    notes nvarchar(max) null
);
create table roundtrip.[order] (
    id int not null constraint pk_order primary key,
    customer_id int not null,
    total decimal(18,2) not null,
    constraint fk_order_customer foreign key (customer_id) references roundtrip.customer (id) on delete cascade,
    constraint ck_order_total check (total >= 0)
);
create index idx_order_customer on roundtrip.[order] (customer_id) include (total);
create unique index idx_customer_name on roundtrip.customer (name) where name is not null;
");

        await assertNoDrift(result, "RichRoundTrip");
    }

    [Theory]
    [InlineData("alter table roundtrip.customer alter column name nvarchar(200) null")]
    [InlineData("alter table roundtrip.customer drop constraint df_balance")]
    [InlineData("alter table roundtrip.customer add constraint df_balance default (1) for balance")]
    public async Task a_changed_nullability_or_default_is_reported(string change)
    {
        var result = await scaffold(@"
create table roundtrip.customer (
    id int not null constraint pk_customer primary key,
    name nvarchar(200) not null,
    balance decimal(18,2) not null constraint df_balance default (0)
);
");
        var database = ScaffoldCompilationHarness.BuildDatabase(
            result, "DriftDetected" + Math.Abs(change.GetHashCode()), ConnectionSource.ConnectionString, "RoundTrip.Generated");

        if (change.Contains("add constraint"))
        {
            await theConnection.CreateCommand("alter table roundtrip.customer drop constraint df_balance").ExecuteNonQueryAsync();
        }

        await theConnection.CreateCommand(change).ExecuteNonQueryAsync();

        var migration = await database.CreateMigrationAsync();
        migration.Difference.ShouldBe(SchemaPatchDifference.Update);
    }

    [Fact]
    public async Task a_composite_key_table_round_trips_with_no_drift()
    {
        var result = await scaffold(@"
create table roundtrip.parent (
    x int not null,
    y int not null,
    constraint pk_parent primary key (y, x)
);
create table roundtrip.child (
    b int not null,
    a int not null,
    constraint pk_child primary key (b, a),
    constraint fk_child_parent foreign key (a, b) references roundtrip.parent (y, x)
);
");

        await assertNoDrift(result, "CompositeRoundTrip");
    }

    [Fact]
    public async Task tables_are_emitted_in_dependency_order()
    {
        var result = await scaffold(@"
create table roundtrip.grandparent (id int not null constraint pk_gp primary key);
create table roundtrip.parent (
    id int not null constraint pk_p primary key,
    gp_id int not null constraint fk_p_gp references roundtrip.grandparent (id)
);
create table roundtrip.child (
    id int not null constraint pk_c primary key,
    p_id int not null constraint fk_c_p references roundtrip.parent (id)
);
");

        var database = result.Files["ScaffoldedDatabase.cs"];

        var grandparent = database.IndexOf("RoundtripGrandparentTable", StringComparison.Ordinal);
        var parent = database.IndexOf("RoundtripParentTable", StringComparison.Ordinal);
        var child = database.IndexOf("RoundtripChildTable", StringComparison.Ordinal);

        grandparent.ShouldBeLessThan(parent);
        parent.ShouldBeLessThan(child);
    }

    [Fact]
    public async Task non_table_objects_use_their_own_weasel_models()
    {
        var result = await scaffold(
            "create table roundtrip.thing (id int not null constraint pk_thing primary key, name varchar(50))",
            "create view roundtrip.thing_names as select id, name from roundtrip.thing",
            "create sequence roundtrip.counter as int start with 42 increment by 7",
            "create function roundtrip.double_it(@value int) returns int as begin return @value * 2 end",
            "create procedure roundtrip.touch_thing as begin update roundtrip.thing set name = name end",
            "create type roundtrip.id_list as table (id int not null, label varchar(20) null)",
            "create trigger roundtrip.thing_audit on roundtrip.thing after insert as begin set nocount on end");

        // One file per object now, so look across the whole generated tree.
        var source = string.Join("\n", result.Files.Values);

        result.Files.Keys.ShouldContain(k => k.StartsWith("Views/", StringComparison.Ordinal));
        result.Files.Keys.ShouldContain(k => k.StartsWith("Functions/", StringComparison.Ordinal));
        result.Files.Keys.ShouldContain(k => k.StartsWith("Procedures/", StringComparison.Ordinal));
        result.Files.Keys.ShouldContain(k => k.StartsWith("Sequences/", StringComparison.Ordinal));
        result.Files.Keys.ShouldContain(k => k.StartsWith("TableTypes/", StringComparison.Ordinal));
        result.Files.Keys.ShouldContain(k => k.StartsWith("Triggers/", StringComparison.Ordinal));
        result.Files.Keys.ShouldContain(k => k.StartsWith("Tables/", StringComparison.Ordinal));
        result.Files.Keys.ShouldContain(k => k.EndsWith(".csproj", StringComparison.Ordinal));

        source.ShouldContain("new View(");
        source.ShouldContain("new Sequence(");
        source.ShouldContain("new Function(");
        source.ShouldContain("new StoredProcedure(");
        source.ShouldContain("new TableType(");

        // Sequence properties are read back, not guessed.
        source.ShouldContain("StartWith = 42");
        source.ShouldContain("IncrementBy = 7");

        source.ShouldContain("new ScaffoldedTrigger(");
        source.ShouldContain("Timing = TriggerTiming.After");
        source.ShouldContain("Events = TriggerEvents.Insert");

        result.OtherObjectCount.ShouldBe(6);
        result.Notes.ShouldBeEmpty();

        Should.NotThrow(() => ScaffoldCompilationHarness.Compile(result, "OtherObjects", "RoundTrip.Generated"));
    }

    [Fact]
    public async Task a_schema_with_every_kind_of_object_round_trips_with_no_drift()
    {
        var result = await scaffold(
            "create table roundtrip.thing (id int not null constraint pk_thing primary key, name varchar(50) not null)",
            "create view roundtrip.thing_names as select id, name from roundtrip.thing",
            "create sequence roundtrip.counter as int start with 42 increment by 7",
            "create function roundtrip.double_it(@value int) returns int as begin return @value * 2 end",
            "create procedure roundtrip.touch_thing as begin update roundtrip.thing set name = name end",
            "create type roundtrip.id_list as table (id int not null, label varchar(20) null)",
            "create trigger roundtrip.thing_audit on roundtrip.thing after insert as begin set nocount on end");

        await assertNoDrift(result, "EverythingRoundTrip");
    }

    [Fact]
    public async Task an_index_using_include_where_and_fillfactor_together_round_trips()
    {
        // SQL Server's grammar fixes the clause order as INCLUDE, WHERE, WITH. Weasel emitted
        // WHERE, WITH, INCLUDE, which only breaks on an index that uses more than one at once —
        // so every test that exercised them one at a time passed.
        var result = await scaffold(
            "create table roundtrip.item (id int not null constraint pk_item primary key, group_id int not null, deleted bit not null, note varchar(50) null)",
            "create index ix_item_live on roundtrip.item (group_id) include (note) where deleted = 0 with (fillfactor = 90)");

        var source = result.Files["Tables/RoundtripItemTable.cs"];
        source.ShouldContain("IncludedColumns");
        source.ShouldContain("Predicate");
        source.ShouldContain("FillFactor = 90");

        await assertNoDrift(result, "IncludeWhereFillfactorRoundTrip");
    }

    [Fact]
    public async Task an_index_mixing_ascending_and_descending_columns_round_trips()
    {
        // IndexDefinition.SortOrder is one flag for the whole index and appends a single trailing
        // DESC, so (a, b DESC, c) had no representation at all and came back as all-ascending.
        var result = await scaffold(
            "create table roundtrip.reading (id int not null constraint pk_reading primary key, sensor int not null, taken_at datetime not null, value int not null)",
            "create index ix_reading_mixed on roundtrip.reading (sensor asc, taken_at desc, value asc)");

        result.Files["Tables/RoundtripReadingTable.cs"].ShouldContain("DescendingColumns");

        await assertNoDrift(result, "MixedSortRoundTrip");

        // And the direction actually landed in the database, not just in the model.
        var descending = await theConnection.CreateCommand(@"
select ic.is_descending_key from sys.indexes i
    join sys.tables t on t.object_id = i.object_id
    join sys.index_columns ic on ic.object_id = i.object_id and ic.index_id = i.index_id
    join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id
where t.name = 'reading' and i.name = 'ix_reading_mixed' and c.name = 'taken_at'")
            .ExecuteScalarAsync();

        Convert.ToBoolean(descending).ShouldBeTrue();
    }

    [Fact]
    public async Task a_unique_constraint_round_trips_as_a_unique_index_and_is_reported()
    {
        var result = await scaffold(
            "create table roundtrip.badge (id int not null constraint pk_badge primary key, code varchar(20) not null constraint uq_badge_code unique)");

        result.Files["Tables/RoundtripBadgeTable.cs"].ShouldContain("IsUnique = true");
        result.Notes.ShouldContain(x => x.Category.StartsWith("UNIQUE constraint") && x.Object == "roundtrip.badge.uq_badge_code");

        await assertNoDrift(result, "UniqueConstraintRoundTrip");
    }

    [Fact]
    public async Task an_index_type_weasel_cannot_model_is_ignored_rather_than_mangled()
    {
        var result = await scaffold(
            "create table roundtrip.doc (id int not null constraint pk_doc primary key, payload xml null)",
            "create primary xml index idx_doc_payload on roundtrip.doc (payload)");

        var source = result.Files["Tables/RoundtripDocTable.cs"];

        source.ShouldContain("IgnoreIndex(\"idx_doc_payload\")");
        source.ShouldNotContain("Indexes.Add(new IndexDefinition(\"idx_doc_payload\")");
        result.Notes.ShouldContain(x => x.Object == "roundtrip.doc.idx_doc_payload");

        await assertNoDrift(result, "XmlIndexRoundTrip");
    }

    [Fact]
    public async Task two_tables_whose_names_reduce_to_the_same_identifier_both_survive()
    {
        // Found scaffolding a real database: Report_Parameter and ReportParameter both want to be
        // ReportParameterTable, and the second silently overwrote the first in the file dictionary.
        var result = await scaffold(
            "create table roundtrip.[Report_Parameter] (id int not null constraint pk_rp1 primary key)",
            "create table roundtrip.[ReportParameter] (id int not null constraint pk_rp2 primary key)");

        result.TableCount.ShouldBe(2);
        // One file per table, plus the database class and the project file.
        result.Files.Keys.Count(k => k.StartsWith("Tables/", StringComparison.Ordinal)).ShouldBe(2);

        var database = result.Files["ScaffoldedDatabase.cs"];
        database.ShouldContain("Report_ParameterTable");
        database.ShouldContain("ReportParameterTable");

        await assertNoDrift(result, "NameCollisionRoundTrip");
    }

    [Fact]
    public async Task an_identifier_that_is_not_a_valid_csharp_name_still_produces_a_class()
    {
        var result = await scaffold(
            "create table roundtrip.[order details] (id int not null constraint pk_od primary key, [unit price] decimal(18,2) not null)");

        // The class name is sanitized; the column name is carried verbatim as a string.
        result.Files.Keys.ShouldContain("Tables/RoundtripOrderDetailsTable.cs");
        result.Files["Tables/RoundtripOrderDetailsTable.cs"].ShouldContain("\"unit price\"");

        await assertNoDrift(result, "AwkwardNamesRoundTrip");
    }

    [Fact]
    public async Task a_computed_column_round_trips_through_computed_as()
    {
        var result = await scaffold(
            "create table roundtrip.line (id int not null constraint pk_line primary key, qty int not null, price decimal(18,2) not null, total as (qty * price) persisted, label as ('#' + convert(varchar(10), id)))");

        var source = result.Files["Tables/RoundtripLineTable.cs"];
        source.ShouldContain(".ComputedAs(");
        source.ShouldNotContain("AddColumn(\"total\", \"\")");

        await assertNoDrift(result, "ComputedRoundTrip");
    }

    [Fact]
    public async Task a_system_named_foreign_key_keeps_its_name()
    {
        var result = await scaffold(
            "create table roundtrip.owner (id int not null constraint pk_owner primary key)",
            "create table roundtrip.pet (id int not null constraint pk_pet primary key, owner_id int not null references roundtrip.owner (id))");

        var name = (string)(await theConnection.CreateCommand(
            "select name from sys.foreign_keys where parent_object_id = object_id('roundtrip.pet')").ExecuteScalarAsync())!;

        result.Files["Tables/RoundtripPetTable.cs"].ShouldContain($"new ForeignKey(\"{name}\")");

        await assertNoDrift(result, "SystemNamedForeignKeyRoundTrip");
    }

    [Fact]
    public async Task a_primary_key_name_weasel_refuses_falls_back_to_the_default_name()
    {
        var result = await scaffold(
            "create table roundtrip.odd_key (id int not null constraint [PK_[odd] primary key)");

        var source = result.Files["Tables/RoundtripOddKeyTable.cs"];
        source.ShouldNotContain("PrimaryKeyName");
        result.Notes.ShouldContain(x => x.Category.StartsWith("PRIMARY KEY whose name") && x.Object.StartsWith("roundtrip.odd_key.PK_[odd"));

        await assertNoDrift(result, "RefusedPrimaryKeyNameRoundTrip");
    }

    [Fact]
    public async Task a_clustered_index_beside_a_nonclustered_primary_key_is_left_alone()
    {
        var result = await scaffold(
            "create table roundtrip.ledger (id int not null constraint pk_ledger primary key nonclustered, posted datetime not null)",
            "create clustered index ix_ledger_posted on roundtrip.ledger (posted)");

        result.Files["Tables/RoundtripLedgerTable.cs"].ShouldContain("IgnoreIndex(\"ix_ledger_posted\")");
        result.Notes.ShouldContain(x => x.Object == "roundtrip.ledger.ix_ledger_posted");
        result.Notes.ShouldContain(x => x.Category == "NONCLUSTERED PRIMARY KEY is recreated CLUSTERED");

        await assertNoDrift(result, "NonclusteredKeyRoundTrip");
    }

    [Fact]
    public async Task a_non_default_column_collation_is_carried()
    {
        var result = await scaffold(
            "create table roundtrip.word (id int not null constraint pk_word primary key, text nvarchar(40) collate Danish_Norwegian_CI_AS not null)");

        result.Files["Tables/RoundtripWordTable.cs"].ShouldContain("nvarchar(40) collate Danish_Norwegian_CI_AS");

        await assertNoDrift(result, "CollationRoundTrip");

        await ResetSchema();
        var database = ScaffoldCompilationHarness.BuildDatabase(
            result, "CollationApply", ConnectionSource.ConnectionString, "RoundTrip.Generated");
        await database.ApplyAllConfiguredChangesToDatabaseAsync();

        var collation = await theConnection.CreateCommand(
                "select collation_name from sys.columns where object_id = object_id('roundtrip.word') and name = 'text'")
            .ExecuteScalarAsync();
        collation.ShouldBe("Danish_Norwegian_CI_AS");
    }

    [Fact]
    public async Task a_disabled_trigger_is_recreated_disabled()
    {
        var result = await scaffold(
            "create table roundtrip.audited (id int not null constraint pk_audited primary key)",
            "create trigger roundtrip.audited_insert on roundtrip.audited for insert, delete as begin set nocount on end",
            "disable trigger roundtrip.audited_insert on roundtrip.audited");

        result.Files.Keys.ShouldContain("ScaffoldedTrigger.cs");
        await assertNoDrift(result, "DisabledTriggerRoundTrip");

        await ResetSchema();
        var database = ScaffoldCompilationHarness.BuildDatabase(
            result, "DisabledTriggerApply", ConnectionSource.ConnectionString, "RoundTrip.Generated");
        await database.ApplyAllConfiguredChangesToDatabaseAsync();

        var disabled = await theConnection.CreateCommand(
            "select is_disabled from sys.triggers where name = 'audited_insert'").ExecuteScalarAsync();
        Convert.ToBoolean(disabled).ShouldBeTrue();
    }

    [Fact]
    public async Task a_view_is_created_after_the_view_it_selects_from()
    {
        var result = await scaffold(
            "create table roundtrip.base_row (id int not null constraint pk_base_row primary key)",
            "create view roundtrip.z_inner as select id from roundtrip.base_row",
            "create view roundtrip.a_outer as select id from roundtrip.z_inner");

        var registry = result.Files["ScaffoldedObjects.cs"];
        registry.IndexOf("RoundtripZInnerView", StringComparison.Ordinal)
            .ShouldBeLessThan(registry.IndexOf("RoundtripAOuterView", StringComparison.Ordinal));

        await ResetSchema();
        var database = ScaffoldCompilationHarness.BuildDatabase(
            result, "ViewOrderApply", ConnectionSource.ConnectionString, "RoundTrip.Generated");
        await database.ApplyAllConfiguredChangesToDatabaseAsync();

        (await database.CreateMigrationAsync()).Difference.ShouldBe(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task a_trigger_changed_after_scaffolding_still_reports_drift()
    {
        var result = await scaffold(
            "create table roundtrip.watched (id int not null constraint pk_watched primary key)",
            "create trigger [roundtrip].[watched_insert] on [roundtrip].[watched] for insert as begin set nocount on end");

        await assertNoDrift(result, "TriggerUnchanged");

        await theConnection.CreateCommand(
            "alter trigger [roundtrip].[watched_insert] on [roundtrip].[watched] for insert as begin select 1 end").ExecuteNonQueryAsync();

        var database = ScaffoldCompilationHarness.BuildDatabase(
            result, "TriggerChanged", ConnectionSource.ConnectionString, "RoundTrip.Generated");
        (await database.CreateMigrationAsync()).Difference.ShouldBe(SchemaPatchDifference.Update);
    }
}
