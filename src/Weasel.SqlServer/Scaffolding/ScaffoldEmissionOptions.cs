namespace Weasel.SqlServer.Scaffolding;

public sealed record ScaffoldEmissionOptions
{
    public string Namespace { get; init; } = "ScaffoldedSchema";

    public string ClassSuffix { get; init; } = "Table";

    public string DatabaseClassName { get; init; } = "ScaffoldedDatabase";

    public string OtherObjectsClassName { get; init; } = "ScaffoldedObjects";

    public string Directory { get; init; } = "ScaffoldedSchema";

    public bool EmitProjectFile { get; init; } = true;

    public string TargetFramework { get; init; } = "net10.0";

    public string WeaselVersion { get; init; } = RunningWeaselVersion();

    internal static string RunningWeaselVersion()
    {
        var assembly = typeof(ScaffoldEmissionOptions).Assembly;
        var informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        return (informational ?? assembly.GetName().Version!.ToString(3)).Split('+')[0];
    }

    public string? ProjectName { get; init; }

    public string ResolvedProjectName => ProjectName ?? Namespace;
}
