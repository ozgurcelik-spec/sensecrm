<#
.SYNOPSIS
    End-to-end runner (Windows PowerShell 5.1 / PowerShell 7): starts an ISOLATED copy of the production stack (compose project "crm-e2e",
    web on 127.0.0.1:8181), creates the platform admin, runs the Playwright suite and tears ONLY that project down again
    (containers, volumes, network) and deletes the temporary secrets. Twin of run.sh.

.DESCRIPTION
    Commands:
      all           (default) build images, up, suite with registration disabled (like production), suite with open registration, down
      up            build + start the stack and keep it (state in e2e\.stack-state) for debugging
      test          run the suite against the stack started with "up"   (-Phase registration-open for the open-registration tests)
      registration  recreate the api with -Mode open|disabled (after "up")
      down          tear the stack down (also cleans up after a crashed run)

    Environment options: E2E_SKIP_BUILD=1 (do not build; use E2E_IMAGE_VERSION, default "latest" = already built crm-*:latest images),
    E2E_WEB_PORT (default 8181), E2E_KEEP=1 (keep the stack after "all"), E2E_SKIP_OPEN_REGISTRATION=1, E2E_TIMEOUT_SECONDS.
    It never touches other compose projects (crm-prod, senseik-*), their containers, volumes or ports.

.PARAMETER PwArgs
    Extra arguments for "playwright test" (main phase only), e.g. -PwArgs '--grep "wrong password"'.

.EXAMPLE
    .\e2e\run.ps1
.EXAMPLE
    .\e2e\run.ps1 up ; .\e2e\run.ps1 test -PwArgs 'tests/auth.spec.ts' ; .\e2e\run.ps1 down
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('all', 'up', 'test', 'down', 'registration')]
    [string]$Command = 'all',
    [Parameter(Position = 1)]
    [string]$Mode = '',
    [string]$Phase = 'main',
    [string]$PwArgs = ''
)

$ErrorActionPreference = 'Stop'

$Here = $PSScriptRoot
$Repo = Split-Path -Parent $Here
$ComposeFile = Join-Path $Repo 'deploy\docker-compose.prod.yml'
$OverlayFile = Join-Path $Here 'docker-compose.e2e.yml'   # test-only: raises the anonymous auth rate limit (see the file)
$StateFile = Join-Path $Here '.stack-state'

$Project = 'crm-e2e'                                       # never changed: every docker command below carries -p $Project
$WebPort = if ($env:E2E_WEB_PORT) { $env:E2E_WEB_PORT } else { '8181' }
$PublicHost = 'crm.e2e.local'
$BaseUrl = "http://localhost:$WebPort"
$TimeoutSeconds = if ($env:E2E_TIMEOUT_SECONDS) { [int]$env:E2E_TIMEOUT_SECONDS } else { 420 }
$BackendSubnet = if ($env:E2E_BACKEND_SUBNET) { $env:E2E_BACKEND_SUBNET } else { '10.213.177.0/24' }
$FrontendSubnet = if ($env:E2E_FRONTEND_SUBNET) { $env:E2E_FRONTEND_SUBNET } else { '10.213.178.0/24' }
$AdminEmail = 'platform-admin@e2e.local'

# Ports that belong to other stacks on the developer machine.
if (@('8080', '5080', '5173', '15433', '13000', '18080', '18081', '15432', '16379') -contains $WebPort) {
    throw "E2E_WEB_PORT=$WebPort is reserved for another stack"
}

$script:OutDir = ''

function Write-Step([string]$Text) { Write-Host ""; Write-Host "== $Text" }

