# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/SoporteRemoto.Protocol/SoporteRemoto.Protocol.csproj src/SoporteRemoto.Protocol/
COPY src/SoporteRemoto.Relay/SoporteRemoto.Relay.csproj src/SoporteRemoto.Relay/
RUN dotnet restore src/SoporteRemoto.Relay/SoporteRemoto.Relay.csproj

COPY src/SoporteRemoto.Protocol/ src/SoporteRemoto.Protocol/
COPY src/SoporteRemoto.Relay/ src/SoporteRemoto.Relay/
RUN dotnet publish src/SoporteRemoto.Relay/SoporteRemoto.Relay.csproj -c Release -o /app --no-restore

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Render injects its own PORT value, which overrides this default.
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SoporteRemoto.Relay.dll"]
