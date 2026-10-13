# Motor C# da versão 2.0.26 + nova interface web.
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
COPY . .
RUN dotnet publish Web/IPTVDownloader.Web.csproj -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://0.0.0.0:5078 \
    XDG_DATA_HOME=/appdata \
    IPTV_DOWNLOAD_PARENT=/storage \
    IPTV_DOWNLOAD_ROOT_NAME="IP Downloader"
EXPOSE 5078
ENTRYPOINT ["dotnet", "IPTVDownloader.Web.dll"]
