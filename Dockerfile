# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY DaggerAgent.csproj ./
RUN dotnet restore DaggerAgent.csproj
COPY . ./
RUN dotnet publish DaggerAgent.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://0.0.0.0:5090 \
    DAGGER_Server__Host=0.0.0.0 \
    DAGGER_Server__Port=5090 \
    DAGGER_Jobs__ConnectionString="Data Source=/data/jobs.db"
VOLUME ["/data", "/app/logs"]
EXPOSE 5090
ENTRYPOINT ["dotnet", "Dagger.dll", "serve"]
