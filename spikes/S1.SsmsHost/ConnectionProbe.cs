using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.Shell;

namespace SqlCompanion.Spikes.S1;

/// <summary>
/// S1 step 6: can the active query window's connection (server, database, authentication kind) be read, and at what
/// cost? SSMS ships no SDK for this. The route add-ins have used for years is
/// <c>ServiceCache.ScriptFactory.CurrentlyActiveWndConnectionInfo.UIConnectionInfo</c> in SSMS's own
/// <c>Microsoft.SqlServer.Management.UI.VSIntegration</c> assembly, which is not redistributable and absent at build
/// time (CI has no SSMS), so it is reached by late binding. Every hop records whether the member is public: the
/// criterion is "no reflection on non-public types", or a documented blast radius.
/// </summary>
/// <remarks>
/// Never reads a password. Advanced options are listed by key only (values may be sensitive), except DATABASE.
/// Server, database and user names are redacted in the log and shown only in the dialog.
/// </remarks>
internal static class ConnectionProbe
{
    private const string AssemblyName = "Microsoft.SqlServer.Management.UI.VSIntegration";
    private const string ServiceCacheTypeName = AssemblyName + ".ServiceCache";

    public static string Describe(EnvDTE.DTE? dte, bool redact)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var lines = new List<string>();
        try
        {
            Probe(lines, redact);
        }
        catch (Exception exception)
        {
            lines.Add($"  probe failed: {exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            DescribeActiveWindowObject(dte, lines, redact);
        }
        catch (Exception exception)
        {
            lines.Add($"  active window object: {exception.GetType().Name}: {exception.Message}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void Probe(List<string> lines, bool redact)
    {
        var serviceCache = FindType(ServiceCacheTypeName, AssemblyName, lines);
        if (serviceCache is null)
        {
            return;
        }

        var scriptFactory = Get(null, serviceCache, "ScriptFactory", lines);
        if (scriptFactory is null)
        {
            return;
        }

        var events = scriptFactory.GetType().GetEvents(BindingFlags.Public | BindingFlags.Instance).Select(e => e.Name).OrderBy(n => n).ToList();
        lines.Add($"  ScriptFactory public events: {(events.Count == 0 ? "(none)" : string.Join(", ", events))}");

        var active = Get(scriptFactory, scriptFactory.GetType(), "CurrentlyActiveWndConnectionInfo", lines);
        if (active is null)
        {
            lines.Add("  (null: activate a connected query window, then run diagnostics again)");
            return;
        }

        lines.Add($"  {active.GetType().Name} public properties: {PublicProperties(active.GetType())}");
        lines.Add($"  Live = {Read(active, "Live") ?? "(missing)"} · IsMultipleConnection = {Read(active, "IsMultipleConnection") ?? "(missing)"}");
        var info = Get(active, active.GetType(), "UIConnectionInfo", lines);
        if (info is null)
        {
            return;
        }

        var infoType = info.GetType();
        lines.Add($"  {infoType.Name} public properties: {PublicProperties(infoType)}");
        lines.Add($"  ServerName = {Show(Read(info, "ServerName"), redact)}");
        lines.Add($"  UserName = {Show(Read(info, "UserName"), redact)}");
        lines.Add($"  AuthenticationType = {Read(info, "AuthenticationType") ?? "(missing)"}");
        if (Read(info, "AdvancedOptions") is NameValueCollection options)
        {
            lines.Add($"  AdvancedOptions keys: {string.Join(", ", options.AllKeys.OrderBy(k => k, StringComparer.Ordinal))}");
            lines.Add($"  AdvancedOptions[DATABASE] = {Show(options["DATABASE"], redact)}");
        }
        else
        {
            lines.Add("  AdvancedOptions: not a NameValueCollection or missing");
        }
    }

    /// <summary>The query window's own automation object — a lead for the <b>current</b> database if UIConnectionInfo only holds the initial one.</summary>
    private static void DescribeActiveWindowObject(EnvDTE.DTE? dte, List<string> lines, bool redact)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var window = dte?.ActiveWindow;
        var value = window?.Object;
        if (value is null)
        {
            lines.Add("  active window object: none");
            return;
        }

        var type = value.GetType();
        var leads = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n.IndexOf("Connect", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Database", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(n => n)
            .ToList();
        lines.Add($"  active window object: {type.FullName} (public type {type.IsVisible}); connection-related public properties: {(leads.Count == 0 ? "(none)" : string.Join(", ", leads))}");
        lines.Add($"  IsConnected = {Read(value, "IsConnected") ?? "(missing)"}");

        var list = Get(value, type, "ConnectionInfoList", lines);
        if (list is System.Collections.ICollection collection)
        {
            lines.Add($"  ConnectionInfoList count = {collection.Count}");
        }

        // The window's own connection: ADO.NET updates Database from the server on USE / drop-down changes, so this
        // is the candidate for the *current* database. Read-only: never opens it, never reads ConnectionString.
        var connection = Get(value, type, "Connection", lines);
        if (connection is System.Data.IDbConnection db)
        {
            lines.Add($"  Connection.State = {db.State} · Connection.Database = {Show(db.Database, redact)}");
            if (connection is System.Data.Common.DbConnection common)
            {
                lines.Add($"  Connection.DataSource = {Show(common.DataSource, redact)}");
            }
        }
        else if (connection is not null)
        {
            lines.Add($"  Connection is not an IDbConnection; public properties: {PublicProperties(connection.GetType())}");
        }
    }

    private static Type? FindType(string fullName, string assemblyName, List<string> lines)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName, throwOnError: false)).FirstOrDefault(t => t is not null);
        if (type is null)
        {
            try
            {
                type = Assembly.Load(assemblyName).GetType(fullName, throwOnError: false);
            }
            catch (Exception exception) when (exception is System.IO.IOException or BadImageFormatException)
            {
                lines.Add($"  {assemblyName}: not loadable ({exception.GetType().Name}: {exception.Message})");
                return null;
            }
        }

        if (type is null)
        {
            lines.Add($"  {fullName}: not found");
            return null;
        }

        var assembly = type.Assembly.GetName();
        lines.Add($"  {type.FullName} in {assembly.Name} {assembly.Version} ({System.IO.Path.GetFileName(type.Assembly.Location)}); public type {type.IsVisible}");
        return type;
    }

