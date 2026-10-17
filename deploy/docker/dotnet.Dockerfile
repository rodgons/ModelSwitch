# syntax=docker/dockerfile:1
# The .NET images (docs/deployment.md section 1): one multi-stage build, one target per process.
#   docker build -f deploy/docker/dotnet.Dockerfile --target api .
# Targets: api, worker, migrator, mock-provider. The build context is the repository root (see /.dockerignore).

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
ENV DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
COPY tools/ tools/
# One restore and publish for every process: compose builds all four targets, and they share this stage.
RUN --mount=type=cache,id=modelswitch-nuget,target=/root/.nuget/packages \
    for project in src/ModelSwitch.Api src/ModelSwitch.Worker src/ModelSwitch.Migrator tools/ModelSwitch.MockProvider; do \
      name="$(basename "$project")"; \
      dotnet publish "$project/$name.csproj" -c Release -o "/app/$name" -p:UseAppHost=false || exit 1; \
    done

# Chiseled: no shell, no package manager, non-root (`app`, uid 1654). InvariantGlobalization is on, so no ICU is needed.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled AS runtime
WORKDIR /app
USER $APP_UID
ENV DOTNET_NOLOGO=true \
    ASPNETCORE_HTTP_PORTS=8080

FROM runtime AS api
COPY --from=build /app/ModelSwitch.Api ./
EXPOSE 8080
# No curl in a chiseled image: the app probes its own /health (src/ModelSwitch.SharedKernel/Hosting/HealthProbe.cs).
HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=3 CMD ["dotnet", "ModelSwitch.Api.dll", "--health-check"]
ENTRYPOINT ["dotnet", "ModelSwitch.Api.dll"]

# The worker serves no HTTP, so it has no HEALTHCHECK; Compose restarts it if it exits.
FROM runtime AS worker
COPY --from=build /app/ModelSwitch.Worker ./
ENTRYPOINT ["dotnet", "ModelSwitch.Worker.dll"]

# Runs to completion: `migrate` (default) or `seed --tenant <name>`.
FROM runtime AS migrator
COPY --from=build /app/ModelSwitch.Migrator ./
ENTRYPOINT ["dotnet", "ModelSwitch.Migrator.dll"]

FROM runtime AS mock-provider
COPY --from=build /app/ModelSwitch.MockProvider ./
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=5s --start-period=10s --retries=3 CMD ["dotnet", "ModelSwitch.MockProvider.dll", "--health-check"]
ENTRYPOINT ["dotnet", "ModelSwitch.MockProvider.dll"]
