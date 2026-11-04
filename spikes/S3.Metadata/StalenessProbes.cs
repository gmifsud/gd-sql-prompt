using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace SqlCompanion.Spikes.S3;

/// <summary>
/// Candidate staleness tokens: one round trip each; the catalogue is reloaded when the token changes. The spike
/// measures each candidate's cost and, in the DDL check, which schema changes it detects.
/// </summary>
internal static class StalenessProbes
{
    public static readonly (string Name, string Sql)[] All =
    [
        // The roadmap's starting point. Known blind spot (to be confirmed): dropping an object that is not the most
        // recently modified one leaves MAX unchanged.
        ("P1 max(modify_date)", """
            SELECT CONVERT(varchar(30), MAX(o.modify_date), 126)
            FROM sys.objects AS o
            WHERE o.is_ms_shipped = 0;
            """),

        // Adds a count and an order-independent checksum over (object_id, modify_date) so drops register, plus
        // schemas and user types, which have no modify_date of their own.
        ("P2 objects+schemas+types", """
            SELECT CONCAT(
                (SELECT CONCAT(COUNT_BIG(*), ':', CONVERT(varchar(30), MAX(o.modify_date), 126), ':',
                               CHECKSUM_AGG(CHECKSUM(o.object_id, o.modify_date)))
                 FROM sys.objects AS o WHERE o.is_ms_shipped = 0), '|',
                (SELECT CONCAT(COUNT_BIG(*), ':', CHECKSUM_AGG(CHECKSUM(s.schema_id, s.name))) FROM sys.schemas AS s), '|',
                (SELECT CONCAT(COUNT_BIG(*), ':', CHECKSUM_AGG(CHECKSUM(t.user_type_id, t.name)))
                 FROM sys.types AS t WHERE t.is_user_defined = 1));
            """),

        // P2 plus a checksum over column shape, in case a column change does not touch its table's modify_date.
        // Cost grows with the column count, so it is only worth it if P2 misses something.
        ("P3 P2+columns", """
            SELECT CONCAT(
                (SELECT CONCAT(COUNT_BIG(*), ':', CONVERT(varchar(30), MAX(o.modify_date), 126), ':',
                               CHECKSUM_AGG(CHECKSUM(o.object_id, o.modify_date)))
                 FROM sys.objects AS o WHERE o.is_ms_shipped = 0), '|',
                (SELECT CONCAT(COUNT_BIG(*), ':', CHECKSUM_AGG(CHECKSUM(s.schema_id, s.name))) FROM sys.schemas AS s), '|',
                (SELECT CONCAT(COUNT_BIG(*), ':', CHECKSUM_AGG(CHECKSUM(t.user_type_id, t.name)))
                 FROM sys.types AS t WHERE t.is_user_defined = 1), '|',
                (SELECT CONCAT(COUNT_BIG(*), ':', CHECKSUM_AGG(CHECKSUM(c.object_id, c.column_id, c.name, c.user_type_id,
                                                                         c.max_length, c.precision, c.scale, c.is_nullable)))
                 FROM sys.columns AS c JOIN sys.objects AS o ON o.object_id = c.object_id WHERE o.is_ms_shipped = 0));
            """),
    ];

    public static async Task<string> ReadAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        return (await command.ExecuteScalarAsync(cancellationToken))?.ToString() ?? "(null)";
    }

    /// <summary>Median and p95 latency of each probe over <paramref name="runs"/> calls on a warm connection.</summary>
    public static async Task<IReadOnlyList<(string Name, double MedianMs, double P95Ms)>> MeasureAsync(
        string connectionString, int runs, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var results = new List<(string, double, double)>();
        foreach (var (name, sql) in All)
        {
            await ReadAsync(connection, sql, cancellationToken); // compile the plan; not counted
            var samples = new List<double>(runs);
            for (var i = 0; i < runs; i++)
            {
                var stopwatch = Stopwatch.StartNew();
                await ReadAsync(connection, sql, cancellationToken);
                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            }

            results.Add((name, Stats.Percentile(samples, 0.5), Stats.Percentile(samples, 0.95)));
        }

        return results;
    }
}

internal static class Stats
{
    public static double Percentile(IReadOnlyCollection<double> samples, double p)
    {
        var sorted = samples.Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(p * sorted.Length))];
    }
}
