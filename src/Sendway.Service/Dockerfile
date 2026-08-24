FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["src/Sendway.Service/Sendway.Service.csproj", "src/Sendway.Service/"]
COPY ["src/Sendway.Core/Sendway.Core.csproj", "src/Sendway.Core/"]
RUN dotnet restore "src/Sendway.Service/Sendway.Service.csproj"

COPY ["src/Sendway.Service/", "src/Sendway.Service/"]
COPY ["src/Sendway.Core/", "src/Sendway.Core/"]
RUN dotnet publish "src/Sendway.Service/Sendway.Service.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Persist the Data Protection key ring outside the container's writable layer so it survives a
# redeploy/restart. Mount a volume at /data. (Encrypted credentials/tenants/message records live in
# PostgreSQL, supplied via ConnectionStrings__Sendway at deploy time — not a local file, so it isn't
# defaulted here.)
ENV Sendway__DataProtectionKeyPath="/data/dp-keys"
VOLUME ["/data"]

EXPOSE 8080

ENTRYPOINT ["dotnet", "Sendway.Service.dll"]
