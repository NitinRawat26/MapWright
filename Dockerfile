# MapWright API. Build: docker build -t mapwright-api .
# Run:   docker run -p 8080:8080 -v mapwright-data:/var/data mapwright-api
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
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
