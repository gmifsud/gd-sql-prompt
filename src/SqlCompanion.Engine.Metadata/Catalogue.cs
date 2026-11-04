using System.Collections.Immutable;

namespace SqlCompanion.Engine.Metadata;

/// <summary>Identifies the scope of a metadata cache: one database on one server.</summary>
public sealed record ConnectionKey(string Server, string Database);

public enum DbObjectKind
{
    Table,
    View,
    Procedure,
    ScalarFunction,
    TableValuedFunction,
    Synonym,
}

public sealed record DbColumn(string Name, string DataType, bool IsNullable, int Ordinal);

public sealed record DbObject(string Schema, string Name, DbObjectKind Kind, ImmutableArray<DbColumn> Columns);

/// <summary>
/// An immutable catalogue snapshot. Refresh produces a new snapshot that replaces the old one
/// atomically, so readers never observe a partially refreshed catalogue.
/// </summary>
/// <param name="Connection">The database this snapshot describes.</param>
/// <param name="StalenessToken">Opaque value compared against the provider's current token to detect change.</param>
/// <param name="LoadedAt">When the snapshot was loaded; surfaced to users when assistance relies on stale metadata.</param>
/// <param name="Objects">Accessible objects. Objects the login cannot see are absent, not errors.</param>
public sealed record CatalogueSnapshot(
    ConnectionKey Connection,
    string StalenessToken,
    DateTimeOffset LoadedAt,
    ImmutableArray<DbObject> Objects);

/// <summary>Database-specific metadata loader (spike S3 implements SQL Server / Azure SQL).</summary>
public interface ICatalogueProvider
{
    ValueTask<CatalogueSnapshot> LoadAsync(ConnectionKey connection, CancellationToken cancellationToken);

    /// <summary>
    /// Cheap probe used to decide whether to reload. Not <c>MAX(modify_date)</c> alone: spike S3 showed it misses drops of
    /// non-latest objects and schema / user-type changes; the SQL Server token adds counts and checksums (S3 token P2).
    /// </summary>
    ValueTask<string> GetStalenessTokenAsync(ConnectionKey connection, CancellationToken cancellationToken);
}
