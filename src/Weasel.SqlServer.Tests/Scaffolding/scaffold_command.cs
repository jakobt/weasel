using Shouldly;
using Weasel.SqlServer.CommandLine;
using Xunit;

namespace Weasel.SqlServer.Tests.Scaffolding;

public class scaffold_command: IntegrationContext, IDisposable
{
    private readonly string _output =
        Path.Combine(Path.GetTempPath(), "weasel-scaffold-" + Guid.NewGuid().ToString("N"));

    public scaffold_command(): base("cmd")
    {
    }

    public void Dispose()
    {
        if (Directory.Exists(_output))
        {
            Directory.Delete(_output, true);
        }
    }

    private async Task givenASchema()
    {
        await ResetSchema();
        await theConnection.CreateCommand(
                "create table cmd.widget (id int not null constraint pk_widget primary key, name varchar(50) not null)")
            .ExecuteNonQueryAsync();
    }

    private ScaffoldInput inputFor() => new()
    {
        ConnectionString = ConnectionSource.ConnectionString,
        SchemaFlag = ["cmd"],
        OutputFlag = _output,
        NamespaceFlag = "Cmd.Generated"
    };

    [Fact]
    public async Task writes_the_generated_files()
    {
        await givenASchema();

        var succeeded = await new ScaffoldCommand().Execute(inputFor());

        succeeded.ShouldBeTrue();
        File.Exists(Path.Combine(_output, "Tables", "CmdWidgetTable.cs")).ShouldBeTrue();
        File.Exists(Path.Combine(_output, "ScaffoldedDatabase.cs")).ShouldBeTrue();
        File.Exists(Path.Combine(_output, "Cmd.Generated.csproj")).ShouldBeTrue();

        var version = typeof(SqlServerMigrator).Assembly.GetName().Version!.ToString(3);
        (await File.ReadAllTextAsync(Path.Combine(_output, "Cmd.Generated.csproj")))
            .ShouldContain($"<PackageReference Include=\"Weasel.SqlServer\" Version=\"{version}\" />");

        (await File.ReadAllTextAsync(Path.Combine(_output, "Tables", "CmdWidgetTable.cs")))
            .ShouldContain("namespace Cmd.Generated;");
    }

    [Fact]
    public async Task dry_run_writes_nothing()
    {
        await givenASchema();

        var input = inputFor();
        input.DryRunFlag = true;

        var succeeded = await new ScaffoldCommand().Execute(input);

        succeeded.ShouldBeTrue();
        Directory.Exists(_output).ShouldBeFalse();
    }

    [Fact]
    public async Task refuses_to_clobber_existing_files_without_overwrite()
    {
        await givenASchema();

        (await new ScaffoldCommand().Execute(inputFor())).ShouldBeTrue();

        // Second run against the same directory must stop rather than silently replace hand edits.
        (await new ScaffoldCommand().Execute(inputFor())).ShouldBeFalse();

        var input = inputFor();
        input.OverwriteFlag = true;
        (await new ScaffoldCommand().Execute(input)).ShouldBeTrue();
    }

    [Fact]
    public async Task an_empty_schema_succeeds_without_writing_anything()
    {
        await ResetSchema();

        var succeeded = await new ScaffoldCommand().Execute(inputFor());

        succeeded.ShouldBeTrue();
        Directory.Exists(_output).ShouldBeFalse();
    }

    [Fact]
    public async Task a_missing_connection_string_fails_cleanly()
    {
        var input = inputFor();
        input.ConnectionString = string.Empty;

        (await new ScaffoldCommand().Execute(input)).ShouldBeFalse();
    }
}
