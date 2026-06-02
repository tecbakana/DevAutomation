# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /repo

COPY src/DevAutomation.Server/DevAutomation.Server.csproj src/DevAutomation.Server/
RUN dotnet restore src/DevAutomation.Server/DevAutomation.Server.csproj

COPY src/ src/
RUN dotnet publish src/DevAutomation.Server/DevAutomation.Server.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /forge

# Instala git (necessário para operações de repositório)
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*

# Copia o binário publicado
COPY --from=build /app/publish ./bin

# Copia assets estáticos do Forge (panel, config, scripts, templates, wiki)
COPY panel/       ./panel/
COPY config/      ./config/
COPY scripts/     ./scripts/
COPY templates/   ./templates/
COPY wiki/        ./wiki/

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080

RUN mkdir -p /forge/dev-requests

EXPOSE 8080

ENTRYPOINT ["dotnet", "/forge/bin/DevAutomation.Server.dll"]