function Invoke-Native {
    # Runs a native command and fails on a non-zero exit code (PowerShell 5.1 does not do this by itself).
    param([string]$File, [string[]]$Arguments, [switch]$AllowFailure)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) { throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function Invoke-Compose {
    param([string[]]$Arguments, [switch]$AllowFailure)
    $base = @('compose', '-p', $Project, '-f', $ComposeFile, '-f', $OverlayFile, '--env-file', (Join-Path $script:OutDir '.env'))
    Invoke-Native -File 'docker' -Arguments ($base + $Arguments) -AllowFailure:$AllowFailure
}

function Set-StackEnvironment {
    param([string]$RegistrationMode = 'disabled')
    $secrets = ($script:OutDir -replace '\\', '/')
    $env:COMPOSE_PROJECT_NAME = $Project
    $env:WEB_BIND = '127.0.0.1'
    $env:WEB_PORT = $WebPort
    $env:BACKEND_SUBNET = $BackendSubnet
    $env:FRONTEND_SUBNET = $FrontendSubnet
    $env:TRUSTED_PROXY_CIDR = $FrontendSubnet
    $env:ALLOWED_HOSTS = $PublicHost
    $env:API_DOCS_ENABLED = 'false'
    $env:COMPOSE_PROFILES = ''
    $env:REGISTRATION_MODE = $RegistrationMode
    $env:CRM_VERSION = if ($env:E2E_IMAGE_VERSION) { $env:E2E_IMAGE_VERSION } else { 'e2e' }
    $env:CRM_REGISTRY = ''
    $env:JWT_SIGNING_KEY_FILE = "$secrets/secrets/jwt-signing-key.pem"
    $env:PLATFORM_ADMIN_PASSWORD_FILE = "$secrets/secrets/platform-admin-password"
    $env:PLATFORM_ADMIN_EMAIL = $AdminEmail
    $env:PLATFORM_ADMIN_NAME = 'E2E Platform Admin'
    $env:PLATFORM_ORG_NAME = 'E2E Platform'
}

function Test-Http([string]$Url) {
    try {
        $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
        return ($r.StatusCode -eq 200)
    } catch { return $false }
}

function Wait-Until {
    param([string]$What, [scriptblock]$Condition)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) {
            Invoke-Compose -Arguments @('ps', '-a') -AllowFailure
            Invoke-Compose -Arguments @('logs', '--tail=60', 'api', 'web', 'migrator') -AllowFailure
            throw "timed out waiting for $What"
        }
        Start-Sleep -Seconds 3
    }
    Write-Host "ok: $What"
}

function Wait-Signup([bool]$Expected) {
    $needle = '"signupEnabled":' + $Expected.ToString().ToLower()
    Wait-Until "signupEnabled=$($Expected.ToString().ToLower())" {
        try { (Invoke-WebRequest -Uri "$BaseUrl/api/v1/auth/config" -UseBasicParsing -TimeoutSec 5).Content -like "*$needle*" } catch { $false }
    }
}

# The api starts as soon as Conductor has STARTED, not when it is healthy: a workflow that starts in that gap fails with
# workflow.engine_unavailable (README, Bulgular F-4). The suite must not begin before the engine is ready.
function Wait-Healthy([string]$Service) {
    Wait-Until "$Service healthy" {
        $id = (Invoke-Compose -Arguments @('ps', '-q', $Service) | Out-String).Trim()
        if (-not $id) { return $false }
        return ((& docker inspect -f '{{.State.Health.Status}}' $id | Out-String).Trim() -eq 'healthy')
    }
}

function Get-ProjectResourceCount {
    $c = @(& docker ps -aq --filter "label=com.docker.compose.project=$Project").Count
    $v = @(& docker volume ls -q --filter "label=com.docker.compose.project=$Project").Count
    $n = @(& docker network ls -q --filter "label=com.docker.compose.project=$Project").Count
    return "$c $v $n"
}

