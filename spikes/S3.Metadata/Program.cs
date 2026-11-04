using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlCompanion.Spikes.S3;

// Spike S3 — catalogue load cost and staleness-probe viability on SQL Server (spec §13: 10k objects / 100k columns).
// The report holds counts, timings and server version only: no object, column or database names.
//
// Usage: dotnet run -c Release --project spikes/S3.Metadata -- --server "(local)\SQL2025" --trust-server-certificate [options]
//   --all                       = --ddl-check --synthetic plus your databases
//   --ddl-check                 which schema changes each staleness probe detects (scratch database, dropped after)
//   --synthetic                 build a 10k-object / 100k-column scratch database and time loads against it
//   --tables N --columns M      synthetic shape (default 5000 x 20)
//   --databases a,b             your databases to time (default: every online user database); --no-user-databases skips them
//   --runs N                    warm repetitions per measurement (default 5)
//   --user NAME                 SQL authentication; password from SQLCOMPANION_S3_PASSWORD (default: Windows authentication)
//   --keep                      keep the scratch databases
//   --out DIR                   report folder (default artifacts/s3)

Console.OutputEncoding = Encoding.UTF8;
var options = Options.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("usage: S3.Metadata --server <name> [--trust-server-certificate] [--all | --ddl-check --synthetic] [--databases a,b | --no-user-databases] [--runs N] [--user NAME] [--keep] [--out DIR]");
    return 2;
}

const string DdlDatabase = "SqlCompanion_S3_DdlCheck";
const string SyntheticDatabase = "SqlCompanion_S3_Synthetic";
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var token = cancellation.Token;
var report = new Report();

try
{
    var master = options.ConnectionString("master");
    await using (var connection = new SqlConnection(master))
    {
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            "SELECT CONCAT(CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')), N' · ', CONVERT(nvarchar(128), SERVERPROPERTY('Edition')), N' · compat ', (SELECT compatibility_level FROM sys.databases WHERE name = N'master'));",
            connection);
        report.Server = (string?)await command.ExecuteScalarAsync(token) ?? "?";
    }

    report.Client = $"{Environment.ProcessorCount} logical CPUs · {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} · Microsoft.Data.SqlClient {SqlClientVersion()}";
    report.Authentication = options.User is null ? "Windows" : "SQL";
    Console.WriteLine($"Server {report.Server}");

    if (options.DdlCheck)
    {
        Console.WriteLine("DDL check (scratch database)...");
        await ScratchDatabase.CreateAsync(master, DdlDatabase, token);
        try
        {
            report.DdlRows = await DdlCheck.RunAsync(options.ConnectionString(DdlDatabase), token);
        }
        finally
        {
            if (!options.Keep)
            {
                await ScratchDatabase.DropIfOursAsync(master, DdlDatabase, token);
            }
        }
    }

    if (options.Synthetic)
    {
        Console.WriteLine($"Synthetic database: {options.Tables} tables x {options.Columns} columns...");
        await ScratchDatabase.CreateAsync(master, SyntheticDatabase, token);
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            await ScratchDatabase.PopulateSyntheticAsync(options.ConnectionString(SyntheticDatabase), options.Tables, options.Columns, Console.WriteLine, token);
            report.SyntheticBuildSeconds = started.Elapsed.TotalSeconds;
            report.Synthetic = await MeasureAsync("synthetic", options.ConnectionString(SyntheticDatabase), options.Runs, token);
        }
        finally
        {
            if (!options.Keep)
            {
                await ScratchDatabase.DropIfOursAsync(master, SyntheticDatabase, token);
            }
        }
    }

    if (options.UserDatabases)
    {
        var names = options.Databases ?? await UserDatabasesAsync(master, token);
        var index = 0;
        foreach (var name in names.Order(StringComparer.OrdinalIgnoreCase))
        {
            var label = $"db{++index}";
            Console.WriteLine($"Measuring {label}...");
            try
            {
                report.UserDatabases.Add(await MeasureAsync(label, options.ConnectionString(name), Math.Min(options.Runs, 3), token));
            }
            catch (SqlException exception)
            {
                report.UserDatabases.Add(new Measurement(label) { Error = $"SQL error {exception.Number}" }); // message may contain names
            }
        }
    }
}
catch (Exception exception) // report any failure rather than crash without a report
{
    Console.Error.WriteLine($"FAILED: {exception.GetType().Name}: {exception.Message}");
    report.Failure = exception.GetType().Name + (exception is SqlException sql ? $" {sql.Number}" : "");
}

