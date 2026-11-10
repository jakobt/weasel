using System.Reflection;
using JasperFx;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.SqlServer.Scaffolding;

namespace Weasel.SqlServer.Tests.Scaffolding;

/// <summary>
///     Compiles scaffolder output with Roslyn and instantiates the generated database class.
///     Mirrors <c>Weasel.EntityFrameworkCore.Tests</c>'s <c>InvertedComparisonHarness</c>.
/// </summary>
/// <remarks>
///     Generated source that does not compile is the failure mode most likely to reach a user, and
///     the one least likely to be caught by eyeballing a golden file — a missing <c>using</c> or an
///     ambiguous type name reads fine and does not build.
/// </remarks>
internal static class ScaffoldCompilationHarness
{
    public static Assembly Compile(ScaffoldResult result, string assemblyName, string @namespace)
    {
        var required = new[]
        {
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(AutoCreate).Assembly,
            typeof(ISchemaObject).Assembly,
            typeof(IDatabase).Assembly,
            typeof(SqlServerMigrator).Assembly,
            typeof(Microsoft.Data.SqlClient.SqlConnection).Assembly
        };

        var locations = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => a.Location)
            .Concat(required.Select(a => a.Location))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var references = locations
            .Select(l => (MetadataReference)MetadataReference.CreateFromFile(l))
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName,
            // Files includes a .csproj; only the C# belongs in the compilation.
            result.Files.Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
                .Select(f => CSharpSyntaxTree.ParseText(f.Value)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        // Module bodies ship as embedded .sql, so the in-memory compilation has to carry them too or
        // ScaffoldedSql.Read finds nothing at runtime.
        var resources = result.Files
            .Where(f => f.Key.EndsWith(".sql", StringComparison.Ordinal))
            .Select(f => new ResourceDescription(
                $"{@namespace}.{f.Key.Replace('/', '.')}",
                () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(f.Value)),
                true))
            .ToList();

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream, manifestResources: resources);

        if (!emitted.Success)
        {
            var errors = emitted.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString());

            throw new InvalidOperationException(
                "Scaffolded sources failed to compile:\n" + string.Join("\n", errors) +
                "\n\n---- sources ----\n" +
                string.Join("\n\n", result.Files.Select(f => $"===== {f.Key} =====\n{f.Value}")));
        }

        stream.Seek(0, SeekOrigin.Begin);
        return Assembly.Load(stream.ToArray());
    }

    /// <summary>
    ///     Compile the output and construct the generated database class against a live connection
    ///     string, ready for <c>CreateMigrationAsync</c>.
    /// </summary>
    public static IDatabase BuildDatabase(
        ScaffoldResult result,
        string assemblyName,
        string connectionString,
        string @namespace,
        string databaseClassName = "ScaffoldedDatabase")
    {
        var assembly = Compile(result, assemblyName, @namespace);

        var type = assembly.GetTypes().Single(t => t.Name == databaseClassName);

        return (IDatabase)Activator.CreateInstance(type, connectionString, AutoCreate.CreateOrUpdate)!;
    }
}
