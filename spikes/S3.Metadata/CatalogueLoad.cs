using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace SqlCompanion.Spikes.S3;

/// <summary>
/// The catalogue queries a SQL Server provider would run for completion and analysis, one round trip each.
/// Only user objects (<c>is_ms_shipped = 0</c>): system catalogue views ship with the product and are static.
/// </summary>
internal static class CatalogueQueries
{
    public static readonly (string Name, string Sql)[] All =
    [
        ("objects", """
            SELECT o.object_id, o.schema_id, o.name, o.type, o.parent_object_id, o.modify_date
            FROM sys.objects AS o
            WHERE o.is_ms_shipped = 0;
            """),
        ("columns", """
            SELECT c.object_id, c.column_id, c.name, c.user_type_id, c.max_length, c.precision, c.scale,
                   c.is_nullable, c.is_identity, c.is_computed
            FROM sys.columns AS c
            JOIN sys.objects AS o ON o.object_id = c.object_id
            WHERE o.is_ms_shipped = 0;
            """),
        ("parameters", """
            SELECT p.object_id, p.parameter_id, p.name, p.user_type_id, p.max_length, p.is_output, p.has_default_value
            FROM sys.parameters AS p
            JOIN sys.objects AS o ON o.object_id = p.object_id
            WHERE o.is_ms_shipped = 0;
            """),
        ("foreign keys", """
            SELECT fkc.constraint_object_id, fk.parent_object_id, fk.referenced_object_id,
                   fkc.constraint_column_id, fkc.parent_column_id, fkc.referenced_column_id
            FROM sys.foreign_key_columns AS fkc
            JOIN sys.foreign_keys AS fk ON fk.object_id = fkc.constraint_object_id;
            """),
        ("schemas", "SELECT s.schema_id, s.name FROM sys.schemas AS s;"),
        ("types", "SELECT t.user_type_id, t.system_type_id, t.schema_id, t.name, t.is_user_defined, t.is_table_type FROM sys.types AS t;"),
        ("synonyms", "SELECT s.object_id, s.base_object_name FROM sys.synonyms AS s;"),
    ];
}

internal sealed record QueryTiming(string Name, int Rows, double FirstRowMs, double TotalMs);

internal sealed record LoadResult(double OpenMs, IReadOnlyList<QueryTiming> Queries, double SequentialMs, double RetainedMegabytes)
{
    public int Rows(string name) => Queries.First(q => q.Name == name).Rows;
}

internal static class CatalogueLoader
{
    /// <summary>
    /// Runs every catalogue query sequentially on one connection, materialising rows the way a snapshot would
    /// (boxed values in row arrays: an upper bound on what a typed snapshot retains).
    /// </summary>
    /// <param name="cold">Clears the connection pool first, so the open includes TCP, TLS and authentication.</param>
    public static async Task<LoadResult> LoadSequentialAsync(string connectionString, bool cold, CancellationToken cancellationToken)
    {
        if (cold)
        {
            SqlConnection.ClearAllPools();
        }

        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var retained = new List<object[]>[CatalogueQueries.All.Length];
        var timings = new List<QueryTiming>();

        var open = Stopwatch.StartNew();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var openMs = open.Elapsed.TotalMilliseconds;

        var total = Stopwatch.StartNew();
        for (var i = 0; i < CatalogueQueries.All.Length; i++)
        {
            var (name, sql) = CatalogueQueries.All[i];
            var (rows, timing) = await RunAsync(connection, name, sql, cancellationToken);
            retained[i] = rows;
            timings.Add(timing);
        }

        var sequentialMs = total.Elapsed.TotalMilliseconds;
        var retainedBytes = GC.GetTotalMemory(forceFullCollection: true) - baseline;
        GC.KeepAlive(retained);
        return new LoadResult(openMs, timings, sequentialMs, Math.Max(0, retainedBytes) / (1024.0 * 1024.0));
    }

    /// <summary>Every query on its own pooled connection, concurrently: the wall time a provider could reach.</summary>
    public static async Task<double> LoadParallelAsync(string connectionString, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        await Task.WhenAll(CatalogueQueries.All.Select(async query =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await RunAsync(connection, query.Name, query.Sql, cancellationToken);
        }));
        return total.Elapsed.TotalMilliseconds;
    }

    private static async Task<(List<object[]> Rows, QueryTiming Timing)> RunAsync(SqlConnection connection, string name, string sql, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<object[]>();
        var firstRowMs = double.NaN;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count == 0)
            {
                firstRowMs = stopwatch.Elapsed.TotalMilliseconds;
            }

            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }

        return (rows, new QueryTiming(name, rows.Count, firstRowMs, stopwatch.Elapsed.TotalMilliseconds));
    }
}
