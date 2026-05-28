FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj files first for layer caching
COPY ["src/Pixelz.Domain/Pixelz.Domain.csproj",               "src/Pixelz.Domain/"]
COPY ["src/Pixelz.Application/Pixelz.Application.csproj",     "src/Pixelz.Application/"]
COPY ["src/Pixelz.Infrastructure/Pixelz.Infrastructure.csproj","src/Pixelz.Infrastructure/"]
COPY ["src/Pixelz.API/Pixelz.API.csproj",                     "src/Pixelz.API/"]

RUN dotnet restore "src/Pixelz.API/Pixelz.API.csproj"

COPY . .
WORKDIR "/src/src/Pixelz.API"
RUN dotnet publish "Pixelz.API.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Create log directory with correct permissions
RUN mkdir -p /app/logs

# Run as non-root
RUN adduser --disabled-password --gecos "" appuser \
    && chown -R appuser /app
USER appuser

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Pixelz.API.dll"]