Directory.CreateDirectory(options.OutDir);
var markdown = report.ToMarkdown();
await File.WriteAllTextAsync(Path.Combine(options.OutDir, "s3-report.md"), markdown, token);
await File.WriteAllTextAsync(Path.Combine(options.OutDir, "s3-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }), token);
Console.WriteLine();
Console.WriteLine(markdown);
Console.WriteLine($"Report: {Path.Combine(options.OutDir, "s3-report.md")}");
return report.Failure is null ? 0 : 1;

static async Task<Measurement> MeasureAsync(string label, string connectionString, int runs, CancellationToken token)
{
    var cold = await CatalogueLoader.LoadSequentialAsync(connectionString, cold: true, token);
    var warm = new List<double>();
    var parallel = new List<double>();
    double retained = cold.RetainedMegabytes;
    for (var i = 0; i < runs; i++)
    {
        var run = await CatalogueLoader.LoadSequentialAsync(connectionString, cold: false, token);
        warm.Add(run.SequentialMs);
        retained = Math.Max(retained, run.RetainedMegabytes);
        parallel.Add(await CatalogueLoader.LoadParallelAsync(connectionString, token));
    }

    return new Measurement(label)
    {
        Objects = cold.Rows("objects"),
        Columns = cold.Rows("columns"),
        Parameters = cold.Rows("parameters"),
        ForeignKeyColumns = cold.Rows("foreign keys"),
        ColdOpenMs = cold.OpenMs,
        ColdQueries = cold.Queries,
        ColdSequentialMs = cold.SequentialMs,
        WarmSequentialMedianMs = Stats.Percentile(warm, 0.5),
        WarmSequentialP95Ms = Stats.Percentile(warm, 0.95),
        ParallelMedianMs = Stats.Percentile(parallel, 0.5),
        RetainedMegabytes = retained,
        Probes = await StalenessProbes.MeasureAsync(connectionString, 20, token),
    };
}

// The assembly version stays 7.0.0.0 across 7.x; the informational version is the package version.
static string SqlClientVersion() =>
    (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(SqlConnection).Assembly)?.InformationalVersion ?? "?").Split('+')[0];

static async Task<List<string>> UserDatabasesAsync(string master, CancellationToken token)
{
    await using var connection = new SqlConnection(master);
    await connection.OpenAsync(token);
    await using var command = new SqlCommand(
        "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 AND user_access = 0 AND HAS_DBACCESS(name) = 1 AND name NOT LIKE N'SqlCompanion[_]S3[_]%';",
        connection);
    await using var reader = await command.ExecuteReaderAsync(token);
    var names = new List<string>();
    while (await reader.ReadAsync(token))
    {
        names.Add(reader.GetString(0));
    }

    return names;
}

internal sealed record Options(
    string Server, string? User, bool TrustServerCertificate, bool DdlCheck, bool Synthetic, int Tables, int Columns,
    bool UserDatabases, IReadOnlyList<string>? Databases, int Runs, bool Keep, string OutDir)
{
    public string ConnectionString(string database)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = database,
            TrustServerCertificate = TrustServerCertificate,
            ApplicationName = "SqlCompanion.S3",
            ConnectTimeout = 15,
        };
        if (User is null)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = User;
            builder.Password = Environment.GetEnvironmentVariable("SQLCOMPANION_S3_PASSWORD") ?? "";
        }

        return builder.ConnectionString;
    }

    public static Options? Parse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        bool Flag(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        int Number(string name, int fallback) => int.TryParse(Value(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : fallback;

        var server = Value("--server");
        if (string.IsNullOrWhiteSpace(server))
        {
            return null;
        }

        var all = Flag("--all");
        return new Options(
            server,
            Value("--user"),
            Flag("--trust-server-certificate"),
            all || Flag("--ddl-check"),
            all || Flag("--synthetic"),
            Number("--tables", 5000),
            Math.Max(3, Number("--columns", 20)),
            !Flag("--no-user-databases"),
            Value("--databases")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Number("--runs", 5),
            Flag("--keep"),
            Path.GetFullPath(Value("--out") ?? "artifacts/s3"));
    }
}

internal sealed record Measurement(string Label)
{
    public int Objects { get; init; }
    public int Columns { get; init; }
    public int Parameters { get; init; }
    public int ForeignKeyColumns { get; init; }
    public double ColdOpenMs { get; init; }
    public IReadOnlyList<QueryTiming> ColdQueries { get; init; } = [];
    public double ColdSequentialMs { get; init; }
    public double WarmSequentialMedianMs { get; init; }
    public double WarmSequentialP95Ms { get; init; }
    public double ParallelMedianMs { get; init; }
    public double RetainedMegabytes { get; init; }
    public IReadOnlyList<(string Name, double MedianMs, double P95Ms)> Probes { get; init; } = [];
    public string? Error { get; init; }
}

internal sealed class Report
{
    public string Server { get; set; } = "?";
    public string Client { get; set; } = "?";
    public string Authentication { get; set; } = "?";
    public IReadOnlyList<DdlCheck.Row> DdlRows { get; set; } = [];
    public double SyntheticBuildSeconds { get; set; }
    public Measurement? Synthetic { get; set; }
    public List<Measurement> UserDatabases { get; } = [];
    public string? Failure { get; set; }

    public string ToMarkdown()
    {
        var md = new StringBuilder();
        md.AppendLine("# S3 metadata report").AppendLine();
        md.AppendLine($"- Server: {Server} ({Authentication} authentication)");
        md.AppendLine($"- Client: {Client}");
        if (Failure is not null)
        {
            md.AppendLine($"- **Run failed:** {Failure}");
        }

        if (DdlRows.Count > 0)
        {
            md.AppendLine().AppendLine("## DDL check — does the token change?").AppendLine();
            md.AppendLine("✅ detected · ❌ **missed** (stale catalogue) · ⚠️ false positive (needless reload) · — correctly unchanged").AppendLine();
            md.AppendLine("| Step | " + string.Join(" | ", StalenessProbes.All.Select(p => p.Name)) + " |");
            md.AppendLine("|---|" + string.Concat(StalenessProbes.All.Select(_ => "---|")));
            foreach (var row in DdlRows)
            {
                md.AppendLine($"| {row.Step} | " + string.Join(" | ", row.Changed.Select(changed => (row.Expected, changed) switch
                {
                    (true, true) => "✅",
                    (true, false) => "❌",
                    (false, true) => "⚠️",
                    _ => "—",
                })) + " |");
            }

            md.AppendLine();
            for (var i = 0; i < StalenessProbes.All.Length; i++)
            {
                var misses = DdlRows.Count(r => r.Expected && !r.Changed[i]);
                var falsePositives = DdlRows.Count(r => !r.Expected && r.Changed[i]);
                md.AppendLine($"- {StalenessProbes.All[i].Name}: {misses} miss(es), {falsePositives} false positive(s)");
            }
        }

        if (Synthetic is not null)
        {
            md.AppendLine().AppendLine($"## Synthetic database (built in {SyntheticBuildSeconds:0} s)").AppendLine();
            AppendMeasurements(md, [Synthetic]);
            md.AppendLine().AppendLine("Cold run per query:").AppendLine();
            md.AppendLine("| Query | Rows | First row ms | Total ms |").AppendLine("|---|---:|---:|---:|");
            foreach (var q in Synthetic.ColdQueries)
            {
                md.AppendLine($"| {q.Name} | {q.Rows:N0} | {(double.IsNaN(q.FirstRowMs) ? "—" : q.FirstRowMs.ToString("0.0", CultureInfo.InvariantCulture))} | {q.TotalMs:0.0} |");
            }
        }

        if (UserDatabases.Count > 0)
        {
            md.AppendLine().AppendLine("## Your databases (names omitted)").AppendLine();
            AppendMeasurements(md, UserDatabases);
        }

        return md.ToString();
    }

    private static void AppendMeasurements(StringBuilder md, IReadOnlyList<Measurement> rows)
    {
        var probeNames = StalenessProbes.All.Select(p => p.Name.Split(' ')[0]).ToArray();
        md.AppendLine("| Database | Objects | Columns | Params | FK cols | Cold open ms | Cold load ms | Warm load median / p95 ms | Parallel median ms | Retained MB | " +
                      string.Join(" | ", probeNames.Select(p => $"{p} median / p95 ms")) + " |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|" + string.Concat(probeNames.Select(_ => "---:|")));
        foreach (var m in rows)
        {
            if (m.Error is not null)
            {
                md.AppendLine($"| {m.Label} | {m.Error} |");
                continue;
            }

            md.AppendLine($"| {m.Label} | {m.Objects:N0} | {m.Columns:N0} | {m.Parameters:N0} | {m.ForeignKeyColumns:N0} | {m.ColdOpenMs:0} | {m.ColdSequentialMs:0} | " +
                          $"{m.WarmSequentialMedianMs:0} / {m.WarmSequentialP95Ms:0} | {m.ParallelMedianMs:0} | {m.RetainedMegabytes:0.0} | " +
                          string.Join(" | ", m.Probes.Select(p => $"{p.MedianMs:0.0} / {p.P95Ms:0.0}")) + " |");
        }
    }
}
