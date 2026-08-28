using Npgsql;
using Veloq.Data;

namespace Veloq.ViewModels;

public sealed class ConnectionInfo
{
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required string Port { get; init; }
    public required string Database { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required QueryRunner Runner { get; init; }

    public string Subtitle => $"{Host}:{Port}/{Database}";

    public string ConnectionString => BuildConnectionString(Host, Port, Database, Username, Password);

    public static string BuildConnectionString(
        string host,
        string port,
        string database,
        string username,
        string password) => new NpgsqlConnectionStringBuilder
    {
        Host = host,
        Port = int.Parse(port),
        Database = database,
        Username = username,
        Password = password,
    }.ConnectionString;
}
