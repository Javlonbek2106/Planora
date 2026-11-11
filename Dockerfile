# Build the API in the SDK image, run it in the slim ASP.NET runtime image as a non-root user.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the package layer is cached until a .csproj changes.
COPY src/BuildingDraft.Domain/*.csproj src/BuildingDraft.Domain/
COPY src/BuildingDraft.Application/*.csproj src/BuildingDraft.Application/
COPY src/BuildingDraft.Infrastructure/*.csproj src/BuildingDraft.Infrastructure/
COPY src/BuildingDraft.Api/*.csproj src/BuildingDraft.Api/
RUN dotnet restore src/BuildingDraft.Api/BuildingDraft.Api.csproj

COPY src/ src/
RUN dotnet publish src/BuildingDraft.Api/BuildingDraft.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Exported files are kept here; mount a volume to keep them across container restarts.
RUN mkdir -p /data/storage && chown -R $APP_UID /data
ENV ASPNETCORE_URLS=http://+:8080 \
    Storage__Root=/data/storage
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "BuildingDraft.Api.dll"]
