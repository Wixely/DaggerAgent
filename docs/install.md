# Installing DaggerAgent

Three ways in, easiest first: a prebuilt release, the published Docker image, or a build from
source. Whichever you pick, configuration works the same way — see
[Configuration](../README.md#configuration) in the README.

## From a release

Every `v*` tag publishes zips on the [Releases page](https://github.com/Wixely/DaggerAgent/releases),
for `win-x64` and `linux-x64`, in two flavours:

| Package                 | Needs                                  | Size   |
| ----------------------- | -------------------------------------- | ------ |
| `self-contained`        | Nothing — the .NET runtime is bundled. | Larger |
| `framework-dependent`   | The [ASP.NET Core 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed. | Smaller |

Each zip holds a single `dagger` executable (`dagger.exe` on Windows) and `appsettings.json`.
Unzip it anywhere, then:

```bash
dagger                # interactive TUI
dagger run --prompt "Summarise this folder"
dagger serve          # HTTP service + web UI on http://localhost:5090/agent/ui
```

On Linux, `chmod +x dagger` first.

Put your own settings in an `appsettings.Local.json` beside the executable rather than editing
`appsettings.json`, so upgrading is just replacing the executable and `appsettings.json`. Jobs,
logs and `data/runtime-config.json` (what the web UI saves) are written next to the executable
too, and are kept across upgrades.

### As a Windows Service

From the folder you unzipped into:

```cmd
sc.exe create DaggerAgent binPath= "%CD%\dagger.exe serve" start= auto
sc.exe start DaggerAgent
```

DaggerAgent detects that it is running as a service on its own; the explicit `serve` argument
makes service mode reliable in containers too. The service runs as LocalSystem by default — use
`sc.exe config DaggerAgent obj= ...` to run it as an account that has the access its tools need.

## Docker

Each release also pushes a multi-arch (`linux/amd64`, `linux/arm64`) image to the GitHub
Container Registry. It is public — no login needed to pull:

```bash
docker run --rm -p 5090:5090 \
  -e DAGGER_OpenAI__BaseUrl=http://host.docker.internal:1234/v1 \
  -v daggeragent-data:/data -v daggeragent-logs:/app/logs \
  -v daggeragent-config:/app/data \
  ghcr.io/wixely/daggeragent:latest
```

Tags follow the release: `latest`, `1.12.0`, `1.12`, and so on. The image starts in `serve`
mode, with three volumes: `/data` for the job database, `/app/data` for
`runtime-config.json` (what you change in the web UI: endpoints, MCP servers, triggers,
Banter), and `/app/logs`. Name them as above — with `--rm`, unnamed volumes are deleted along
with the container, and your web UI settings with them.

### Building the image yourself

The build restores packages from the Wixely GitHub Packages feed (see below), so it needs a
token, passed as a BuildKit secret so it never lands in an image layer:

```bash
export GITHUB_PACKAGES_TOKEN=ghp_...   # a PAT with read:packages
docker build \
  --build-arg GITHUB_PACKAGES_USER=your-github-username \
  --secret id=github_packages_token,env=GITHUB_PACKAGES_TOKEN \
  -t daggeragent .
```

## From source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

Two dependencies — `AgentClientProtocol` (the Wixely/acp-csharp fork) and `Banter.Agents.Sdk` —
come from the Wixely GitHub Packages feed, which [`nuget.config`](../nuget.config) adds next to
nuget.org. GitHub Packages requires authentication even to read, so set these before restoring:

```bash
export GITHUB_PACKAGES_USER=your-github-username
export GITHUB_PACKAGES_TOKEN=ghp_...   # a PAT with the read:packages scope
```

(PowerShell: `$env:GITHUB_PACKAGES_USER = "..."` and so on.)

Then run it straight from the checkout:

```bash
dotnet run                     # interactive TUI
dotnet run -- serve            # HTTP service
```

or publish your own build:

```bash
dotnet publish -c Release -o publish
```
