using DevAutomation.Models;

namespace DevAutomation.Services.Store;

public interface IDevRequestStore
{
    public string? WatchDirectory { get; }
    public Task<IEnumerable<DevRequest>> ListAllAsync();
    public Task<DevRequest?> GetByIdAsync(string id);
    public Task SaveAsync(DevRequest request);
    public Task DeleteAsync(string id);
}
