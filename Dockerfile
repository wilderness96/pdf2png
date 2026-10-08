# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY pdftopng.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0

# Ghostscript does the actual PDF rasterization.
# It runs with -dSAFER (no exec/file ops from PDF content, no JS/XFA execution).
RUN apt-get update \
    && apt-get install -y --no-install-recommends ghostscript \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Rendered pages live here; must be writable by the unprivileged user below.
RUN mkdir -p /app/jobs && chown -R app:app /app/jobs

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Drop to the unprivileged 'app' user shipped with the mcr base image.
USER app

ENTRYPOINT ["dotnet", "pdftopng.dll"]