function Stop-Stack {
    Write-Step "tearing down project $Project (containers, volumes, network)"
    if ($script:OutDir -and (Test-Path -LiteralPath (Join-Path $script:OutDir '.env'))) {
        Set-StackEnvironment
        Invoke-Compose -Arguments @('down', '-v', '--remove-orphans', '--timeout', '20') -AllowFailure
        if ((Split-Path -Leaf $script:OutDir) -like 'crm-e2e-*') {
            Remove-Item -LiteralPath $script:OutDir -Recurse -Force -ErrorAction SilentlyContinue
        } else {
            Write-Warning "refusing to delete unexpected directory $script:OutDir"
        }
    } else {
        # No state (crashed run): remove exactly the resources that carry this project's label.
        Write-Host 'no state; removing by project label only'
        foreach ($id in @(& docker ps -aq --filter "label=com.docker.compose.project=$Project")) { & docker rm -f $id | Out-Null }
        foreach ($id in @(& docker volume ls -q --filter "label=com.docker.compose.project=$Project")) { & docker volume rm -f $id | Out-Null }
        foreach ($id in @(& docker network ls -q --filter "label=com.docker.compose.project=$Project")) { & docker network rm $id | Out-Null }
    }
    if (Test-Path -LiteralPath $StateFile) { Remove-Item -LiteralPath $StateFile -Force }
    Write-Host "leftover containers/volumes/networks of ${Project}: $(Get-ProjectResourceCount)"
    $script:OutDir = ''
}

function Read-State {
    if (-not (Test-Path -LiteralPath $StateFile)) { throw 'no running e2e stack (run: .\e2e\run.ps1 up)' }
    $script:OutDir = (Get-Content -LiteralPath $StateFile -Raw).Trim()
}

function Start-Stack {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'docker is required' }
    # A crashed earlier run may have left the project behind: remove exactly that project first.
    if (Test-Path -LiteralPath $StateFile) { $script:OutDir = (Get-Content -LiteralPath $StateFile -Raw).Trim(); Stop-Stack }
    if (@(& docker ps -aq --filter "label=com.docker.compose.project=$Project").Count -gt 0) { $script:OutDir = ''; Stop-Stack }

    $script:OutDir = Join-Path ([System.IO.Path]::GetTempPath()) ('crm-e2e-' + [System.IO.Path]::GetRandomFileName().Replace('.', ''))
    New-Item -ItemType Directory -Path $script:OutDir | Out-Null
    [System.IO.File]::WriteAllText($StateFile, $script:OutDir)

    Write-Step "generating isolated secrets in $script:OutDir"
    & (Join-Path $Repo 'deploy\generate-secrets.ps1') -OutDir $script:OutDir -PublicHostname $PublicHost -PlatformAdminEmail $AdminEmail -PlatformAdminName 'E2E Platform Admin' | Out-Null
    Set-StackEnvironment

    if ($env:E2E_SKIP_BUILD -ne '1') {
        Write-Step "building images (crm-*:$($env:CRM_VERSION); Docker layer cache makes repeat runs cheap)"
        Invoke-Compose -Arguments @('build')
    } else {
        Write-Step "skipping build: reusing images crm-*:$($env:CRM_VERSION)"
    }

    Write-Step "starting stack (registration: $($env:REGISTRATION_MODE), web: $BaseUrl)"
    Invoke-Compose -Arguments @('up', '-d')
    Wait-Until 'web /healthz' { Test-Http "$BaseUrl/healthz" }
    Wait-Until 'api via nginx (/api/v1/auth/config)' { Test-Http "$BaseUrl/api/v1/auth/config" }
    Wait-Healthy 'conductor'

    Write-Step 'creating the platform admin'
    Invoke-Compose -Arguments @('run', '--rm', 'migrator', 'create-platform-admin')

    # Production mode is asserted at the source too (the browser suite can only see what nginx forwards): API docs must be closed.
    Write-Step 'asserting production mode inside the api container'
    foreach ($path in @('/scalar', '/openapi/v1.json')) {
        $code = (Invoke-Compose -Arguments @('exec', '-T', 'api', 'curl', '-s', '-o', '/dev/null', '-w', '%{http_code}', "http://localhost:8080$path") | Out-String).Trim()
        if ($code -ne '404') { throw "api answers $code (expected 404) on $path : documentation endpoints are open" }
        Write-Host "ok: api $path -> 404"
    }
}

