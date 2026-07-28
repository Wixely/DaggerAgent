# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# nuget.config adds the fork's GitHub Packages feed for the AgentClientProtocol package.
# The username is a build-arg; the token is mounted as a BuildKit secret so it never lands
# in an image layer. Both are read from the environment by nuget.config's %VAR% credentials.
ARG GITHUB_PACKAGES_USER=""
COPY DaggerAgent.csproj nuget.config ./
RUN --mount=type=secret,id=github_packages_token \
    GITHUB_PACKAGES_USER="$GITHUB_PACKAGES_USER" \
    GITHUB_PACKAGES_TOKEN="$(cat /run/secrets/github_packages_token 2>/dev/null || true)" \
    dotnet restore DaggerAgent.csproj
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
ENTRYPOINT ["dotnet", "dagger.dll", "serve"]
