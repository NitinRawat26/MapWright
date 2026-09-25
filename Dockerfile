# MapWright API and web UI. Build: docker build -t mapwright-api .
# Run:   docker run -p 8080:8080 -v mapwright-data:/var/data mapwright-api
FROM node:24-bookworm-slim AS web
WORKDIR /src/web
ENV NG_CLI_ANALYTICS=false
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
COPY --from=web /src/src/MapWright.Api/wwwroot src/MapWright.Api/wwwroot
RUN dotnet publish src/MapWright.Api/MapWright.Api.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    MapWright__DatabasePath=/var/data/mapwright.db
RUN mkdir -p /var/data
VOLUME /var/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "MapWright.Api.dll"]
