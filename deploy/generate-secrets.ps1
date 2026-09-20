<#
.SYNOPSIS
    Generates the secrets of a CRM production installation: deploy\.env (passwords) and deploy\secrets\* (JWT signing key, platform admin password).

.DESCRIPTION
    Works on Windows PowerShell 5.1 and PowerShell 7 (no external tools needed). Nothing is printed except file names; secrets go only into files.
      - .env                                  copy of .env.example with POSTGRES_PASSWORD, CRM_OWNER_PASSWORD, CRM_APP_PASSWORD,
                                              CONDUCTOR_DB_PASSWORD, REDIS_PASSWORD (32 random alphanumeric characters each) filled in
      - secrets\jwt-signing-key.pem           2048-bit RSA private key (PEM, "RSA PRIVATE KEY") for signing JWT access tokens
      - secrets\platform-admin-password       one-time password of the first platform admin (create-platform-admin); empty the file afterwards
    Existing files are never overwritten unless -Force is given (rotating the JWT key signs every user out; rotating a DB password
    requires "docker compose up -d" afterwards so db-init re-syncs the role passwords).

.PARAMETER OutDir
    Target directory (default: the directory of this script, i.e. deploy\).
.PARAMETER PublicHostname
    Written to ALLOWED_HOSTS (default: crm.example.local).
.PARAMETER PlatformAdminEmail
    Written to PLATFORM_ADMIN_EMAIL.
.PARAMETER Force
    Overwrite existing .env / secret files.

.EXAMPLE
    .\generate-secrets.ps1 -PublicHostname crm.company.local -PlatformAdminEmail admin@company.local