function Invoke-Suite {
    param([string]$SuitePhase, [string[]]$ExtraArgs = @())
    $env:E2E_BASE_URL = $BaseUrl
    $env:E2E_PHASE = $SuitePhase
    $env:E2E_PLATFORM_ADMIN_EMAIL = $AdminEmail
    $env:E2E_PLATFORM_ADMIN_PASSWORD = (Get-Content -LiteralPath (Join-Path $script:OutDir 'secrets\platform-admin-password') -Raw).Trim()
    Push-Location $Here
    try {
        $pnpm = if (Get-Command pnpm -ErrorAction SilentlyContinue) { @('pnpm') } else { @('corepack', 'pnpm') }
        $exe = $pnpm[0]
        $pre = @($pnpm | Select-Object -Skip 1)
        Invoke-Native -File $exe -Arguments ($pre + @('exec', 'playwright', 'install', 'chromium')) | Out-Null
        Invoke-Native -File $exe -Arguments ($pre + @('exec', 'playwright', 'test') + $ExtraArgs)
    } finally {
        Pop-Location
        Remove-Item Env:\E2E_PLATFORM_ADMIN_PASSWORD -ErrorAction SilentlyContinue
    }
}

function Save-StackLogs {
    $dir = Join-Path $Here 'artifacts'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    try {
        Set-StackEnvironment
        Invoke-Compose -Arguments @('ps', '-a') -AllowFailure | Out-File -FilePath (Join-Path $dir 'stack-ps.txt') -Encoding utf8
        Invoke-Compose -Arguments @('logs', '--no-color', '--tail=300') -AllowFailure | Out-File -FilePath (Join-Path $dir 'stack.log') -Encoding utf8
    } catch { Write-Warning "could not save stack logs: $_" }
}

$pwList = @()
if ($PwArgs) {
    # split on spaces, keeping "quoted phrases" together
    $pwList = @([regex]::Matches($PwArgs, '"([^"]*)"|(\S+)') | ForEach-Object { if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value } })
}

switch ($Command) {
    'up' {
        try { Start-Stack } catch { Save-StackLogs; Stop-Stack; throw }
        Write-Host ''
        Write-Host "Stack is up at $BaseUrl. Run '.\e2e\run.ps1 test' and finally '.\e2e\run.ps1 down'."
    }
    'test' {
        Read-State
        Set-StackEnvironment
        Invoke-Suite -SuitePhase $Phase -ExtraArgs $pwList
    }
    'registration' {
        if ($Mode -notin 'open', 'disabled') { throw 'usage: run.ps1 registration open|disabled' }
        Read-State
        Set-StackEnvironment -RegistrationMode $Mode
        Invoke-Compose -Arguments @('up', '-d', 'api')
        Wait-Signup ($Mode -eq 'open')
    }
    'down' {
        if (Test-Path -LiteralPath $StateFile) { $script:OutDir = (Get-Content -LiteralPath $StateFile -Raw).Trim() }
        Stop-Stack
    }
    'all' {
        $failed = $false
        try {
            Start-Stack
            Write-Step 'suite: main (registration disabled, like production)'
            try { Invoke-Suite -SuitePhase 'main' -ExtraArgs $pwList } catch { $failed = $true; Write-Warning $_ }
            if ($env:E2E_SKIP_OPEN_REGISTRATION -ne '1' -and $pwList.Count -eq 0) {
                Write-Step 'suite: open-registration variant (api recreated with Registration__Mode=open)'
                Set-StackEnvironment -RegistrationMode 'open'
                Invoke-Compose -Arguments @('up', '-d', 'api')
                Wait-Signup $true
                try { Invoke-Suite -SuitePhase 'registration-open' } catch { $failed = $true; Write-Warning $_ }
            }
            if ($failed) { Save-StackLogs }
        } catch {
            $failed = $true
            Write-Warning $_
            if ($script:OutDir) { Save-StackLogs }
        } finally {
            if ($env:E2E_KEEP -eq '1') {
                Write-Host "E2E_KEEP=1: stack left running ($BaseUrl); tear down with '.\e2e\run.ps1 down'"
            } else {
                Stop-Stack
            }
        }
        if ($failed) { exit 1 }
    }
}
