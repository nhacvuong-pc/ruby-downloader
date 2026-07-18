FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY RubyDownloader.csproj ./
RUN dotnet restore RubyDownloader.csproj

COPY . ./
RUN dotnet publish RubyDownloader.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/playwright/dotnet:v1.61.0-noble AS runtime

ENV DOTNET_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_HTTP_PORTS=8080 \
    PLAYWRIGHT_HEADLESS=true \
    DOWNLOAD_PATH=/app/downloads

WORKDIR /app
COPY --from=build --chown=pwuser:pwuser /app/publish ./

USER pwuser
VOLUME ["/app/downloads"]
EXPOSE 8080

ENTRYPOINT ["dotnet", "RubyDownloader.dll"]
