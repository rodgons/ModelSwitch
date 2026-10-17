# syntax=docker/dockerfile:1
# The dashboard image (docs/deployment.md section 1): the Vite build, served as static files by Caddy.
#   docker build -f deploy/docker/web.Dockerfile .
# The build context is the repository root (see /.dockerignore).

FROM node:24-bookworm-slim AS build
WORKDIR /web
# The version in web/package.json "packageManager".
RUN npm install --global --no-fund --no-audit pnpm@12.6.0
COPY web/package.json web/pnpm-lock.yaml web/pnpm-workspace.yaml ./
RUN --mount=type=cache,id=modelswitch-pnpm,target=/root/.local/share/pnpm/store \
    pnpm install --frozen-lockfile
COPY web/ ./
RUN pnpm build

FROM caddy:2 AS web
COPY deploy/docker/web.Caddyfile /etc/caddy/Caddyfile
COPY --from=build /web/dist /srv
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --retries=3 CMD ["wget", "-q", "-O", "/dev/null", "http://127.0.0.1:8080/"]
