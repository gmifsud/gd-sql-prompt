using Microsoft.Data.SqlClient;

namespace SqlCompanion.Spikes.S3;

/// <summary>
/// Truth table: runs one schema change at a time in a scratch database and records which candidate tokens change.
/// A change that leaves a token unchanged is a <b>miss</b> (stale completion); a token that changes on a data-only
/// statement is a <b>false positive</b> (needless reload).
/// </summary>
internal static class DdlCheck
{
    /// <summary>(description, batch, schema change expected). Each batch runs on its own.</summary>
    private static readonly (string Step, string Sql, bool Expected)[] Steps =
    [
        ("create table", "CREATE TABLE dbo.T3 (id int);", true),
        ("add column", "ALTER TABLE dbo.T3 ADD c2 int;", true),
        ("change column type", "ALTER TABLE dbo.T3 ALTER COLUMN c2 bigint;", true),
        ("rename column (sp_rename)", "EXEC sp_rename N'dbo.T3.c2', N'c3', N'COLUMN';", true),
        ("drop column", "ALTER TABLE dbo.T3 DROP COLUMN c3;", true),
        ("rename table (sp_rename)", "EXEC sp_rename N'dbo.T3', N'T3r';", true),
        ("drop an older table", "DROP TABLE dbo.T1;", true),
        ("create view", "CREATE VIEW dbo.V1 AS SELECT a FROM dbo.T2;", true),
        ("alter view (new column)", "ALTER VIEW dbo.V1 AS SELECT a, a AS b FROM dbo.T2;", true),
        ("create procedure", "CREATE PROCEDURE dbo.Pr1 @x int AS SELECT @x;", true),
        ("alter procedure (new parameter)", "ALTER PROCEDURE dbo.Pr1 @x int, @y int AS SELECT @x;", true),
        ("create schema", "CREATE SCHEMA s1;", true),
        ("create table in new schema", "CREATE TABLE s1.T4 (a int);", true),
        ("transfer table to dbo", "ALTER SCHEMA dbo TRANSFER s1.T4;", true),
        ("drop empty schema", "DROP SCHEMA s1;", true),
        ("create user type", "CREATE TYPE dbo.Ty FROM int;", true),
        ("drop user type", "DROP TYPE dbo.Ty;", true),
        ("create synonym", "CREATE SYNONYM dbo.Sy FOR dbo.T2;", true),
        ("drop an older view", "DROP VIEW dbo.V1;", true),
        ("control: insert rows", "INSERT INTO dbo.T2 (a) VALUES (1), (2);", false),
        ("control: update statistics", "UPDATE STATISTICS dbo.T2;", false),
        ("control: no-op", "SELECT 1;", false),
    ];

    public sealed record Row(string Step, bool Expected, IReadOnlyList<bool> Changed);

    public static async Task<IReadOnlyList<Row>> RunAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // T1 is created first so it is never the most recently modified object when it is dropped.
        await ScratchDatabase.ExecAsync(connection, "CREATE TABLE dbo.T1 (a int);", cancellationToken);
        await Task.Delay(50, cancellationToken);
        await ScratchDatabase.ExecAsync(connection, "CREATE TABLE dbo.T2 (a int);", cancellationToken);

        var previous = await ReadTokensAsync(connection, cancellationToken);
        var rows = new List<Row>();
        foreach (var (step, sql, expected) in Steps)
        {
            // modify_date is datetime (≈3 ms resolution); keep consecutive steps in distinct ticks.
            await Task.Delay(20, cancellationToken);
            await ScratchDatabase.ExecAsync(connection, sql, cancellationToken);
            var current = await ReadTokensAsync(connection, cancellationToken);
            rows.Add(new Row(step, expected, current.Select((token, i) => token != previous[i]).ToList()));
            previous = current;
        }

        return rows;
    }

    private static async Task<string[]> ReadTokensAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var tokens = new string[StalenessProbes.All.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            tokens[i] = await StalenessProbes.ReadAsync(connection, StalenessProbes.All[i].Sql, cancellationToken);
        }

        return tokens;
    }
}
