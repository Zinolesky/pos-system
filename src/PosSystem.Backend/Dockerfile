FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/PosSystem.Backend/PosSystem.Backend.csproj", "src/PosSystem.Backend/"]
COPY ["src/PosSystem.Shared/PosSystem.Shared.csproj", "src/PosSystem.Shared/"]
RUN dotnet restore "src/PosSystem.Backend/PosSystem.Backend.csproj"
COPY . .
WORKDIR "/src/src/PosSystem.Backend"
RUN dotnet build "PosSystem.Backend.csproj" -c Release -o /app/build --no-restore

FROM build AS publish
RUN dotnet publish "PosSystem.Backend.csproj" -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PosSystem.Backend.dll"]
