using System.Text.Json.Serialization;

namespace DevAutomation.Models;

public class ContratosCamada
{
    [JsonPropertyName("schema")]
    public ContratoCamada? Schema { get; set; }

    [JsonPropertyName("repositorio")]
    public ContratoCamada? Repositorio { get; set; }

    [JsonPropertyName("api")]
    public ContratoCamada? Api { get; set; }

    [JsonPropertyName("frontend")]
    public ContratoCamada? Frontend { get; set; }
}

public class ContratoCamada
{
    [JsonPropertyName("afetada")]
    public bool Afetada { get; set; }

    [JsonPropertyName("escopo")]
    public string Escopo { get; set; } = "";

    [JsonPropertyName("artefatos")]
    public string[] Artefatos { get; set; } = [];

    [JsonPropertyName("expoe")]
    public string Expoe { get; set; } = "";
}
