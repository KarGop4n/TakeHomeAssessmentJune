FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy 
COPY ["TradingAlertSystem.Console/TradingAlertSystem.Console.csproj", "TradingAlertSystem.Console/"]
COPY ["TradingAlertSystem.Core/TradingAlertSystem.Core.csproj", "TradingAlertSystem.Core/"]
COPY ["TradingAlertSystem.Infrastructure/TradingAlertSystem.Infrastructure.csproj", "TradingAlertSystem.Infrastructure/"]
COPY ["TradingAlertSystem.Services/TradingAlertSystem.Services.csproj", "TradingAlertSystem.Services/"]

RUN dotnet restore "TradingAlertSystem.Console/TradingAlertSystem.Console.csproj"

COPY . .

# Build 
WORKDIR "/src/TradingAlertSystem.Console"
RUN dotnet build "TradingAlertSystem.Console.csproj" -c Release -o /app/build

# Publish
FROM build AS publish
RUN dotnet publish "TradingAlertSystem.Console.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app

# Install ca-certificates
RUN apt-get update && apt-get install -y ca-certificates && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN adduser --disabled-password --gecos '' appuser && chown -R appuser /app
USER appuser

# Copy published app
COPY --from=publish /app/publish .

# Set environment variables
ENV DOTNET_ENVIRONMENT=Production
ENV TZ=UTC

ENTRYPOINT ["dotnet", "TradingAlertSystem.Console.dll"]