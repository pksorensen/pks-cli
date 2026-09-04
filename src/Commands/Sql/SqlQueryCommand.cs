using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Sql;

/// <summary>
/// Runs T-SQL against an Azure SQL database with the Entra login from `pks sqlserver init`.
/// No password ever touches the machine: the token is minted per invocation and handed to
/// SqlConnection.AccessToken.
/// </summary>
[Description("Run a query against an Azure SQL database")]
public class SqlQueryCommand : AsyncCommand<SqlQueryCommand.Settings>
{
    private readonly IAzureAuthService _authService;
    private readonly IConfigurationService _configuration;
    private readonly IAnsiConsole _console;

    public SqlQueryCommand(IAzureAuthService authService, IConfigurationService configuration, IAnsiConsole console)
    {
        _authService = authService;
        _configuration = configuration;
        _console = console;
    }

    public class Settings : SqlSettings
    {
        [CommandArgument(0, "[server]")]
        [Description("Server name — \"sql-mc-weu-prd\" or the full host name; omit to use the one from `pks sqlserver init`")]
        public string? Server { get; set; }

        [CommandArgument(1, "[database]")]
        [Description("Database name; omit to use the one from `pks sqlserver init`")]
        public string? Database { get; set; }

        [CommandArgument(2, "[query]")]
        [Description("The T-SQL to run (omit when using --file)")]
        public string? Query { get; set; }

        [CommandOption("-s|--server")]
        [Description("Server, when you'd rather not give it positionally")]
        public string? ServerOption { get; set; }

        [CommandOption("-d|--database")]
        [Description("Database, when you'd rather not give it positionally")]
        public string? DatabaseOption { get; set; }

        [CommandOption("-f|--file")]
        [Description("Read the query from a file instead")]
        public string? File { get; set; }

        [CommandOption("-o|--output")]
        [Description("table (default), json, csv or tsv")]
        public string Output { get; set; } = "table";

        [CommandOption("-n|--max-rows")]
        [Description("Stop after this many rows (default 200, 0 = no limit)")]
        public int MaxRows { get; set; } = 200;

        [CommandOption("--timeout")]
        [Description("Command timeout in seconds (default 60)")]
        public int Timeout { get; set; } = 60;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var (server, database, inlineQuery) = ReadPositionals(settings);

        var defaults = await SqlDefaults.LoadAsync(_configuration);
        server ??= defaults?.Server;
        database ??= defaults?.Database;

        if (string.IsNullOrWhiteSpace(server))
        {
            _console.MarkupLine("[red]No server given, and none stored.[/]");
            _console.MarkupLine("[dim]Name it: [bold]pks sql <server> <database> \"select 1\"[/] — or run [bold]pks sqlserver init[/] once.[/]");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            _console.MarkupLine("[red]No database given, and none stored.[/]");
            _console.MarkupLine("[dim]Add it with [bold]-d <database>[/], or run [bold]pks sqlserver init[/] to pick a default.[/]");
            return 1;
        }

        string sql;
        if (!string.IsNullOrWhiteSpace(settings.File))
        {
            if (!System.IO.File.Exists(settings.File))
            {
                _console.MarkupLine($"[red]Query file not found: {Markup.Escape(settings.File)}[/]");
                return 1;
            }
            sql = await System.IO.File.ReadAllTextAsync(settings.File);
        }
        else if (!string.IsNullOrWhiteSpace(inlineQuery))
        {
            sql = inlineQuery;
        }
        else
        {
            _console.MarkupLine("[red]No query given — pass it as the last argument or use --file.[/]");
            return 1;
        }

        var token = await _authService.GetAccessTokenAsync(SqlAuth.Scope, SqlAuth.StorageKey);
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Not signed in for Azure SQL.[/]");
            _console.MarkupLine("[dim]Run [bold]pks sqlserver init --email you@example.com[/] (or --tenant <id>) first.[/]");
            return 1;
        }

