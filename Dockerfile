FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Search.Core/Search.Core.csproj src/Search.Core/
COPY src/Search.Infrastructure/Search.Infrastructure.csproj src/Search.Infrastructure/
COPY src/Search.Api/Search.Api.csproj src/Search.Api/
COPY nuget.config ./
COPY local-feed/ local-feed/
RUN dotnet restore src/Search.Api/Search.Api.csproj
COPY . .
RUN dotnet publish src/Search.Api/Search.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5003
ENV ASPNETCORE_URLS=http://+:5003
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
USER app
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:5003/health || exit 1
ENTRYPOINT ["dotnet", "Search.Api.dll"]
