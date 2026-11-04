using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlCompanion.Spikes.S3;

/// <summary>
/// Databases the spike creates and drops itself. Each carries a marker extended property; a database with the
/// same name but without the marker is never touched.
/// </summary>
internal static class ScratchDatabase
{
    private const string Marker = "SqlCompanion.S3";

    public static async Task CreateAsync(string masterConnectionString, string name, CancellationToken cancellationToken)
    {
        await using var master = new SqlConnection(masterConnectionString);
        await master.OpenAsync(cancellationToken);
        if (await ExistsAsync(master, name, cancellationToken))
        {
            if (!await HasMarkerAsync(master, name, cancellationToken))
            {
                throw new InvalidOperationException($"Database [{name}] exists and was not created by this spike; refusing to touch it.");
            }

            await DropAsync(master, name, cancellationToken);
        }

        await ExecAsync(master, $"CREATE DATABASE {Quote(name)};", cancellationToken);
        await ExecAsync(master, $"ALTER DATABASE {Quote(name)} SET RECOVERY SIMPLE;", cancellationToken);
        await ExecAsync(master, $"EXEC {Quote(name)}.sys.sp_addextendedproperty @name = N'{Marker}', @value = N'scratch';", cancellationToken);
    }

    public static async Task DropIfOursAsync(string masterConnectionString, string name, CancellationToken cancellationToken)
    {
        await using var master = new SqlConnection(masterConnectionString);
        await master.OpenAsync(cancellationToken);
        if (await ExistsAsync(master, name, cancellationToken) && await HasMarkerAsync(master, name, cancellationToken))
        {
            SqlConnection.ClearAllPools(); // pooled sessions would hold the database open
            await DropAsync(master, name, cancellationToken);
        }
    }

    /// <summary>
    /// Builds the 10k-object / 100k-column shape from the roadmap: <paramref name="tables"/> tables of
    /// <paramref name="columns"/> columns, each with a primary key, every other table with a foreign key to its
    /// predecessor, plus one view and one three-parameter procedure per ten tables.
    /// </summary>
    public static async Task PopulateSyntheticAsync(string connectionString, int tables, int columns, Action<string> progress, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const int BatchSize = 100;
        var batch = new StringBuilder();
        for (var i = 1; i <= tables; i++)
        {
            batch.Append($"CREATE TABLE dbo.T{i:D5} (id int NOT NULL CONSTRAINT PK_T{i:D5} PRIMARY KEY");
            for (var c = 1; c < columns - 1; c++)
            {
                batch.Append($", c{c:D2} {(c % 3 == 0 ? "int" : c % 3 == 1 ? "nvarchar(50)" : "datetime2")} NULL");
            }

            batch.Append(i % 2 == 0
                ? $", ref_id int NULL CONSTRAINT FK_T{i:D5} REFERENCES dbo.T{i - 1:D5}(id));\n"
                : ", ref_id int NULL);\n");

            if (i % 10 == 0)
            {
                // CREATE VIEW / PROCEDURE must start a batch; EXEC lets several share one round trip.
                batch.Append($"EXEC(N'CREATE VIEW dbo.V{i:D5} AS SELECT * FROM dbo.T{i:D5}');\n");
                batch.Append($"EXEC(N'CREATE PROCEDURE dbo.P{i:D5} @id int, @name nvarchar(50) = NULL, @count int OUTPUT AS SELECT @count = COUNT(*) FROM dbo.T{i:D5} WHERE id = @id');\n");
            }

            if (i % BatchSize == 0 || i == tables)
            {
                await ExecAsync(connection, batch.ToString(), cancellationToken);
                batch.Clear();
                if (i % 1000 == 0 || i == tables)
                {
                    progress($"  synthetic: {i}/{tables} tables");
                }
            }
        }
    }

    public static async Task ExecAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 600 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static async Task<bool> ExistsAsync(SqlConnection master, string name, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name = @name;", master);
        command.Parameters.AddWithValue("@name", name);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<bool> HasMarkerAsync(SqlConnection master, string name, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            $"SELECT COUNT(*) FROM {Quote(name)}.sys.extended_properties WHERE class = 0 AND name = N'{Marker}';", master);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task DropAsync(SqlConnection master, string name, CancellationToken cancellationToken)
    {
        await ExecAsync(master, $"ALTER DATABASE {Quote(name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Quote(name)};", cancellationToken);
    }
}
