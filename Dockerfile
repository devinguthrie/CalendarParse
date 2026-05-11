# CalendarParse.Api — multi-stage Linux container
# Build: docker build -t calendarparse-api .
# Run:   docker run -p 5150:5150 \
#          -e CalendarParse__ApiKey=<key> \
#          -e CalendarParse__OllamaBaseUrl=<runpod-url> \
#          -e CalendarParse__OllamaModel=glm-ocr \
#          -e CalendarParse__OllamaApiStyle=runpod \
#          -e CalendarParse__RunPodApiKey=<runpod-key> \
#          -v /data:/data \
#          -e CalendarParse__DataDir=/data \
#          calendarparse-api

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 5150

# Emgu.CV requires libgomp (OpenMP) and libGL on Linux.
RUN apt-get update && apt-get install -y --no-install-recommends \
        libgomp1 \
        libglib2.0-0 \
        libgl1 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY CalendarParse.slnx ./
COPY Directory.Build.props ./
COPY CalendarParse.Api/CalendarParse.Api.csproj CalendarParse.Api/
COPY CalendarParse.Parsing/CalendarParse.Parsing.csproj CalendarParse.Parsing/
COPY CalendarParse.Core/CalendarParse.Core.csproj CalendarParse.Core/

# Restore only the API and its dependencies (skip MAUI / Tests)
RUN dotnet restore CalendarParse.Api/CalendarParse.Api.csproj

# Copy source
COPY CalendarParse.Api/ CalendarParse.Api/
COPY CalendarParse.Parsing/ CalendarParse.Parsing/
COPY CalendarParse.Core/ CalendarParse.Core/

RUN dotnet publish CalendarParse.Api/CalendarParse.Api.csproj \
        -c Release \
        -o /app/publish \
        --no-restore

FROM base AS final
WORKDIR /app

# Run as non-root for security
RUN useradd -m appuser && chown -R appuser /app
USER appuser

COPY --from=build --chown=appuser /app/publish .

ENTRYPOINT ["dotnet", "CalendarParse.Api.dll"]
