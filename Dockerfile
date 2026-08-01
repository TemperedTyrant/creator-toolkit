# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
WORKDIR /source

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY LICENSE THIRD_PARTY_NOTICES.md /app/notices/
COPY src/TemperedTyrant.CreatorToolkit.Core/*.csproj src/TemperedTyrant.CreatorToolkit.Core/
COPY src/TemperedTyrant.CreatorToolkit.Infrastructure/*.csproj src/TemperedTyrant.CreatorToolkit.Infrastructure/
COPY src/TemperedTyrant.CreatorToolkit.Web/*.csproj src/TemperedTyrant.CreatorToolkit.Web/
RUN dotnet restore \
    src/TemperedTyrant.CreatorToolkit.Web/TemperedTyrant.CreatorToolkit.Web.csproj

COPY src/ src/
RUN dotnet publish \
    src/TemperedTyrant.CreatorToolkit.Web/TemperedTyrant.CreatorToolkit.Web.csproj \
    --configuration Release \
    --no-restore \
    --self-contained false \
    -p:DebugSymbols=false \
    -p:DebugType=None \
    --output /app/publish \
    && chmod 0444 /app/notices/LICENSE /app/notices/THIRD_PARTY_NOTICES.md \
    && mkdir --parents /app/data \
    && touch /app/data/.volume-initialized

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-noble-chiseled-extra@sha256:f9bd6be9b5ab75b8196bff0f0972580edaea7fa8ca04e6ef530950e33caee5b0 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    CREATOR_TOOLKIT_DataDirectory=/app/data \
    DOTNET_EnableDiagnostics=0 \
    PATH="/app:${PATH}"

COPY --from=build /app/publish/ ./
COPY --from=build /app/notices/ ./
COPY --from=build --chown=1654:1654 /app/data/ ./data/

EXPOSE 8080
VOLUME ["/app/data"]
USER 1654:1654
ENTRYPOINT ["/app/creator-toolkit"]
