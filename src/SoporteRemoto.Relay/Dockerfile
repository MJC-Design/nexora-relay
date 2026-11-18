# Fase de compilación
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copiar todo el repositorio completo
COPY . .

# Restaurar y publicar usando la ruta correcta desde la raíz
RUN dotnet restore "src/SoporteRemoto.Relay/SoporteRemoto.Relay.csproj"
RUN dotnet publish "src/SoporteRemoto.Relay/SoporteRemoto.Relay.csproj" -c Release -o /app/publish

# Fase de ejecución
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Puerto dinámico inyectado por Render
ENV ASPNETCORE_URLS=http://+:$PORT

ENTRYPOINT ["dotnet", "SoporteRemoto.Relay.dll"]