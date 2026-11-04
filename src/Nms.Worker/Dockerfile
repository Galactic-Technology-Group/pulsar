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
# The gateway probe pings sites from Azure. Raw ICMP sockets need CAP_NET_RAW, granted to the dotnet host as a file
# capability so the process can keep running as the unprivileged app user.
RUN apt-get update && apt-get install -y --no-install-recommends libcap2-bin && rm -rf /var/lib/apt/lists/* \
 && setcap cap_net_raw+ep "$(readlink -f "$(command -v dotnet)")"
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["dotnet", "Nms.Worker.dll"]
