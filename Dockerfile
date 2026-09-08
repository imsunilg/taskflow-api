# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY TaskManager.sln ./
COPY src/ ./src/
RUN dotnet restore src/TaskManager.Api/TaskManager.Api.csproj
RUN dotnet publish src/TaskManager.Api/TaskManager.Api.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
RUN adduser --disabled-password --gecos "" appuser
COPY --from=build /app ./
USER appuser
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "TaskManager.Api.dll"]