#>
[CmdletBinding()]
param(
    [string]$OutDir = $PSScriptRoot,
    [string]$PublicHostname = 'crm.example.local',
    [string]$PlatformAdminEmail = '',
    [string]$PlatformAdminName = 'Platform Admin',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$Rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()

function New-RandomSecret {
    param([int]$Length = 32)
    # Unbiased pick from an alphabet without look-alike characters; no ';', quotes or spaces (they would break connection strings).
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789'
    $limit = 256 - (256 % $alphabet.Length)
    $sb = New-Object System.Text.StringBuilder
    $buffer = New-Object 'byte[]' 1
    while ($sb.Length -lt $Length) {
        $Rng.GetBytes($buffer)
        if ($buffer[0] -lt $limit) { [void]$sb.Append($alphabet[$buffer[0] % $alphabet.Length]) }
    }
    return $sb.ToString()
}

# ---- minimal ASN.1 DER writer: enough to serialize a PKCS#1 RSAPrivateKey (Windows PowerShell 5.1 / .NET Framework has no ExportPkcs8PrivateKey)
function New-DerTlv {
    param([byte]$Tag, [byte[]]$Value)
    $out = New-Object 'System.Collections.Generic.List[byte]'
    $out.Add($Tag)
    $len = $Value.Length
    if ($len -lt 128) {
        $out.Add([byte]$len)
    } else {
        $lenBytes = New-Object 'System.Collections.Generic.List[byte]'
        $rest = $len
        while ($rest -gt 0) { $lenBytes.Insert(0, [byte]($rest -band 0xFF)); $rest = $rest -shr 8 }
        $out.Add([byte](0x80 -bor $lenBytes.Count))
        $out.AddRange($lenBytes)
    }
    $out.AddRange($Value)
    return , $out.ToArray()
}

function New-DerInteger {
    param([byte[]]$BigEndian)
    $i = 0
    while ($i -lt ($BigEndian.Length - 1) -and $BigEndian[$i] -eq 0) { $i++ }
    $v = New-Object 'System.Collections.Generic.List[byte]'
    if ($BigEndian[$i] -ge 0x80) { $v.Add(0) }
    for ($j = $i; $j -lt $BigEndian.Length; $j++) { $v.Add($BigEndian[$j]) }
    return , (New-DerTlv -Tag 0x02 -Value $v.ToArray())
}

function New-RsaPrivateKeyPem {
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(2048)
    try {
        $p = $rsa.ExportParameters($true)
    } finally {
        $rsa.PersistKeyInCsp = $false
        $rsa.Dispose()
    }
    $body = New-Object 'System.Collections.Generic.List[byte]'
    foreach ($part in @((New-Object 'byte[]' 1), $p.Modulus, $p.Exponent, $p.D, $p.P, $p.Q, $p.DP, $p.DQ, $p.InverseQ)) {
        [byte[]]$der = New-DerInteger -BigEndian ([byte[]]$part)
        $body.AddRange($der)
    }
    [byte[]]$seq = New-DerTlv -Tag 0x30 -Value $body.ToArray()
    $b64 = [Convert]::ToBase64String($seq)
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('-----BEGIN RSA PRIVATE KEY-----')
    for ($k = 0; $k -lt $b64.Length; $k += 64) { $lines.Add($b64.Substring($k, [Math]::Min(64, $b64.Length - $k))) }
    $lines.Add('-----END RSA PRIVATE KEY-----')
    return (($lines -join "`n") + "`n")
}

function Write-SecretFile {
    param([string]$Path, [string]$Content)
    if ((Test-Path -LiteralPath $Path) -and -not $Force) {
        Write-Warning "Exists, kept (use -Force to overwrite): $Path"
        return $false
    }
    [System.IO.File]::WriteAllText($Path, $Content, $Utf8NoBom)
    Write-Host "Wrote $Path"
    return $true
}

function Restrict-Access {
    # Best effort: only the current user (and SYSTEM/Administrators) may read the secret material. No-op where icacls is missing.
    param([string]$Path)
    if (-not (Get-Command icacls.exe -ErrorAction SilentlyContinue)) { return }
    $user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls.exe $Path /inheritance:r /grant:r "${user}:(F)" 'SYSTEM:(F)' 'Administrators:(F)' | Out-Null
}

# ---- run ------------------------------------------------------------------------------------------------------------------------
$example = Join-Path $PSScriptRoot '.env.example'
if (-not (Test-Path -LiteralPath $example)) { throw ".env.example not found next to this script: $example" }
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$secretsDir = Join-Path $OutDir 'secrets'
if (-not (Test-Path -LiteralPath $secretsDir)) { New-Item -ItemType Directory -Path $secretsDir | Out-Null }

$values = @{
    'ALLOWED_HOSTS'         = $PublicHostname
    'POSTGRES_PASSWORD'     = New-RandomSecret
    'CRM_OWNER_PASSWORD'    = New-RandomSecret
    'CRM_APP_PASSWORD'      = New-RandomSecret
    'CONDUCTOR_DB_PASSWORD' = New-RandomSecret
    'REDIS_PASSWORD'        = New-RandomSecret
    'PLATFORM_ADMIN_EMAIL'  = $PlatformAdminEmail
    'PLATFORM_ADMIN_NAME'   = $PlatformAdminName
}

$envLines = @()
foreach ($line in [System.IO.File]::ReadAllLines($example)) {
    $m = [regex]::Match($line, '^([A-Z][A-Z0-9_]*)=(.*)$')
    if ($m.Success -and $values.ContainsKey($m.Groups[1].Value)) {
        $envLines += ($m.Groups[1].Value + '=' + $values[$m.Groups[1].Value])
    } else {
        $envLines += $line
    }
}
$envPath = Join-Path $OutDir '.env'
$wroteEnv = Write-SecretFile -Path $envPath -Content (($envLines -join "`n") + "`n")

$keyPath = Join-Path $secretsDir 'jwt-signing-key.pem'
$wroteKey = Write-SecretFile -Path $keyPath -Content (New-RsaPrivateKeyPem)

# AES-256 key (base64, 32 bytes) for webhook secrets at rest (M8B). Never replaced by -Force: regenerating it would make every stored webhook secret unreadable.
$intKeyPath = Join-Path $secretsDir 'integrations-encryption-key'
if (-not (Test-Path -LiteralPath $intKeyPath)) {
    $keyBytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($keyBytes) } finally { $rng.Dispose() }
    [System.IO.File]::WriteAllText($intKeyPath, [Convert]::ToBase64String($keyBytes), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Wrote $intKeyPath"
} else {
    Write-Host "Exists, kept: $intKeyPath"
}

$pwPath = Join-Path $secretsDir 'platform-admin-password'
$wrotePw = Write-SecretFile -Path $pwPath -Content (New-RandomSecret -Length 24)

# Observability overlay (docker-compose.observability.yml, C-OPS1): scrape bearer token, Grafana admin password, postgres-exporter role password.
$obsPaths = @()
foreach ($name in @('metrics-bearer-token', 'grafana-admin-password', 'pg-monitor-password')) {
    $p = Join-Path $secretsDir $name
    [void](Write-SecretFile -Path $p -Content (New-RandomSecret -Length 40))
    $obsPaths += $p
}

foreach ($p in (@($envPath, $keyPath, $pwPath) + $obsPaths)) { if (Test-Path -LiteralPath $p) { Restrict-Access -Path $p } }
Restrict-Access -Path $secretsDir

Write-Host ''
Write-Host 'Done. Next steps:'
Write-Host '  1. Review .env (ALLOWED_HOSTS, WEB_BIND/WEB_PORT, PLATFORM_ADMIN_EMAIL, resource limits).'
Write-Host '  2. Store .env and secrets\ in your secret vault / encrypted backup. The JWT key and DB passwords are needed to restore.'
Write-Host '  3. docker compose -f deploy/docker-compose.prod.yml up -d ; then create the first platform admin:'
Write-Host '       docker compose -f deploy/docker-compose.prod.yml run --rm migrator create-platform-admin'
Write-Host '     The password is in secrets\platform-admin-password (read it once, then empty the file).'
if (-not $wroteEnv) { Write-Warning '.env was NOT regenerated; existing passwords are unchanged.' }
