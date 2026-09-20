#!/usr/bin/env bash
# End-to-end runner: starts an ISOLATED copy of the production stack (compose project "crm-e2e", web on 127.0.0.1:8181), creates the
# platform admin, runs the Playwright suite against it and tears ONLY that project down again (containers, volumes, network, secrets).
# Twin of run.ps1 (Windows PowerShell 5.1). Needs: docker compose v2, openssl, curl, node 24 + corepack/pnpm.
#
#   ./e2e/run.sh                       # build images (layer cache), up, suite (registration closed) + open-registration phase, down
#   ./e2e/run.sh -- --grep "login"     # everything after "--" goes to `playwright test` (main phase only)
#   ./e2e/run.sh up | test | down      # step by step: keep the stack for debugging (state in e2e/.stack-state)
#
# Options (env): E2E_SKIP_BUILD=1 (do not build; use E2E_IMAGE_VERSION, default "latest" = already built crm-*:latest images),
#   E2E_WEB_PORT (default 8181), E2E_KEEP=1 (do not tear down after "all"), E2E_SKIP_OPEN_REGISTRATION=1, E2E_TIMEOUT_SECONDS (health wait).
# It never touches other compose projects (crm-prod, senseik-*), their containers, volumes or ports.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
compose_file="$repo/deploy/docker-compose.prod.yml"
overlay_file="$here/docker-compose.e2e.yml"   # test-only: raises the anonymous auth rate limit (see the file)
state_file="$here/.stack-state"

PROJECT="crm-e2e"                       # never changed: every docker command below carries -p $PROJECT
WEB_PORT="${E2E_WEB_PORT:-8181}"
PUBLIC_HOST="crm.e2e.local"
BASE_URL="http://localhost:${WEB_PORT}"
TIMEOUT="${E2E_TIMEOUT_SECONDS:-420}"
BACKEND_SUBNET_E2E="${E2E_BACKEND_SUBNET:-10.213.177.0/24}"
FRONTEND_SUBNET_E2E="${E2E_FRONTEND_SUBNET:-10.213.178.0/24}"

cmd="all"
case "${1:-}" in up|test|down|all|registration) cmd="$1"; shift ;; esac
[ "${1:-}" = "--" ] && shift
pw_args=("$@")

# Ports that belong to other stacks on the developer machine.
for p in 8080 5080 5173 15433 13000 18080 18081 15432 16379; do
  [ "$WEB_PORT" = "$p" ] && { echo "E2E_WEB_PORT=$p is reserved for another stack" >&2; exit 2; }
done

# Windows (Git Bash): docker.exe needs C:/... style paths in bind mounts.
native_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi; }

step() { printf '\n== %s\n' "$*"; }
die() { echo "FAILED: $*" >&2; exit 1; }

OUT_DIR=""
load_state() { [ -f "$state_file" ] || die "no running e2e stack (run: ./e2e/run.sh up)"; OUT_DIR="$(cat "$state_file")"; }

dc() {
  docker compose -p "$PROJECT" -f "$compose_file" -f "$overlay_file" --env-file "$OUT_DIR/.env" "$@"
}

export_env() {
  export COMPOSE_PROJECT_NAME="$PROJECT"
  export WEB_BIND="127.0.0.1" WEB_PORT
  export BACKEND_SUBNET="$BACKEND_SUBNET_E2E" FRONTEND_SUBNET="$FRONTEND_SUBNET_E2E" TRUSTED_PROXY_CIDR="$FRONTEND_SUBNET_E2E"
  export ALLOWED_HOSTS="$PUBLIC_HOST" API_DOCS_ENABLED="false" COMPOSE_PROFILES=""
  export REGISTRATION_MODE="${REGISTRATION_MODE:-disabled}"
  export CRM_VERSION="${E2E_IMAGE_VERSION:-e2e}" CRM_REGISTRY=""
  export JWT_SIGNING_KEY_FILE="$OUT_DIR/secrets/jwt-signing-key.pem"
  export PLATFORM_ADMIN_PASSWORD_FILE="$OUT_DIR/secrets/platform-admin-password"
  export PLATFORM_ADMIN_EMAIL="platform-admin@e2e.local" PLATFORM_ADMIN_NAME="E2E Platform Admin" PLATFORM_ORG_NAME="E2E Platform"
}

