namespace DevAutomation.Models;

public class RagChunk
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Conteudo { get; set; } = "";
    public string Fonte { get; set; } = "";     // caminho relativo ao projeto
    public string Tipo { get; set; } = "";      // code | config | devrequest
    public string Projeto { get; set; } = "";   // forge | cmsx | salematic
    public float[] Vetor { get; set; } = [];    // 768 dims
}
