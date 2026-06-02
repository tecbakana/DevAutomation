using System.Text.Json;
using Dapper;
using DevAutomation.Models;
using Microsoft.Data.Sqlite;

namespace DevAutomation.Services.Store;

public sealed class SqliteDevRequestStore : IDevRequestStore
{
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions _writeOpts = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions _readOpts  = new() { PropertyNameCaseInsensitive = true };

    public string? WatchDirectory => null;

    public SqliteDevRequestStore(string connectionString)
    {
        _connectionString = connectionString;
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var conn = OpenConnection();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS dev_requests (
                id        TEXT PRIMARY KEY,
                payload   TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                status    TEXT NOT NULL
            )
            """);
    }

    public async Task<IEnumerable<DevRequest>> ListAllAsync()
    {
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<string>(
            "SELECT payload FROM dev_requests ORDER BY timestamp DESC");

        var all = rows
            .Select(j => { try { return JsonSerializer.Deserialize<DevRequest>(j, _readOpts); } catch { return null; } })
            .Where(r => r is not null)
            .Cast<DevRequest>()
            .ToList();

        ApplyBloqueado(all);
        return all;
    }

    public async Task<DevRequest?> GetByIdAsync(string id)
    {
        using var conn = OpenConnection();
        var json = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT payload FROM dev_requests WHERE id = @id", new { id });
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<DevRequest>(json, _readOpts); } catch { return null; }
    }

    public async Task SaveAsync(DevRequest request)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO dev_requests (id, payload, timestamp, status)
            VALUES (@id, @payload, @timestamp, @status)
            ON CONFLICT(id) DO UPDATE SET payload = @payload, timestamp = @timestamp, status = @status
            """,
            new
            {
                id        = request.Id,
                payload   = JsonSerializer.Serialize(request, _writeOpts),
                timestamp = (request.TimestampAtualizacao ?? request.Timestamp).ToString("o"),
                status    = request.Status
            });
    }

    public async Task DeleteAsync(string id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("DELETE FROM dev_requests WHERE id = @id", new { id });
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void ApplyBloqueado(List<DevRequest> all)
    {
        var doneIds = all.Where(r => r.Status == "done").Select(r => r.Id).ToHashSet();
        foreach (var r in all)
            r.Bloqueado = r.Dependencias?.Any(d => !doneIds.Contains(d)) ?? false;
    }
}
