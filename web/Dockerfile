# syntax=docker/dockerfile:1
#
# Production image for the SPA: Vite build -> nginx (unprivileged, port 8080) that serves the static files with history
# fallback and reverse-proxies /api to the api service. Build context MUST be the repository root:
#   docker build -f web/Dockerfile -t crm-web .
# (the pnpm workspace manifest and lockfile live at the root.)

FROM node:24.21.0-alpine AS build
WORKDIR /repo

# Node 24 still bundles corepack; it activates the pnpm version pinned in package.json ("packageManager").
RUN corepack enable

# Manifests first so `pnpm install` stays cached until a dependency actually changes.
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY web/package.json web/package.json

RUN --mount=type=cache,target=/root/.local/share/pnpm/store \
    pnpm install --frozen-lockfile --filter web...

COPY web/ web/

# Vite inlines import.meta.env.* at build time. The default is same-origin: nginx proxies /api/* to the api service,
# so no host name is baked into the bundle and one image runs in every environment.
ARG VITE_API_BASE_URL=/api/v1
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL

RUN pnpm --filter web build

FROM nginxinc/nginx-unprivileged:1.29.8-alpine AS runtime

# The official entrypoint renders /etc/nginx/templates/*.template with the container's environment (envsubst) into
# /etc/nginx/conf.d/. Only variables that exist in the environment are substituted, nginx's own $variables are untouched.
COPY web/nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY web/nginx/security-headers.conf /etc/nginx/snippets/security-headers.conf
COPY --from=build /repo/web/dist /usr/share/nginx/html

# Runtime settings (all optional):
#   API_UPSTREAM        where /api is proxied to                                (default http://api:8080)
#   DNS_RESOLVER        resolver used to (re)resolve the upstream at run time   (default 127.0.0.11 = Docker's embedded DNS)
#   TRUSTED_PROXY_CIDR  address/network of the external TLS proxy / load balancer whose X-Forwarded-For is trusted
#                       (default 127.0.0.1/32 = loopback only = nothing is trusted, a client cannot spoof its address)
ENV API_UPSTREAM=http://api:8080 \
    DNS_RESOLVER=127.0.0.11 \
    TRUSTED_PROXY_CIDR=127.0.0.1/32

EXPOSE 8080

# 127.0.0.1, not localhost: musl's resolver prefers ::1 and the server only listens on IPv4.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD wget -q -O /dev/null http://127.0.0.1:8080/healthz || exit 1
