# syntax=docker/dockerfile:1
# Camera Viewer in one image: the React UI, the embedded Tailscale helper and the ASP.NET Core server, plus ffmpeg.
#   docker compose up -d --build     (see docker-compose.yml)

FROM node:22-bookworm-slim AS client
WORKDIR /src/client
COPY client/package.json client/package-lock.json ./
RUN npm ci --no-fund --no-audit
COPY client/ ./
# vite writes to ../server/CameraViewer.Api/wwwroot
RUN mkdir -p ../server/CameraViewer.Api && npm run build

FROM golang:1.27.3-bookworm AS tsproxy
ARG TARGETARCH
WORKDIR /src/tsproxy
COPY tailscale/tsproxy/go.mod tailscale/tsproxy/go.sum ./
RUN go mod download
COPY tailscale/tsproxy/ ./
RUN rid=linux-$([ "$TARGETARCH" = arm64 ] && echo arm64 || echo x64) \
 && CGO_ENABLED=0 GOARCH=$TARGETARCH go build -trimpath -ldflags '-s -w' -o /out/$rid/tsproxy .

FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS server
WORKDIR /src
COPY server/CameraViewer.Api/CameraViewer.Api.csproj server/CameraViewer.Api/
RUN dotnet restore server/CameraViewer.Api
COPY server/ server/
COPY --from=client /src/server/CameraViewer.Api/wwwroot server/CameraViewer.Api/wwwroot
COPY --from=tsproxy /out tailscale/bin
# appsettings.Local.json holds device passwords; it is never baked into the image.
RUN rm -f server/CameraViewer.Api/appsettings.Local.json \
 && dotnet publish server/CameraViewer.Api -c Release --no-restore -o /app \
 && chmod +x /app/tailscale/*/tsproxy

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg tzdata && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS= \
    Urls=http://0.0.0.0:5080 \
    Storage__DataPath=/data
WORKDIR /app
COPY --from=server /app ./
RUN mkdir -p /data && chown "$APP_UID" /data
# The image's unprivileged "app" account; /data must belong to it (the database and keys are private to it).
USER $APP_UID
EXPOSE 5080
VOLUME /data
ENTRYPOINT ["dotnet", "CameraViewer.Api.dll"]
