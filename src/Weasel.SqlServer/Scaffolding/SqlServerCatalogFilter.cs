namespace Weasel.SqlServer.Scaffolding;

public sealed record SqlServerCatalogFilter
{
    public static readonly string[] DefaultExcludedSchemas =
    [
        "sys",
        "INFORMATION_SCHEMA",
        "guest",
        "db_owner",
        "db_accessadmin",
        "db_securityadmin",
        "db_ddladmin",
        "db_backupoperator",
        "db_datareader",
        "db_datawriter",
        "db_denydatareader",
        "db_denydatawriter"
    ];

    public static readonly string[] DefaultExcludedTables =
    [
        "sysdiagrams",
        "__EFMigrationsHistory",
        "__RefactorLog"
    ];

    public string[]? IncludeSchemas { get; init; }

    public string[] ExcludeSchemas { get; init; } = DefaultExcludedSchemas;

    public string? NameLikePattern { get; init; }

    public bool IncludeSystemObjects { get; init; }
}
