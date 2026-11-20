# syntax=docker/dockerfile:1.7

# -------- build --------
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Directory.Build.props holds the version defaults and the target framework, so it must be at /src.
COPY global.json Directory.Build.props ./
COPY Custos.Server/ ./Custos.Server/
RUN dotnet restore Custos.Server/Custos.Server.csproj

# The version comes from CI. Version must be numeric. InformationalVersion is free text.
ARG VERSION=unstable
ARG ASSEMBLY_VERSION=0.0.0

RUN dotnet publish Custos.Server/Custos.Server.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        -p:UseAppHost=false \
        -p:Version="${ASSEMBLY_VERSION}" \
        -p:InformationalVersion="${VERSION}"

# -------- runtime --------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

ARG SOURCE_REPO=""
ARG REVISION=""
ARG VERSION=unstable
LABEL org.opencontainers.image.source="${SOURCE_REPO}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.licenses="AGPL-3.0-or-later"

ENTRYPOINT ["dotnet", "Custos.Server.dll"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "Custos.Server.dll", "--healthcheck"]
