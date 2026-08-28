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

    public string ConnectionString => $"Host={Host};Port={Port};Database={Database};Username={Username};Password={Password}";
}
