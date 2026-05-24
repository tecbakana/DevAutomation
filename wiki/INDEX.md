# Wiki — Hub de Padrões e Documentação

> Ponto de entrada único. Consulte este índice antes de propor features, módulos ou integrações.

---

## Como usar

- **Antes de adicionar módulo ao CMSX** → leia `cmsx/cmsx-arch-ntier.md` + `cmsx/cmsx-howto-novo-modulo.md`
- **Antes de implementar nova tool no Salematic** → leia `salematic/salematic-howto-nova-tool.md`
- **Antes de integração entre projetos** → leia `shared/shared-pattern-saga.md` + `shared/shared-pattern-servicebus.md`
- **Antes de decisão arquitetural** → verifique `decisions/` — não contrariar sem ler o dec correspondente

---

## CMSX (Multiplai)

| Arquivo | Tipo | Descrição |
|---|---|---|
| [cmsx-arch-ntier.md](cmsx/cmsx-arch-ntier.md) | arch | Arquitetura N-Tier: camadas, fluxo, regras |
| [cmsx-howto-novo-modulo.md](cmsx/cmsx-howto-novo-modulo.md) | howto | Guia passo-a-passo para adicionar módulo |
| [cmsx-pattern-multitenancy.md](cmsx/cmsx-pattern-multitenancy.md) | pattern | Fluxo do Aplicacaoid: JWT → repositório |
| [cmsx-swagger-bearer.md](cmsx/cmsx-swagger-bearer.md) | ref | Swagger + Bearer JWT + CORS |

---

## Salematic

| Arquivo | Tipo | Descrição |
|---|---|---|
| [salematic-howto-nova-tool.md](salematic/salematic-howto-nova-tool.md) | howto | Guia para adicionar tool ao agente IA |

---

## Forge (DevAutomation)

| Arquivo | Tipo | Descrição |
|---|---|---|
| [forge-devrequests-rules.md](forge/forge-devrequests-rules.md) | rules | Regras para execução de dev-requests — impeditivo quando falta JSON de contrato |

---

## Shared — Padrões compartilhados

| Arquivo | Tipo | Descrição |
|---|---|---|
| [shared-pattern-saga.md](shared/shared-pattern-saga.md) | pattern | SAGA coreografada via Azure Service Bus |
| [shared-pattern-servicebus.md](shared/shared-pattern-servicebus.md) | pattern | Namespace, tópicos e subscriptions |
| [shared-ref-linq-csharp.md](shared/shared-ref-linq-csharp.md) | ref | Referência rápida LINQ C# |

---

## Decisions (ADRs)

| Arquivo | Data | Status |
|---|---|---|
| *(a criar conforme decisões forem tomadas)* | | |

---

## Segurança — Regra permanente

> **Nunca commitar arquivos com API keys, senhas ou configurações sensíveis** (ex: `environments.json` com chave real, `appsettings.json` com connection strings de produção, qualquer arquivo `.env`).
> - Use `dotnet user-secrets` para projetos .NET
> - Mantenha arquivos sensíveis no `.gitignore`
> - Se subiu por acidente: revogue a key imediatamente, não basta deletar o arquivo

---

## Convenção de nomes

```
{prefixo}-{tipo}-{assunto}.md
```

- Prefixos: `cmsx-`, `salematic-`, `forge-`, `shared-`, `dec-NNN-`
- Tipos: `-arch-`, `-howto-`, `-pattern-`, `-ref-`
- Sempre minúsculo, hifenizado, sem acentos

## Convenção de commit

```
wiki: cria cmsx-howto-novo-modulo
wiki: atualiza shared-pattern-saga — SAGA completada com teste E2E
wiki: adiciona pitfall em salematic-howto-nova-tool
```
