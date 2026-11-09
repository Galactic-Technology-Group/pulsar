# Build from the repository root: az acr build -t pulsar-loadtest:<sha> -f tools/Nms.LoadTest/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Nms.Shared/Nms.Shared.csproj src/Nms.Shared/
COPY src/Nms.Data/Nms.Data.csproj src/Nms.Data/
COPY tools/Nms.LoadTest/Nms.LoadTest.csproj tools/Nms.LoadTest/
RUN dotnet restore tools/Nms.LoadTest/Nms.LoadTest.csproj
COPY src/Nms.Shared/ src/Nms.Shared/
COPY src/Nms.Data/ src/Nms.Data/
COPY tools/Nms.LoadTest/ tools/Nms.LoadTest/
RUN dotnet publish tools/Nms.LoadTest/Nms.LoadTest.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["dotnet", "Nms.LoadTest.dll"]
