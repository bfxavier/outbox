FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY Outbox.Relay/Outbox.Relay.csproj Outbox.Relay/
RUN dotnet restore Outbox.Relay/Outbox.Relay.csproj
COPY Outbox.Relay/. Outbox.Relay/
RUN dotnet publish Outbox.Relay/Outbox.Relay.csproj \
        -c Release \
        -o /app \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
RUN apk add --no-cache curl && \
    mkdir -p /data && \
    chown -R 1000:1000 /data /app
COPY --from=build --chown=1000:1000 /app .
USER 1000:1000
VOLUME ["/data"]
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    OUTBOX_DB_PATH=/data/outbox.db \
    DOTNET_RUNNING_IN_CONTAINER=true
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -fsS http://localhost:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "Outbox.Relay.dll"]