# The api container is recreated: poll the public config until it reports the expected signupEnabled (old container may still answer).
wait_for_signup() { # expected true|false
  local deadline=$((SECONDS + TIMEOUT))
  until curl -fsS --max-time 5 "$BASE_URL/api/v1/auth/config" 2>/dev/null | grep -q "\"signupEnabled\":$1"; do
    [ $SECONDS -lt $deadline ] && sleep 3 || { dc logs --tail=60 api || true; die "timed out waiting for signupEnabled=$1"; }
  done
  echo "ok: signupEnabled=$1"
}

http_ok() { curl -fsS -o /dev/null --max-time 5 "$1" 2>/dev/null; }

wait_for() { # description, url
  local deadline=$((SECONDS + TIMEOUT))
  until http_ok "$2"; do
    [ $SECONDS -lt $deadline ] && sleep 3 || { dc ps -a || true; dc logs --tail=60 api web migrator || true; die "timed out waiting for $1 ($2)"; }
  done
  echo "ok: $1"
}

# The api starts as soon as Conductor has STARTED, not when it is healthy: a workflow that starts in that gap fails with
# workflow.engine_unavailable (README, Bulgular F-4). The suite must not begin before the engine is ready.
wait_healthy() { # compose service
  local deadline=$((SECONDS + TIMEOUT)) id
  until id="$(dc ps -q "$1")" && [ -n "$id" ] && [ "$(docker inspect -f '{{.State.Health.Status}}' "$id" 2>/dev/null)" = "healthy" ]; do
    [ $SECONDS -lt $deadline ] && sleep 3 || { dc ps -a || true; dc logs --tail=60 "$1" || true; die "timed out waiting for $1 to become healthy"; }
  done
  echo "ok: $1 healthy"
}

stack_down() {
  step "tearing down project $PROJECT (containers, volumes, network)"
  [ -n "$OUT_DIR" ] && [ -f "$OUT_DIR/.env" ] || { echo "no state; removing by project label only"; \
    docker ps -aq --filter "label=com.docker.compose.project=$PROJECT" | xargs -r docker rm -f >/dev/null 2>&1 || true; \
    docker volume ls -q --filter "label=com.docker.compose.project=$PROJECT" | xargs -r docker volume rm -f >/dev/null 2>&1 || true; \
    docker network ls -q --filter "label=com.docker.compose.project=$PROJECT" | xargs -r docker network rm >/dev/null 2>&1 || true; \
    rm -f "$state_file"; return 0; }
  export_env
  dc down -v --remove-orphans --timeout 20 || true
  case "$OUT_DIR" in *crm-e2e-*) rm -rf "$OUT_DIR" ;; *) echo "refusing to delete unexpected dir $OUT_DIR" >&2 ;; esac
  rm -f "$state_file"
  leftovers="$(docker ps -aq --filter "label=com.docker.compose.project=$PROJECT" | wc -l | tr -d ' ')"
  leftovers="$leftovers $(docker volume ls -q --filter "label=com.docker.compose.project=$PROJECT" | wc -l | tr -d ' ')"
  leftovers="$leftovers $(docker network ls -q --filter "label=com.docker.compose.project=$PROJECT" | wc -l | tr -d ' ')"
  echo "leftover containers/volumes/networks of $PROJECT: $leftovers"
  OUT_DIR=""
}

