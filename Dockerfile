# --- сборка ---
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY src/Tar2Registry/Tar2Registry.csproj .
RUN dotnet restore
COPY src/Tar2Registry/ .
RUN dotnet publish -c Release -o /out

# --- запуск (внутри нужен skopeo) ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
RUN apk add --no-cache skopeo
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8000 SKOPEO_PATH=/usr/bin/skopeo
EXPOSE 8000
ENTRYPOINT ["dotnet", "Tar2Registry.dll"]
