using System.Text.Json;
using DevAutomation.Models;

namespace DevAutomation.Services.Store;

public sealed class JsonFileDevRequestStore : IDevRequestStore
{
    private static readonly JsonSerializerOptions _writeOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions _readOpts  = new() { PropertyNameCaseInsensitive = true };

    public string? WatchDirectory { get; }

    public JsonFileDevRequestStore(string directory)
    {
        WatchDirectory = directory;
        Directory.CreateDirectory(directory);
    }

    public async Task<IEnumerable<DevRequest>> ListAllAsync()
    {
        if (WatchDirectory is null || !Directory.Exists(WatchDirectory)) return [];

        var tasks = Directory.GetFiles(WatchDirectory, "*.json")
            .Select(async f =>
            {
                try { return JsonSerializer.Deserialize<DevRequest>(await File.ReadAllTextAsync(f), _readOpts); }
                catch { return null; }
            });

        var all = (await Task.WhenAll(tasks))
            .Where(r => r is not null)
            .Cast<DevRequest>()
            .OrderByDescending(r => r.Timestamp)
            .ToList();

        ApplyBloqueado(all);
        return all;
    }

    public async Task<DevRequest?> GetByIdAsync(string id)
    {
        var path = Path.Combine(WatchDirectory!, $"{id}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<DevRequest>(json, _readOpts);
        }
        catch { return null; }
    }

    public async Task SaveAsync(DevRequest request)
    {
        Directory.CreateDirectory(WatchDirectory!);
        var path = Path.Combine(WatchDirectory!, $"{request.Id}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request, _writeOpts));
    }

    public Task DeleteAsync(string id)
    {
        var path = Path.Combine(WatchDirectory!, $"{id}.json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private static void ApplyBloqueado(List<DevRequest> all)
    {
        var doneIds = all.Where(r => r.Status == "done").Select(r => r.Id).ToHashSet();
        foreach (var r in all)
            r.Bloqueado = r.Dependencias?.Any(d => !doneIds.Contains(d)) ?? false;
    }
}
