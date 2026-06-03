# orchestratR

![.NET](https://img.shields.io/badge/.NET-9-blue)
![AI](https://img.shields.io/badge/AI-Claude%20%7C%20Gemini-green)

Orquestrador de ambientes de desenvolvimento com IA. Gerencia dev-requests em um kanban, despacha implementações para agentes de LLM (Claude CLI ou Gemini), revisa o código gerado automaticamente e mantém um índice RAG do código-fonte para contexto.

Acesso via painel web local em `http://localhost:8080`.

---

## Funcionalidades

- **Kanban de dev-requests** — colunas: Backlog, Aguardando, Em progresso, Impeditivo, Em testes, Revisão, Concluído, Erro
- **Orquestração por LLM** — despacha a implementação para Claude CLI ou Gemini conforme disponibilidade
- **Revisão automática** — auditor externo valida o código antes de mover para Em testes
- **RAG** — indexa o código-fonte em Qdrant com embeddings via Ollama; contexto injetado nas prompts do agente
- **Feature detection** — Qdrant, Ollama, Claude CLI e Gemini detectados no startup; ausência de qualquer um degrada graciosamente sem derrubar o servidor
- **Troca de ambiente** — switch entre branches (developer / homolog / master) de múltiplos projetos
- **Operações git** — status, diff, commit e discard pelo painel
- **Observabilidade** — traces OpenTelemetry exportados para Langfuse

---

## Stack

| Camada | Tecnologia |
|---|---|
| Servidor | ASP.NET Core 9, C# |
| Painel | HTML/JS estático (sem build step) |
| Real-time | SignalR |
| Store de dev-requests | JSON (padrão) \| SQLite \| MongoDB |
| RAG — vetores | Qdrant |
| RAG — embeddings | Ollama (`bge-m3`) |
| LLM orquestrador | Claude CLI \| Gemini 2.5 Flash |
| Tracing | OpenTelemetry → Langfuse |

---

## Configuração rápida

### 1. Dependências opcionais

| Serviço | Para que serve | Sem ele |
|---|---|---|
| Claude CLI | Execução de dev-requests | Orquestrador desabilitado |
| Qdrant | Armazenar vetores RAG | RAG desabilitado |
| Ollama + `bge-m3` | Gerar embeddings | RAG desabilitado |
| Langfuse | Traces de LLM | Tracing desabilitado |

### 2. User secrets (desenvolvimento local)

```bash
# Gemini (obrigatório para orquestração via Gemini)
dotnet user-secrets set "agent:apiKey" "<gemini-api-key>"

# Langfuse (opcional)
dotnet user-secrets set "Langfuse:PublicKey" "<key>"
dotnet user-secrets set "Langfuse:SecretKey" "<key>"

# Store alternativo (padrão: json)
dotnet user-secrets set "DevAutomation:StoreType" "mongo"
dotnet user-secrets set "DevAutomation:MongoConnectionString" "mongodb://localhost:27017"
```

### 3. Executar

```bash
cd src/DevAutomation.Server
dotnet run
```

O painel abre automaticamente em `http://localhost:8080`.

---

## Store de dev-requests

Configurável via `DevAutomation:StoreType`:

| Valor | Backend | Quando usar |
|---|---|---|
| `json` | Arquivos `.json` em `dev-requests/` | Padrão, sem dependências |
| `sqlite` | SQLite em `dev-requests/devrequests.db` | Dev local persistente |
| `mongo` | MongoDB | Produção / multiusuário |

Para MongoDB com Docker:
```bash
docker run -d --name orchestratr-mongo -p 27017:27017 mongo:8
```

---

## Docker

```bash
docker compose up -d
```

O `docker-compose.yml` inclui o serviço `orchestratr`. MongoDB comentado — descomente e altere `StoreType` para ativar.

---

## Estrutura

```
orchestratR/
├── src/
│   └── DevAutomation.Server/
│       ├── Controllers/         — API REST
│       ├── Hubs/                — SignalR
│       ├── Models/              — DevRequest, FeatureFlags
│       └── Services/
│           ├── Orchestration/   — ClaudeCliStrategy, NoopStrategy
│           ├── Store/           — IDevRequestStore (JSON/SQLite/Mongo)
│           ├── OrchestratorService.cs
│           ├── RagIndexerService.cs
│           ├── RagService.cs
│           └── AuditorService.cs
├── panel/                       — Painel web (index.html)
├── config/
│   ├── environments.json        — APIs, branches, agente
│   └── state.json               — estado em tempo de execução
├── templates/                   — Templates de config por ambiente
├── dev-requests/                — Fila JSON (quando StoreType=json)
├── scripts/                     — PowerShell (Switch-Environment, git, etc.)
├── batches/                     — Atalhos .bat
└── docker-compose.yml
```

---

## API principal

| Método | Rota | Descrição |
|---|---|---|
| `GET` | `/api/devrequests` | Lista todas as dev-requests |
| `POST` | `/api/devrequests` | Cria uma nova dev-request |
| `PUT` | `/api/devrequests/{id}` | Edita campos de uma dev-request |
| `POST` | `/api/devrequests/action` | Executa uma ação (aprovar, completar, cancelar…) |
| `GET` | `/api/health` | Health check |
| `GET` | `/api/platform` | Feature flags e status dos serviços |
| `GET` | `/api/rag/stats` | Estatísticas do índice RAG |
| `POST` | `/api/rag/reindex` | Força reindexação |

Swagger disponível em `http://localhost:8080/swagger`.

---

## Status de dev-requests

| Status | Descrição |
|---|---|
| `pendente` | Aguardando aprovação |
| `aguardando_aprovacao` | Em revisão manual |
| `in_progress` | Agente implementando |
| `em_testes` | Aguarda aprovação de testes |
| `revisao_amarela` | Revisão com avisos |
| `revisao_reprovada` | Revisão reprovou |
| `impeditivo` | Agente bloqueado, aguarda resposta |
| `done` | Concluído |
| `error` | Erro na execução |
| `cancelado` | Cancelado |
