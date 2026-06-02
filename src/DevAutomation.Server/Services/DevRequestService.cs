using System.Text.Json;
using DevAutomation.Models;

namespace DevAutomation.Services;

public class DevRequestService
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    public string DevRequestsDir { get; }

    public DevRequestService(IConfiguration config)
    {
        DevRequestsDir = config["DevAutomation:DevRequestsDir"]!;
    }

    public async Task<DevRequest?> LoadAsync(string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<DevRequest>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(string filePath, DevRequest request)
    {
        var json = JsonSerializer.Serialize(request, _jsonOpts);
        await File.WriteAllTextAsync(filePath, json);
    }

    public async Task<(DevRequest? Request, string? FilePath)> FindByIdAsync(string id)
    {
        if (!Directory.Exists(DevRequestsDir)) return (null, null);

        var file = Directory.GetFiles(DevRequestsDir, "*.json")
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == id);

        if (file is null) return (null, null);

        var request = await LoadAsync(file);
        return (request, file);
    }

    public IEnumerable<DevRequest> ListAll()
    {
        if (!Directory.Exists(DevRequestsDir)) return [];

        var all = Directory.GetFiles(DevRequestsDir, "*.json")
            .Select(f =>
            {
                try { return JsonSerializer.Deserialize<DevRequest>(File.ReadAllText(f), _jsonOpts); }
                catch { return null; }
            })
            .Where(r => r is not null)
            .Cast<DevRequest>()
            .OrderByDescending(r => r.Timestamp)
            .ToList();

        var doneIds = all.Where(r => r.Status == "done").Select(r => r.Id).ToHashSet();
        foreach (var r in all)
            r.Bloqueado = r.Dependencias?.Any(d => !doneIds.Contains(d)) ?? false;

        return all;
    }
}
