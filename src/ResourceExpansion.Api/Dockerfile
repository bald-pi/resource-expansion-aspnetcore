# Build from the repository root: docker build -f src/ResourceExpansion.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .
COPY src/ResourceExpansion.Api/ResourceExpansion.Api.csproj src/ResourceExpansion.Api/
RUN dotnet restore src/ResourceExpansion.Api/ResourceExpansion.Api.csproj
COPY src/ResourceExpansion.Api/ src/ResourceExpansion.Api/
RUN dotnet publish src/ResourceExpansion.Api/ResourceExpansion.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The aspnet image listens on port 8080.
ENTRYPOINT ["dotnet", "ResourceExpansion.Api.dll"]
