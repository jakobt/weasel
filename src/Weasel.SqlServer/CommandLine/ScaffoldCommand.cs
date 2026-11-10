using JasperFx;
using JasperFx.CommandLine;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using Weasel.SqlServer.Scaffolding;

namespace Weasel.SqlServer.CommandLine;

public class ScaffoldInput
{
    [Description("Connection string for the SQL Server database to reverse engineer")]
    public string ConnectionString { get; set; } = string.Empty;

    [Description("Only scaffold these schemas. Repeat the flag for more than one. Default is every user schema")]
    [FlagAlias("schema", 's')]
    public List<string> SchemaFlag { get; set; } = [];

    [Description("Skip these schemas")]
    [FlagAlias("exclude-schema")]
    public List<string> ExcludeSchemaFlag { get; set; } = [];

    [Description("Only scaffold objects whose name matches this SQL LIKE pattern")]
    [FlagAlias("pattern")]
    public string? PatternFlag { get; set; }

    [Description("Output directory for the generated files. Default is ./ScaffoldedSchema")]
    [FlagAlias("output", 'o')]
    public string? OutputFlag { get; set; }

    [Description("Namespace for the generated files. Default is ScaffoldedSchema")]
    [FlagAlias("namespace")]
    public string? NamespaceFlag { get; set; }

    [Description("Name of the generated database class. Default is ScaffoldedDatabase")]
    [FlagAlias("database-class")]
    public string? DatabaseClassFlag { get; set; }

    [Description("Suffix appended to each generated table class name. Default is 'Table'")]
    [FlagAlias("class-suffix")]
    public string? ClassSuffixFlag { get; set; }

    [Description("Overwrite files that already exist in the output directory")]
    [FlagAlias("overwrite")]
    public bool OverwriteFlag { get; set; }

    [Description("Report what would be written without writing anything")]
    [FlagAlias("dry-run")]
    public bool DryRunFlag { get; set; }

    [Description("Do not emit a .csproj alongside the generated source")]
    [FlagAlias("no-project")]
    public bool NoProjectFlag { get; set; }

    [Description("Target framework for the generated project file. Default is net10.0")]
    [FlagAlias("target-framework")]
    public string? TargetFrameworkFlag { get; set; }

    [Description("Weasel.SqlServer package version referenced by the generated project file. Default is the running version")]
    [FlagAlias("weasel-version")]
    public string? WeaselVersionFlag { get; set; }

    internal SqlServerCatalogFilter ToFilter()
    {
        var filter = new SqlServerCatalogFilter { NameLikePattern = PatternFlag };

        if (SchemaFlag.Any())
        {
            filter = filter with { IncludeSchemas = SchemaFlag.ToArray() };
        }

        if (ExcludeSchemaFlag.Any())
        {
            filter = filter with
            {
                ExcludeSchemas = SqlServerCatalogFilter.DefaultExcludedSchemas
                    .Concat(ExcludeSchemaFlag)
                    .ToArray()
            };
        }

        return filter;
    }

    internal ScaffoldEmissionOptions ToOptions()
    {
        var options = new ScaffoldEmissionOptions { EmitProjectFile = !NoProjectFlag };

        if (TargetFrameworkFlag.IsNotEmpty())
        {
            options = options with { TargetFramework = TargetFrameworkFlag! };
        }

        if (WeaselVersionFlag.IsNotEmpty())
        {
            options = options with { WeaselVersion = WeaselVersionFlag! };
        }

        if (OutputFlag.IsNotEmpty())
        {
            options = options with { Directory = OutputFlag! };
        }

        if (NamespaceFlag.IsNotEmpty())
        {
            options = options with { Namespace = NamespaceFlag! };
        }

        if (DatabaseClassFlag.IsNotEmpty())
        {
            options = options with { DatabaseClassName = DatabaseClassFlag! };
        }

        if (ClassSuffixFlag != null)
        {
            options = options with { ClassSuffix = ClassSuffixFlag };
        }

        return options;
    }
}

[Description(
    "Generates Weasel schema definitions in C# from an existing SQL Server database",
    Name = "db-scaffold")]
public class ScaffoldCommand: JasperFxAsyncCommand<ScaffoldInput>
{
    public ScaffoldCommand()
    {
        Usage("Scaffold an existing database").Arguments(x => x.ConnectionString);
    }

    public override async Task<bool> Execute(ScaffoldInput input)
    {
        JasperFxEnvironment.RunQuiet = true;

        AnsiConsole.Write(new FigletText("Weasel") { Justification = Justify.Left });

        if (input.ConnectionString.IsEmpty())
        {
            AnsiConsole.MarkupLine("[red]A connection string is required.[/]");
            return false;
        }

        var options = input.ToOptions();

        await using var conn = new SqlConnection(input.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        var result = await SqlServerScaffolder
            .ScaffoldAsync(conn, input.ToFilter(), options)
            .ConfigureAwait(false);

        if (result.TableCount == 0 && result.OtherObjectCount == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing matched. Nothing to scaffold.[/]");
            return true;
        }

        writeSummary(result, options, input);

        if (input.DryRunFlag)
        {
            AnsiConsole.MarkupLine("[grey]--dry-run: no files written.[/]");
            return true;
        }

        return await writeFilesAsync(result, options, input).ConfigureAwait(false);
    }

    private static void writeSummary(ScaffoldResult result, ScaffoldEmissionOptions options, ScaffoldInput input)
    {
        // A per-file list runs to thousands of rows on a real database; summarise by folder.
        var table = new Spectre.Console.Table();
        table.AddColumn("Folder");
        table.AddColumn("Files");

        foreach (var group in result.Files.Keys
                     .GroupBy(x => Path.GetDirectoryName(x).IsEmpty() ? "(root)" : Path.GetDirectoryName(x)!)
                     .OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            table.AddRow(Markup.Escape(group.Key), group.Count().ToString());
        }

        AnsiConsole.Write(table);

        if (!result.Notes.Any())
        {
            return;
        }

        AnsiConsole.MarkupLine(
            $"[yellow]{result.Notes.Count} thing(s) in the database the generated code leaves out or reproduces differently:[/]");
        foreach (var category in result.Notes.GroupBy(x => x.Category))
        {
            AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(category.Key)} ({category.Count()})[/]");
            foreach (var note in category)
            {
                AnsiConsole.MarkupLine($"    [grey]{Markup.Escape(note.Object)}[/]");
            }
        }
    }

    private static async Task<bool> writeFilesAsync(
        ScaffoldResult result,
        ScaffoldEmissionOptions options,
        ScaffoldInput input)
    {
        Directory.CreateDirectory(options.Directory);

        var existing = result.Files.Keys
            .Select(name => Path.Combine(options.Directory, name))
            .Where(File.Exists)
            .ToArray();

        if (existing.Any() && !input.OverwriteFlag)
        {
            AnsiConsole.MarkupLine(
                $"[red]{existing.Length} file(s) already exist in {Markup.Escape(options.Directory)}. Re-run with --overwrite to replace them.[/]");
            return false;
        }

        foreach (var (name, content) in result.Files)
        {
            var path = Path.Combine(options.Directory, name);

            // File names carry their folder (Tables/, Views/, ...), so the tree has to exist first.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await File.WriteAllTextAsync(path, content, CancellationToken.None).ConfigureAwait(false);
        }

        var others = result.OtherObjectCount > 0 ? $" and {result.OtherObjectCount} other object(s)" : "";
        AnsiConsole.MarkupLine(
            $"[green]Wrote {result.Files.Count} file(s) for {result.TableCount} table(s){others} to {Markup.Escape(options.Directory)}.[/]");

        return true;
    }
}
