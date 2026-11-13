FROM mcr.microsoft.com/dotnet/sdk:11.0.100 AS build
WORKDIR /source

COPY global.json Directory.Build.props Directory.Packages.props NativeRtcAssets.props ./
COPY src/Pongsole.Server/Pongsole.Server.csproj src/Pongsole.Server/
COPY src/Pongsole.Contracts/Pongsole.Contracts.csproj src/Pongsole.Contracts/
COPY src/Pongsole.Core/Pongsole.Core.csproj src/Pongsole.Core/
COPY src/Pongsole.Transport/Pongsole.Transport.csproj src/Pongsole.Transport/
RUN dotnet restore src/Pongsole.Server/Pongsole.Server.csproj

COPY src/Pongsole.Server/ src/Pongsole.Server/
COPY src/Pongsole.Contracts/ src/Pongsole.Contracts/
COPY src/Pongsole.Core/ src/Pongsole.Core/
COPY src/Pongsole.Transport/ src/Pongsole.Transport/
COPY native/libdatachannel/ native/libdatachannel/
RUN dotnet publish src/Pongsole.Server/Pongsole.Server.csproj \
    --configuration Release --no-restore --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
COPY --from=build /app/ ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Pongsole.Server.dll"]
