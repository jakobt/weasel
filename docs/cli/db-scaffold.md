# db-scaffold

Generates Weasel schema definitions in C# from an **existing** SQL Server database.

Weasel is normally model-first: you hand-write `Table` objects and Weasel diffs them against the
database. `db-scaffold` runs the other direction, so an existing database can be adopted into Weasel
without transcribing every table, index and constraint by hand.

```bash
dotnet run -- db-scaffold "Server=localhost;Database=northwind;..." --schema dbo --output ./Schema
```

The command is defined in `Weasel.SqlServer`, so it appears in any application that references that
package.

## What it writes

A buildable project that references the `Weasel.SqlServer` package version that ran the command, one
`partial class` per table, and a database class that wires them together:

```csharp
public partial class OrdersTable: Table
{
    public OrdersTable(): base(new SqlServerObjectName("dbo", "orders"))
    {
        AddColumn("id", "int").AsPrimaryKey().AutoIncrement();
        AddColumn("customer_id", "int").NotNull();
        AddColumn("total", "decimal(18,2)").NotNull().DefaultValueByExpression("((0))");
        AddColumn("total_with_tax", "decimal(19,2)").ComputedAs("([total]*(1.25))", true);
        AddColumn("note", "nvarchar(200) collate Danish_Norwegian_CI_AS").AllowNulls();

        PrimaryKeyName = "pk_orders";

        Indexes.Add(new IndexDefinition("idx_orders_customer") { Columns = ["customer_id"] });

        ForeignKeys.Add(new ForeignKey("fk_orders_customer")
        {
            LinkedTable = new SqlServerObjectName("dbo", "customer"),
            ColumnNames = ["customer_id"],
            LinkedNames = ["id"],
            DeleteAction = Weasel.Core.CascadeAction.Cascade,
            UpdateAction = Weasel.Core.CascadeAction.NoAction
        });
    }
}
```

The classes are `partial` so hand-written additions survive a re-scaffold, and tables are added to the
database class in **foreign key dependency order**.

Store types are always explicit. `AddColumn<decimal>("total")` maps through `SqlServerProvider`, which
covers a handful of CLR types and falls back to `nvarchar(max)`; it would silently change the type.

A column whose collation differs from the database default carries it in the type string, which
Weasel's comparison ignores.

## Non-table objects

Views, sequences, synonyms, functions, stored procedures, table types and triggers each get their own
file, built through the matching Weasel model. Module bodies are written as embedded `.sql` files next to
the C#, because a large database exceeds the CLR's limit on string literals in one assembly.

```csharp
public static class ActiveCustomersView
{
    public static View Build()
        => new View(new SqlServerObjectName("dbo", "active_customers"), ScaffoldedSql.Read("Views.ActiveCustomersView.sql"));
}
```

`ScaffoldedObjects.All()` yields them in creation order — each after the modules it references — and
a generated `FeatureSchemaBase` adds them to the migration alongside the tables.

Triggers are emitted as `ScaffoldedTrigger`, a small generated subclass of Weasel's `Trigger`. It keeps
a disabled trigger disabled, and it treats the source database's own spelling of the trigger
(`FOR INSERT`, bracketed names) as unchanged, which `Trigger` alone would report as drift.

## What it cannot reproduce

The command prints everything in the database that the generated code leaves out or reproduces
differently, grouped by kind. Nothing is dropped silently. The main ones:

- `WITH NOCHECK`, disabled and `NOT FOR REPLICATION` foreign keys are recreated as ordinary ones.
- A `NONCLUSTERED` primary key is recreated clustered. A clustered index beside one is left unmanaged
  with `IgnoreIndex`, since both cannot be clustered.
- A `UNIQUE` constraint is recreated as a unique index.
- `ROWGUIDCOL`, `SPARSE`, a non-default `IDENTITY` seed or increment, named `DEFAULT` constraints,
  extended properties, `WITH SCHEMABINDING` and indexes on views.
- Columnstore, XML, spatial and hash indexes, and index or key names Weasel refuses (a quote or a
  bracket in the name), are left unmanaged with `IgnoreIndex`. A refused primary key name falls back to
  Weasel's default name.

## Flags

| Flag | Description |
|------|-------------|
| `--schema`, `-s` | Only scaffold these schemas. Repeatable. Default is every user schema |
| `--exclude-schema` | Skip these schemas, in addition to the built-in system schemas |
| `--pattern` | Only scaffold objects whose name matches this SQL `LIKE` pattern |
| `--output`, `-o` | Output directory. Default `./ScaffoldedSchema` |
| `--namespace` | Namespace for the generated files. Default `ScaffoldedSchema` |
| `--database-class` | Name of the generated database class. Default `ScaffoldedDatabase` |
| `--class-suffix` | Suffix on each table class name. Default `Table` |
| `--target-framework` | Target framework of the generated project. Default `net10.0` |
| `--weasel-version` | `Weasel.SqlServer` version the generated project references. Default is the running version |
| `--no-project` | Do not write a `.csproj` |
| `--overwrite` | Replace files that already exist in the output directory |
| `--dry-run` | Report what would be written without writing anything |

Schemas belonging to the fixed database roles (`db_owner`, `db_datareader`, …) plus `sys` and
`INFORMATION_SCHEMA` are always skipped, as are `is_ms_shipped` objects and tooling tables such as
`__EFMigrationsHistory` and `sysdiagrams`.

## Adopting a database

```bash
dotnet run -- db-scaffold "$CONN" --schema dbo --dry-run
dotnet run -- db-scaffold "$CONN" --schema dbo --output ./Schema --namespace MyApp.Schema
dotnet run -- db-assert
```

Tables are read through `Table.FetchExistingAsync`, the same reader delta detection uses, so the last
step should report no changes against the database the code came from.
