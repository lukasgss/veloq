using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Veloq.Data;

namespace Veloq.ViewModels;

internal static class ConnectionStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Veloq");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "connections.json");

    private static readonly JsonSerializerOptions _jsonSerializerOptions = new() { WriteIndented = true };

    public static IReadOnlyList<ConnectionInfo> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        string json = File.ReadAllText(FilePath);
        var stored = JsonSerializer.Deserialize<IReadOnlyCollection<StoredConnection>>(json) ?? [];

        return stored.Select(item => new ConnectionInfo
        {
            Name = item.Name,
            Host = item.Host,
            Port = item.Port,
            Database = item.Database,
            Username = item.Username,
            Password = item.Password,
            Runner = new QueryRunner(ConnectionInfo.BuildConnectionString(
                item.Host, item.Port, item.Database, item.Username, item.Password)),
        }).ToList();
    }

    public static void Save(IEnumerable<ConnectionInfo> connections)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(DirectoryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        IReadOnlyCollection<StoredConnection> stored = connections.Select(connection => new StoredConnection
        {
            Name = connection.Name,
            Host = connection.Host,
            Port = connection.Port,
            Database = connection.Database,
            Username = connection.Username,
            Password = connection.Password,
        }).ToList();

        string json = JsonSerializer.Serialize(stored, _jsonSerializerOptions);
        string temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, json);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporaryPath, FilePath, overwrite: true);
    }

    private sealed class StoredConnection
    {
        public string Name { get; init; } = string.Empty;
        public string Host { get; init; } = string.Empty;
        public string Port { get; init; } = string.Empty;
        public string Database { get; init; } = string.Empty;
        public string Username { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
    }
}
