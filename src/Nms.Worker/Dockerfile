FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Nms.sln ./
COPY src/Nms.Shared/Nms.Shared.csproj src/Nms.Shared/
COPY src/Nms.Data/Nms.Data.csproj src/Nms.Data/
COPY src/Nms.Worker/Nms.Worker.csproj src/Nms.Worker/
RUN dotnet restore src/Nms.Worker/Nms.Worker.csproj
COPY src/ src/
RUN dotnet publish src/Nms.Worker/Nms.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["dotnet", "Nms.Worker.dll"]
