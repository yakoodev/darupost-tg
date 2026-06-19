FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY global.json ./
COPY TgAutoposter.sln ./
COPY src/TgAutoposter.Domain/TgAutoposter.Domain.csproj src/TgAutoposter.Domain/
COPY src/TgAutoposter.Application/TgAutoposter.Application.csproj src/TgAutoposter.Application/
COPY src/TgAutoposter.Infrastructure/TgAutoposter.Infrastructure.csproj src/TgAutoposter.Infrastructure/
COPY src/TgAutoposter.Api/TgAutoposter.Api.csproj src/TgAutoposter.Api/
RUN dotnet restore src/TgAutoposter.Api/TgAutoposter.Api.csproj

COPY src src
RUN dotnet publish src/TgAutoposter.Api/TgAutoposter.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-pip ffmpeg ca-certificates fonts-dejavu-core \
    && python3 -m pip install --break-system-packages --no-cache-dir yt-dlp \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TgAutoposter.Api.dll"]
