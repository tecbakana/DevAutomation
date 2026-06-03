using System.Text.Json;
using DevAutomation.Models;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace DevAutomation.Services.Store;

public sealed class MongoDevRequestStore : IDevRequestStore
{
    private readonly IMongoCollection<BsonDocument> _collection;
    private static readonly JsonSerializerOptions _writeOpts = new() { WriteIndented = false };
    private static readonly JsonWriterSettings _relaxedJson  = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

    public string? WatchDirectory => null;

    public MongoDevRequestStore(string connectionString, string databaseName = "orchestratr")
    {
        var client     = new MongoClient(connectionString);
        var db         = client.GetDatabase(databaseName);
        _collection    = db.GetCollection<BsonDocument>("dev_requests");

        var indexModel = new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Descending("timestamp"));
        _collection.Indexes.CreateOne(indexModel);
    }

    public async Task<IEnumerable<DevRequest>> ListAllAsync()
    {
        var docs = await _collection.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending("timestamp"))
            .ToListAsync();

        var all = docs
            .Select(DocToRequest)
            .Where(r => r is not null)
            .Cast<DevRequest>()
            .ToList();

        ApplyBloqueado(all);
        return all;
    }

    public async Task<DevRequest?> GetByIdAsync(string id)
    {
        var doc = await _collection.Find(Builders<BsonDocument>.Filter.Eq("_id", id))
            .FirstOrDefaultAsync();
        return doc is null ? null : DocToRequest(doc);
    }

    public async Task SaveAsync(DevRequest request)
    {
        var json = JsonSerializer.Serialize(request, _writeOpts);
        var doc  = BsonDocument.Parse(json);
        doc["_id"] = request.Id;

        await _collection.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", request.Id),
            doc,
            new ReplaceOptions { IsUpsert = true });
    }

    public async Task DeleteAsync(string id) =>
        await _collection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", id));

    private static readonly JsonSerializerOptions _ciReadOpts = new() { PropertyNameCaseInsensitive = true };

    private static DevRequest? DocToRequest(BsonDocument doc)
    {
        try
        {
            // Remove _id para não conflitar com o campo "id" do modelo
            var clean = doc.Clone().AsBsonDocument;
            clean.Remove("_id");
            var json = clean.ToJson(_relaxedJson);
            return JsonSerializer.Deserialize<DevRequest>(json, _ciReadOpts);
        }
        catch { return null; }
    }

    private static void ApplyBloqueado(List<DevRequest> all)
    {
        var doneIds = all.Where(r => r.Status == "done").Select(r => r.Id).ToHashSet();
        foreach (var r in all)
            r.Bloqueado = r.Dependencias?.Any(d => !doneIds.Contains(d)) ?? false;
    }
}
