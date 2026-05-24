using System.Text.Json.Serialization;

namespace DevAutomation.Models;

public class DevRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // Campos usados pelo frontend (devreq-list)
    [JsonPropertyName("api")]
    public string Api { get; set; } = "";          // nome do projeto/api

    [JsonPropertyName("tipo")]
    public string Tipo { get; set; } = "feature";  // feature | bugfix | config

    [JsonPropertyName("impacto")]
    public string Impacto { get; set; } = "baixo"; // baixo | medio | alto

    [JsonPropertyName("descricao")]
    public string Descricao { get; set; } = "";

    [JsonPropertyName("detalhes")]
    public string? Detalhes { get; set; }

    [JsonPropertyName("url_externa")]
    public string? UrlExterna { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "pendente";
    // pendente | aguardando_aprovacao | in_progress | impeditivo | revisao_reprovada | revisao_amarela | em_testes | done | error | cancelado

    [JsonPropertyName("impeditivo")]
    public bool Impeditivo { get; set; } = false;

    [JsonPropertyName("resultado")]
    public string? Resultado { get; set; }

    [JsonPropertyName("prompt_agente")]
    public string? PromptAgente { get; set; }

    [JsonPropertyName("diretorio_alvo")]
    public string? DiretorioAlvo { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("pendencias")]
    public string? Pendencias { get; set; }

    [JsonPropertyName("dependencias")]
    public List<string>? Dependencias { get; set; }

    [JsonPropertyName("bloqueado")]
    public bool Bloqueado { get; set; }

    [JsonPropertyName("resposta_usuario")]
    public string? RespostaUsuario { get; set; }

    [JsonPropertyName("comentarios_teste")]
    public string? ComentariosTeste { get; set; }

    [JsonPropertyName("timestamp_atualizacao")]
    public DateTime? TimestampAtualizacao { get; set; }

    [JsonPropertyName("resultado_revisao")]
    public string? ResultadoRevisao { get; set; }

    [JsonPropertyName("url_teste")]
    public string? UrlTeste { get; set; }

    [JsonPropertyName("consideracoes_refazer")]
    public string? ConsideracoesRefazer { get; set; }

    [JsonPropertyName("contratos")]
    public ContratosCamada? Contratos { get; set; }
}