        var host = SqlAuth.ResolveServer(server);
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = host,
            InitialCatalog = database,
            Encrypt = true,
            TrustServerCertificate = false,
            ConnectTimeout = 30,
            ApplicationName = "pks-cli",
        };

        await using var connection = new SqlConnection(builder.ConnectionString) { AccessToken = token };

        try
        {
            await connection.OpenAsync();
        }
        catch (SqlException ex)
        {
            _console.MarkupLine($"[red]{Markup.Escape(ex.Message.Trim())}[/]");
            Explain(ex, host);
            return 1;
        }

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = settings.Timeout };

        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            var any = false;
            do
            {
                if (reader.FieldCount == 0)
                    continue;
                if (any)
                    _console.WriteLine();
                await RenderAsync(reader, settings);
                any = true;
            }
            while (await reader.NextResultAsync());

            if (!any)
                _console.MarkupLine($"[green]{reader.RecordsAffected} row(s) affected.[/]");
        }
        catch (SqlException ex)
        {
            _console.MarkupLine($"[red]{Markup.Escape(ex.Message.Trim())}[/]");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Three positionals mean server, database, query. One means just the query, run against the
    /// server and database `pks sqlserver init` remembered — which is the everyday case once you
    /// have signed in. Two mean server and database, with the query coming from --file.
    /// </summary>
    private static (string? Server, string? Database, string? Query) ReadPositionals(Settings settings)
    {
        var server = settings.ServerOption;
        var database = settings.DatabaseOption;
        string? query = null;

        if (settings.Query != null)
        {
            server ??= settings.Server;
            database ??= settings.Database;
            query = settings.Query;
        }
        else if (settings.Database != null)
        {
            server ??= settings.Server;
            database ??= settings.Database;
        }
        else if (settings.Server != null)
        {
            if (settings.File != null)
                server ??= settings.Server;
            else
                query = settings.Server;
        }

        return (server, database, query);
    }

    /// <summary>
    /// The three ways this fails are all about who you are and where you are calling from, and the
    /// server's own wording points at none of them. Say it plainly instead.
    /// </summary>
    private void Explain(SqlException ex, string host)
    {
        var message = ex.Message;

        if (message.Contains("not currently configured to accept this token", StringComparison.OrdinalIgnoreCase))
        {
            _console.MarkupLine("[yellow]The token is valid but issued by another tenant than the server trusts.[/]");
            _console.MarkupLine("[dim]Sign in again in the server's own tenant: [bold]pks sqlserver init --force --tenant <id>[/][/]");
            return;
        }

        if (message.Contains("Cannot open server", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not allowed to access the server", StringComparison.OrdinalIgnoreCase))
        {
            _console.MarkupLine($"[yellow]{Markup.Escape(host)} is blocking this machine's IP address.[/]");
            _console.MarkupLine("[dim]Add it to the server firewall — the address the server saw is quoted in the message above.[/]");
            return;
        }

        if (message.Contains("Login failed for user", StringComparison.OrdinalIgnoreCase))
        {
            _console.MarkupLine("[yellow]Signed in, but this account has no user in that database.[/]");
            _console.MarkupLine("[dim]It needs CREATE USER [...] FROM EXTERNAL PROVIDER plus a role membership.[/]");
        }
    }

    private async Task RenderAsync(SqlDataReader reader, Settings settings)
    {
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<object?[]>();
        var limit = settings.MaxRows <= 0 ? int.MaxValue : settings.MaxRows;
        var truncated = false;

        while (await reader.ReadAsync())
        {
            if (rows.Count >= limit)
            {
                truncated = true;
                break;
            }

            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(values);
        }

        switch (settings.Output.ToLowerInvariant())
        {
            case "json":
                WriteJson(columns, rows);
                break;
            case "csv":
                WriteSeparated(columns, rows, ',');
                break;
            case "tsv":
                WriteSeparated(columns, rows, '\t');
                break;
            default:
                WriteTable(columns, rows);
                break;
        }

        if (truncated)
            _console.MarkupLine($"[yellow]Stopped at {limit} rows — raise it with --max-rows.[/]");
    }

    private void WriteTable(string[] columns, List<object?[]> rows)
    {
        var table = new Table().Border(TableBorder.Rounded);
        foreach (var column in columns)
            table.AddColumn(new TableColumn($"[bold]{Markup.Escape(column)}[/]"));

        foreach (var row in rows)
            table.AddRow(row.Select(v => Markup.Escape(Format(v))).ToArray());

        _console.Write(table);
        _console.MarkupLine($"[dim]{rows.Count} row(s).[/]");
    }

    private void WriteJson(string[] columns, List<object?[]> rows)
    {
        var records = rows.Select(row =>
        {
            var record = new Dictionary<string, object?>();
            for (var i = 0; i < columns.Length; i++)
                record[columns[i]] = row[i] is byte[] bytes ? Convert.ToBase64String(bytes) : row[i];
            return record;
        }).ToList();

        Console.WriteLine(JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void WriteSeparated(string[] columns, List<object?[]> rows, char separator)
    {
        Console.WriteLine(string.Join(separator, columns.Select(c => Quote(c, separator))));
        foreach (var row in rows)
            Console.WriteLine(string.Join(separator, row.Select(v => Quote(Format(v), separator))));
    }

    private static string Quote(string value, char separator)
    {
        if (!value.Contains(separator) && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
