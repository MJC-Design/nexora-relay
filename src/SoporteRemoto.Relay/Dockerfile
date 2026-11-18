# Fase de compilación
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copiar todo el código fuente manteniendo la estructura
COPY . .

# Publicar el proyecto del Relay
RUN dotnet publish SoporteRemoto.Relay/SoporteRemoto.Relay.csproj -c Release -o /app/publish --no-restore

# Fase de ejecución
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Render asigna el puerto dinámicamente mediante la variable PORT
ENV ASPNETCORE_URLS=http://+:$PORT

ENTRYPOINT ["dotnet", "SoporteRemoto.Relay.dll"]