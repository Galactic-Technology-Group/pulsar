FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Nms.sln ./
COPY src/Nms.Shared/Nms.Shared.csproj src/Nms.Shared/
COPY src/Nms.Data/Nms.Data.csproj src/Nms.Data/
COPY src/Nms.Api/Nms.Api.csproj src/Nms.Api/
RUN dotnet restore src/Nms.Api/Nms.Api.csproj
COPY src/ src/
RUN dotnet publish src/Nms.Api/Nms.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Nms.Api.dll"]
