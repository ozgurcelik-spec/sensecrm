<#
.SYNOPSIS
    Logical backup of the "crm" and "conductor" databases of the production stack (Windows PowerShell 5.1 / PowerShell 7).

.DESCRIPTION
    pg_dump (plain SQL, no owners/privileges/comments; crm: schema "public" excluded, db-init owns it) runs INSIDE the postgres container, is gzip-compressed there, copied out with
    "docker compose cp" (binary-safe, no PowerShell pipeline encoding problems), integrity-checked and pruned by age.
    Encryption is MANDATORY: without -GpgRecipient (or the BACKUP_GPG_RECIPIENT environment variable) the script refuses to run (exit code 2,
    before any docker command) unless -NoEncryption is passed, which stores the backup in CLEAR TEXT and prints a loud warning.
    Not included: deploy\.env and deploy\secrets\ (JWT signing key, DB passwords) - back those up separately and encrypted.

.PARAMETER BackupDir      Target directory (default deploy\backups).
.PARAMETER RetentionDays  Delete backups this script created that are older than N days (default 14).
.PARAMETER EnvFile        Compose env file (default deploy\.env).
.PARAMETER GpgRecipient   Required unless -NoEncryption: encrypt every file with gpg for this recipient (gpg + public key required); the plain .gz is removed.
                          Default: the BACKUP_GPG_RECIPIENT environment variable.
.PARAMETER NoEncryption   Explicit opt-out: store the backup in clear text (loud warning).

.EXAMPLE
    .\backup.ps1 -BackupDir D:\backups\crm -RetentionDays 30 -GpgRecipient backup@company.local
    Scheduled task: powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\crm\deploy\backup.ps1
#>
[CmdletBinding()]
param(
    [string]$BackupDir = (Join-Path $PSScriptRoot 'backups'),
    [int]$RetentionDays = 14,
    [string]$EnvFile = (Join-Path $PSScriptRoot '.env'),
    [string]$GpgRecipient = $env:BACKUP_GPG_RECIPIENT,
    [switch]$NoEncryption
)

$ErrorActionPreference = 'Stop'

# Encryption is mandatory. Checked BEFORE any docker command so a misconfigured run never produces a clear-text dump.
if ($NoEncryption) {
    Write-Warning 'WARNING: -NoEncryption given: the backups are stored in CLEAR TEXT (all customer data, password hashes, tokens). Protect the target directory, or use -GpgRecipient.'
    $GpgRecipient = ''
} elseif (-not $GpgRecipient) {
    [Console]::Error.WriteLine('REFUSING to run: backup encryption is mandatory and no key is configured.')
    [Console]::Error.WriteLine('  pass -GpgRecipient <gpg key id / e-mail> (or set BACKUP_GPG_RECIPIENT; the public key must be in the gpg keyring),')
    [Console]::Error.WriteLine('  or pass -NoEncryption to knowingly store the backup in clear text.')
    exit 2
} elseif (-not (Get-Command gpg -ErrorAction SilentlyContinue)) {
    [Console]::Error.WriteLine('REFUSING to run: -GpgRecipient is set but gpg is not installed / not on PATH.')
    exit 2
}

$compose = @('compose', '-f', (Join-Path $PSScriptRoot 'docker-compose.prod.yml'))
if (Test-Path -LiteralPath $EnvFile) { $compose += @('--env-file', $EnvFile) }

function Invoke-Compose {
    param([string[]]$Arguments)
    # docker writes progress ("Copying ...") to stderr; Windows PowerShell 5.1 turns native stderr into terminating errors under
    # $ErrorActionPreference='Stop', so stderr goes to a file and is only shown when the command fails.
    $errFile = [System.IO.Path]::GetTempFileName()
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & docker @compose @Arguments 2> $errFile
        $exitCode = $LASTEXITCODE
        $ErrorActionPreference = $previousPreference
        if ($exitCode -ne 0) {
            $details = (Get-Content -LiteralPath $errFile -Raw)
            throw "docker $($Arguments -join ' ') failed with exit code ${exitCode}: $details"
        }
    } finally {
        Remove-Item -LiteralPath $errFile -Force -ErrorAction SilentlyContinue
    }
}

function Test-GzipFile {
    param([string]$Path)
    $in = [System.IO.File]::OpenRead($Path)
    try {
        $gz = New-Object System.IO.Compression.GZipStream($in, [System.IO.Compression.CompressionMode]::Decompress)
        $buffer = New-Object 'byte[]' 65536
        while ($gz.Read($buffer, 0, $buffer.Length) -gt 0) { }
    } finally {
        $in.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir | Out-Null }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

foreach ($db in @('crm', 'conductor')) {
    $target = Join-Path $BackupDir "$db-$stamp.sql.gz"
    # crm: everything lives in module schemas (public holds only db-init helpers). conductor: its tables ARE in public, so nothing is excluded.
    $exclude = ""; if ($db -eq "crm") { $exclude = "--exclude-schema=public" }
    Write-Host "[$(Get-Date -Format T)] dumping $db ..."
    Invoke-Compose -Arguments @('exec', '-T', 'postgres', 'sh', '-c', "pg_dump -U postgres --no-owner --no-privileges --no-comments $exclude -f /tmp/$db.sql $db && gzip -9 -f /tmp/$db.sql")
    Invoke-Compose -Arguments @('cp', "postgres:/tmp/$db.sql.gz", $target)
    Invoke-Compose -Arguments @('exec', '-T', 'postgres', 'rm', '-f', "/tmp/$db.sql.gz")

    Test-GzipFile -Path $target   # throws on a truncated/corrupt archive

    if ($GpgRecipient) {
        & gpg --batch --yes --encrypt --recipient $GpgRecipient --output "$target.gpg" $target
        if ($LASTEXITCODE -ne 0) {
            Remove-Item -LiteralPath $target, "$target.gpg" -Force -ErrorAction SilentlyContinue
            throw "gpg failed with exit code $LASTEXITCODE (the clear-text dump was removed)"
        }
        Remove-Item -LiteralPath $target -Force
        $target = "$target.gpg"
    } else {
        Write-Warning "$target is stored in CLEAR TEXT"
    }
    $size = [math]::Round((Get-Item -LiteralPath $target).Length / 1KB, 1)
    Write-Host "[$(Get-Date -Format T)] wrote $target ($size KB)"
}

# Retention: only files this script created (name pattern), older than RetentionDays.
$cutoff = (Get-Date).AddDays(-$RetentionDays)
Get-ChildItem -LiteralPath $BackupDir -File | Where-Object {
    ($_.Name -like 'crm-*.sql.gz*' -or $_.Name -like 'conductor-*.sql.gz*') -and $_.LastWriteTime -lt $cutoff
} | ForEach-Object {
    Write-Host "pruned $($_.Name)"
    Remove-Item -LiteralPath $_.FullName -Force
}
Write-Host "[$(Get-Date -Format T)] backup finished"