    /// <summary>Reads a property, preferring the public surface, and records how it was reached.</summary>
    private static object? Get(object? target, Type type, string name, List<string> lines)
    {
        var scope = target is null ? BindingFlags.Static : BindingFlags.Instance;
        var property = type.GetProperty(name, scope | BindingFlags.Public);
        var access = "public";
        if (property is null)
        {
            property = type.GetProperty(name, scope | BindingFlags.NonPublic);
            access = "NON-PUBLIC";
        }

        if (property is null)
        {
            lines.Add($"  {type.Name}.{name}: not found");
            return null;
        }

        object? value;
        try
        {
            value = property.GetValue(target);
        }
        catch (TargetInvocationException exception)
        {
            lines.Add($"  {type.Name}.{name} ({access}): threw {exception.InnerException?.GetType().Name}: {exception.InnerException?.Message}");
            return null;
        }

        var declared = property.PropertyType;
        lines.Add($"  {type.Name}.{name} ({access}; declared {declared.FullName}, public type {declared.IsVisible}) → {(value is null ? "null" : value.GetType().FullName + (value.GetType().IsVisible ? "" : " (NON-PUBLIC runtime type)"))}");
        return value;
    }

    private static object? Read(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

    private static string PublicProperties(Type type) =>
        string.Join(", ", type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Where(n => n.IndexOf("Password", StringComparison.OrdinalIgnoreCase) < 0).OrderBy(n => n));

    private static string Show(object? value, bool redact) => value switch
    {
        null => "(null)",
        string { Length: 0 } => "(empty)",
        _ when redact => $"<redacted, {value.ToString()!.Length} chars>",
        _ => value.ToString()!,
    };
}