stack_up() {
  command -v docker >/dev/null || die "docker is required"
  # A crashed earlier run may have left the project behind: remove exactly that project first.
  if [ -f "$state_file" ]; then OUT_DIR="$(cat "$state_file")"; stack_down; fi
  stale="$(docker ps -aq --filter "label=com.docker.compose.project=$PROJECT" | wc -l | tr -d ' ')"
  if [ "$stale" != "0" ]; then OUT_DIR=""; stack_down; fi

  OUT_DIR="$(native_path "$(mktemp -d "${TMPDIR:-/tmp}/crm-e2e-XXXXXX")")"
  echo "$OUT_DIR" >"$state_file"
  step "generating isolated secrets in $OUT_DIR"
  OUT_DIR="$OUT_DIR" PUBLIC_HOSTNAME="$PUBLIC_HOST" PLATFORM_ADMIN_EMAIL="platform-admin@e2e.local" PLATFORM_ADMIN_NAME="E2E Platform Admin" \
    bash "$repo/deploy/generate-secrets.sh" >/dev/null
  export_env

  if [ "${E2E_SKIP_BUILD:-0}" != "1" ]; then
    step "building images (crm-*:${CRM_VERSION}; Docker layer cache makes repeat runs cheap)"
    dc build
  else
    step "skipping build: reusing images crm-*:${CRM_VERSION}"
  fi

  step "starting stack (registration: $REGISTRATION_MODE, web: $BASE_URL)"
  dc up -d
  wait_for "web /healthz" "$BASE_URL/healthz"
  wait_for "api via nginx (/api/v1/auth/config)" "$BASE_URL/api/v1/auth/config"
  wait_healthy conductor

  step "creating the platform admin"
  dc run --rm migrator create-platform-admin

  # Production mode is asserted at the source too (the browser suite can only see what nginx forwards): API docs must be closed.
  step "asserting production mode inside the api container"
  for path in /scalar /openapi/v1.json; do
    code="$(dc exec -T api curl -s -o /dev/null -w '%{http_code}' "http://localhost:8080$path" | tr -d '\r\n')"
    [ "$code" = "404" ] || die "api answers $code (expected 404) on $path: documentation endpoints are open"
    echo "ok: api $path -> 404"
  done
}

read_admin_password() { tr -d '\r\n' <"$OUT_DIR/secrets/platform-admin-password"; }

run_suite() { # phase, extra playwright args...
  local phase="$1"; shift
  ( cd "$here"
    export E2E_BASE_URL="$BASE_URL" E2E_PHASE="$phase" E2E_PLATFORM_ADMIN_EMAIL="platform-admin@e2e.local" E2E_PLATFORM_ADMIN_PASSWORD
    E2E_PLATFORM_ADMIN_PASSWORD="$(read_admin_password)"
    if command -v pnpm >/dev/null 2>&1; then pnpm=(pnpm); else pnpm=(corepack pnpm); fi
    "${pnpm[@]}" exec playwright install chromium >/dev/null
    "${pnpm[@]}" exec playwright test "$@" )
}

dump_logs() {
  mkdir -p "$here/artifacts"
  ( export_env; dc ps -a >"$here/artifacts/stack-ps.txt" 2>&1; dc logs --no-color --tail=300 >"$here/artifacts/stack.log" 2>&1 ) || true
}

case "$cmd" in
  up)
    trap 'rc=$?; if [ $rc -ne 0 ]; then dump_logs; stack_down; fi' EXIT
    stack_up; echo; echo "Stack is up at $BASE_URL. Run './e2e/run.sh test' and finally './e2e/run.sh down'." ;;
  registration) # ./run.sh registration open|disabled : recreate the api with the given Registration:Mode
    load_state; ( export REGISTRATION_MODE="${pw_args[0]:?open or disabled}"; export_env; dc up -d api ); [ "${pw_args[0]}" = "open" ] && wait_for_signup true || wait_for_signup false ;;
  test) load_state; export_env; run_suite "${E2E_PHASE:-main}" "${pw_args[@]}" ;;
  down) [ -f "$state_file" ] && OUT_DIR="$(cat "$state_file")"; stack_down ;;
  all)
    status=0
    cleanup() { if [ "${E2E_KEEP:-0}" = "1" ]; then echo "E2E_KEEP=1: stack left running ($BASE_URL); tear down with './e2e/run.sh down'"; else stack_down; fi; }
    trap cleanup EXIT
    trap 'exit 130' INT TERM
    stack_up
    step "suite: main (registration disabled, like production)"
    run_suite main "${pw_args[@]}" || status=$?
    if [ "${E2E_SKIP_OPEN_REGISTRATION:-0}" != "1" ] && [ ${#pw_args[@]} -eq 0 ]; then
      step "suite: open-registration variant (api recreated with Registration__Mode=open)"
      ( export REGISTRATION_MODE=open; export_env; dc up -d api )
      wait_for_signup true
      run_suite registration-open || status=$?
    fi
    [ $status -eq 0 ] || dump_logs
    exit $status
    ;;
esac
