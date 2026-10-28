# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/Bin4Scraper/Bin4Scraper.csproj src/Bin4Scraper/
RUN dotnet restore src/Bin4Scraper/Bin4Scraper.csproj -a $TARGETARCH
COPY src/ src/
RUN dotnet publish src/Bin4Scraper/Bin4Scraper.csproj -c Release -a $TARGETARCH --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=dev
LABEL org.opencontainers.image.title="bin4scraper" \
      org.opencontainers.image.description="Texts the Bin 4 Burger Lounge monthly feature" \
      org.opencontainers.image.source="https://github.com/Jsyro/bin4scraper" \
      org.opencontainers.image.version=$VERSION
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Storage__DataDirectory=/data \
    TZ=America/Vancouver \
    DOTNET_gcServer=0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data && chown $APP_UID /data && chmod 0777 /data
VOLUME /data
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=1m --timeout=10s --start-period=30s --retries=3 CMD ["dotnet", "Bin4Scraper.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "Bin4Scraper.dll"]
