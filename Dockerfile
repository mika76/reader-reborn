# Reader in a container, for Docker or Podman (see README):
#   podman build -t reader .     (or: docker build -t reader .)
#   podman run -d -p 5199:5199 -v reader-data:/data reader
# or with compose.yaml: podman compose up -d   (or: docker compose up -d)

# 1. The Vue frontend
FROM docker.io/library/node:22-alpine AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# 2. The backend, published from the single reader.cs file
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
WORKDIR /src/server
COPY server/reader.cs ./
RUN dotnet publish reader.cs -c Release -o /app

# 3. What runs: the ASP.NET runtime, the published server and the built frontend
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=server /app ./
COPY --from=web /src/web/dist ./wwwroot
# reader.db and reader-media.db live in /data; mount a volume there to keep them
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID
# ASPNETCORE_HTTP_PORTS: the base image's default of 8080 would otherwise be reported as overridden
ENV READER_HOST=0.0.0.0 \
    READER_PORT=5199 \
    ASPNETCORE_HTTP_PORTS= \
    READER_DB=/data/reader.db \
    READER_WEB=/app/wwwroot
EXPOSE 5199
VOLUME /data
ENTRYPOINT ["dotnet", "reader.dll"]
