# Regras para execução de dev-requests

> Leia antes de iniciar qualquer dev-request. Estas regras têm precedência sobre interpretação livre.

---

## Contrato de dados — JSON obrigatório

Quando a dev-request envolve **qualquer um dos itens abaixo**, e o campo `detalhes` não contém um JSON de exemplo explícito, **não implementar**: setar `status = "impeditivo"` e registrar em `pendencias` o que está faltando.

Situações que exigem JSON no `detalhes`:

- Novo endpoint de API (request body ou response)
- Estrutura de dados hierárquica ou recursiva
- DTO com regras de negócio (campos mutuamente exclusivos, campos opcionais com semântica específica)
- Payload de evento (Service Bus, WebSocket, etc.)
- Formato de snapshot / log imutável

### Por que isso importa

Prosa descreve a intenção, mas é ambígua. Um JSON de exemplo é um contrato verificável — o agente pode validar o código gerado contra ele. Sem o contrato, o agente pode gerar estruturas erradas que compilam mas violam regras de negócio (ex: retornar `filhos` e `opcoes` preenchidos simultaneamente em um nó, quando a regra é que são mutuamente exclusivos).

### Como registrar o impeditivo

```json
{
  "status": "impeditivo",
  "pendencias": "Falta JSON de contrato para o endpoint GET /atributos. Necessário: exemplo de response com árvore recursiva, indicando se filhos e opcoes podem coexistir no mesmo nó."
}
```

---

## Quando o JSON está presente

Tratar como contrato normativo — não como sugestão. Regras explícitas no JSON têm precedência sobre qualquer interpretação da prosa do `detalhes`.

Exemplos de regras que devem ser lidas do JSON, não inferidas:

- Campos mutuamente exclusivos (`filhos` vs `opcoes`)
- Campos obrigatórios vs opcionais
- Nomes exatos de propriedades (camelCase, snake_case, etc.)
- Tipos (string UUID vs Guid, decimal vs float)

---

## Tríade de entrega obrigatória

Toda dev-request de funcionalidade deve conter os **três pilares**. A task só pode ser marcada como concluída se todos estiverem presentes:

| Pilar | O que é |
|---|---|
| **Backend** | Lógica de negócio, serviços, repositórios |
| **API** | Rota/endpoint exposto (REST, SignalR, etc.) |
| **UI** | Componente ou rota navegável na interface do usuário |

Se a spec não cobrir explicitamente um dos pilares, o agente deve sinalizar impeditivo antes de implementar — não assumir que o pilar é dispensável.

---

## Mapeamento prévio de dependências

Antes de escrever qualquer linha de código, o agente deve listar no output:

1. Arquivos de backend a criar ou modificar
2. Rotas/endpoints a expor
3. Arquivos de UI a criar ou modificar

Esse mapeamento serve como contrato de escopo declarado — o revisor valida que nada foi omitido.

---

## Garantia de acoplamento visual

É proibido gerar ou modificar lógicas de negócio em isolamento. Toda nova funcionalidade deve nascer conectada a um ponto de entrada ou controle interativo na interface do usuário. Componente Angular sem rota registrada, ou endpoint sem ação correspondente no painel, são entregas incompletas.

---

## Validação por teste E2E

O agente deve criar o arquivo de teste de integração ou E2E **antes** de qualquer implementação de feature. O ciclo é:

1. Criar o teste que simula a interação do usuário na tela
2. Implementar a feature
3. Confirmar que o teste passa

A task só está validada quando o teste específico criado para ela passar.

---

## Outras regras gerais

- Nunca implementar além do escopo da dev-request
- Não criar arquivos de documentação além do solicitado
- `diretorio_alvo` indica o projeto — não alterar arquivos fora dele sem justificativa explícita
- Após implementar, compilar e registrar resultado em `resultado`; se houver erros de compilação não relacionados ao escopo, registrar em `pendencias`
