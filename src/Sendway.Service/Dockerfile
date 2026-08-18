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

EXPOSE 8080

ENTRYPOINT ["dotnet", "Sendway.Service.dll"]
