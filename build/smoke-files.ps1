<#
.SYNOPSIS
  Canli duman testi (M8C dosya ekleri): gercek S3 uyumlu depoya (MinIO) ve gercek PostgreSQL'e karsi yukleme/indirme/kota/temizlik/imha akisini dogrular.

.DESCRIPTION
  Onkosullar (bu betik baslatmaz): migrate edilmis bir veritabani, calisan API (Files:Storage:Provider=s3) ve Worker
  (Files__Purge__PollMinutes=1, Platform__Deletion__PollMinutes=1), platform yoneticisi hesabi (migrator create-platform-admin),
  Docker'da postgres ve MinIO konteynerleri. Betik yalniz ASCII isimlerle konusur (Windows terminal kodlamasi guvenligi).
  Adimlar: kayit -> yukleme (PDF, PNG) -> indirme bayt bayt + Range + onizleme basliklari -> imza/uzanti reddi -> yeniden adlandirma -> silme ->
  kota 402 (istisna maxStorageMb) -> kota kaldirilinca gecer -> yumusak silinen dosyanin Worker temizligi (deleted_at gecmise cekilerek) ->
  salt okunur kip (askiya alma) -> KVKK silme talebi -> Worker imhasi -> onekte 0 nesne. Her adim [OK]/[FAIL] yazar; herhangi bir [FAIL] cikis kodunu 1 yapar.

.EXAMPLE
  ./build/smoke-files.ps1 -PlatformEmail platform@example.com -PlatformPassword '<parola>' -MinioRootUser crmrootadmin -MinioRootPassword '<parola>'
#>
[CmdletBinding()]
param(
    [string]$Api = 'http://localhost:5086',
    [Parameter(Mandatory = $true)][string]$PlatformEmail,
    [Parameter(Mandatory = $true)][string]$PlatformPassword,
    [string]$PgContainer = 'crm-m8c-pg',
    [string]$PgUser = 'crm',
    [string]$PgDatabase = 'crm_m8c',
    [string]$Bucket = 'crm-files',
    [string]$MinioContainer = 'crm-m8c-minio',
    [string]$DockerNetwork = 'crm-m8c-net',
    [Parameter(Mandatory = $true)][string]$MinioRootUser,
    [Parameter(Mandatory = $true)][string]$MinioRootPassword,
    [string]$McImage = 'quay.io/minio/mc:RELEASE.2025-08-13T08-35-41Z',
    [int]$WorkerWaitSeconds = 150
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$script:failures = 0
$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromMinutes(5)

function Check([bool]$condition, [string]$message) {
    if ($condition) { Write-Host "[OK]   $message" -ForegroundColor Green }
    else { Write-Host "[FAIL] $message" -ForegroundColor Red; $script:failures++ }
}

function Send([string]$method, [string]$path, [string]$token, $body = $null, $content = $null, [hashtable]$headers = @{}) {
    $request = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::new($method), "$Api$path")
    if ($token) { $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $token) }
    foreach ($key in $headers.Keys) { [void]$request.Headers.TryAddWithoutValidation($key, [string]$headers[$key]) }
    if ($null -ne $body) { $request.Content = New-Object System.Net.Http.StringContent(($body | ConvertTo-Json -Depth 8 -Compress), [Text.Encoding]::UTF8, 'application/json') }
    if ($null -ne $content) { $request.Content = $content }
    return $client.SendAsync($request).GetAwaiter().GetResult()
}

function Json($response) {
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ([string]::IsNullOrEmpty($text)) { return $null }
    return $text | ConvertFrom-Json
}

function Bytes($response) { return , $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult() }

function Sha([byte[]]$bytes) { return ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)) -replace '-', '').ToLowerInvariant() }

function Multipart([string]$fileName, [byte[]]$bytes) {
    $form = New-Object System.Net.Http.MultipartFormDataContent
    $part = New-Object System.Net.Http.ByteArrayContent (, $bytes)
    $form.Add($part, 'file', $fileName)
    # MultipartFormDataContent bir IEnumerable'dir: virgul olmadan PowerShell parcalara acar (gecersiz istek olur).
    return , $form
}

function Sql([string]$statement) {
    $out = docker exec $PgContainer psql -U $PgUser -d $PgDatabase -tAc $statement
    return ($out | Out-String).Trim()
}

function ObjectCount([string]$prefix) {
    $out = docker run --rm --network $DockerNetwork --entrypoint sh $McImage -c "mc alias set s http://${MinioContainer}:9000 $MinioRootUser '$MinioRootPassword' >/dev/null && mc ls --recursive s/$Bucket/$prefix | wc -l" 2>$null
    return [int](($out | Out-String).Trim())
}

function Pdf([int]$size) { $b = New-Object byte[] $size; [Text.Encoding]::ASCII.GetBytes("%PDF-1.7`n").CopyTo($b, 0); for ($i = 9; $i -lt $size; $i++) { $b[$i] = [byte](97 + ($i % 26)) }; return , $b }

# ---- Kurulum: kiracı + kayit -----------------------------------------------------------------------------------
$stamp = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$email = "smoke-$stamp@example.com"
$signup = Send 'POST' '/api/v1/auth/signup' $null @{ organizationName = "Smoke Files $stamp"; displayName = "Smoke Admin"; email = $email; password = 'Sm0ke-Tenant-91827-Qz'; locale = 'en' }
Check ($signup.StatusCode -eq 200) 'signup'
$auth = Json $signup
$token = $auth.accessToken
$me = Json (Send 'GET' '/api/v1/me' $token)
$tenantId = $me.organization.id
$account = (Json (Send 'POST' '/api/v1/accounts' $token @{ name = "Smoke Account $stamp" })).id
Check ([bool]$account) 'account created'

$platformLogin = Json (Send 'POST' '/api/v1/auth/login' $null @{ email = $PlatformEmail; password = $PlatformPassword })
$platform = $platformLogin.accessToken
Check ([bool]$platform) 'platform admin login'

# ---- Yukleme: PDF + PNG -----------------------------------------------------------------------------------------
$pdfBytes = Pdf 300000
$upload = Send 'POST' "/api/v1/files?recordType=account&recordId=$account" $token $null (Multipart 'Contract 2026.pdf' $pdfBytes)
Check ($upload.StatusCode -eq 201) 'upload pdf -> 201'
$pdf = (Json $upload).items[0]
Check ($pdf.sha256 -eq (Sha $pdfBytes) -and $pdf.sizeBytes -eq 300000 -and $pdf.contentType -eq 'application/pdf' -and $pdf.state -eq 'ready') 'pdf dto: sha256, size, canonical type, ready'

$pngBytes = New-Object byte[] 5000; ([byte[]]@(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)).CopyTo($pngBytes, 0)
$png = (Json (Send 'POST' "/api/v1/files?recordType=account&recordId=$account" $token $null (Multipart 'logo.png' $pngBytes))).items[0]
Check ([bool]$png.id) 'upload png'
$objects = ObjectCount "$tenantId/"
Check ($objects -eq 2) "object store holds exactly 2 objects under the tenant prefix (found $objects)"

# ---- Indirme bayt bayt, Range, onizleme basliklari --------------------------------------------------------------
$download = Send 'GET' "/api/v1/files/$($pdf.id)/content" $token
Check ($download.StatusCode -eq 200 -and (Sha (Bytes $download)) -eq (Sha $pdfBytes)) 'download is byte-for-byte identical'
Check ($download.Headers.GetValues('X-Content-Type-Options') -contains 'nosniff') 'download: nosniff'
Check ($download.Content.Headers.ContentDisposition.DispositionType -eq 'attachment') 'download: Content-Disposition attachment'

$range = Send 'GET' "/api/v1/files/$($pdf.id)/content" $token $null $null @{ Range = 'bytes=100-199' }
$rangeBytes = Bytes $range
Check ($range.StatusCode -eq 206 -and $rangeBytes.Length -eq 100 -and (Sha $rangeBytes) -eq (Sha $pdfBytes[100..199])) 'Range bytes=100-199 -> 206 with the exact slice'
Check ($range.Content.Headers.ContentRange.ToString() -eq 'bytes 100-199/300000') 'Content-Range header'
$badRange = Send 'GET' "/api/v1/files/$($pdf.id)/content" $token $null $null @{ Range = 'bytes=999999-' }
Check ([int]$badRange.StatusCode -eq 416) 'unsatisfiable Range -> 416'

$inline = Send 'GET' "/api/v1/files/$($png.id)/content?disposition=inline" $token
Check ($inline.StatusCode -eq 200 -and ($inline.Headers.GetValues('Content-Security-Policy') -join '') -like "*sandbox*" -and ($inline.Headers.GetValues('X-Frame-Options') -join '') -eq 'SAMEORIGIN') 'png inline preview: sandboxed CSP + SAMEORIGIN'
$etag = ($download.Headers.ETag.Tag)
$notModified = Send 'GET' "/api/v1/files/$($pdf.id)/content" $token $null $null @{ 'If-None-Match' = $etag }
Check ([int]$notModified.StatusCode -eq 304) 'If-None-Match -> 304'

# ---- Reddedilenler -----------------------------------------------------------------------------------------------
$exeBytes = New-Object byte[] 400; [Text.Encoding]::ASCII.GetBytes('MZ').CopyTo($exeBytes, 0)
$fake = Send 'POST' "/api/v1/files?recordType=account&recordId=$account" $token $null (Multipart 'invoice.pdf' $exeBytes)
Check ([int]$fake.StatusCode -eq 422 -and (Json $fake).code -eq 'file.content_mismatch') 'EXE named .pdf -> 422 file.content_mismatch'
$exe = Send 'POST' "/api/v1/files?recordType=account&recordId=$account" $token $null (Multipart 'setup.exe' $exeBytes)
Check ([int]$exe.StatusCode -eq 415 -and (Json $exe).code -eq 'file.type_not_allowed') '.exe -> 415 file.type_not_allowed'
Check ((ObjectCount "$tenantId/") -eq 2) 'rejected uploads left no object behind'

# ---- Yeniden adlandirma, silme -----------------------------------------------------------------------------------
$rename = Send 'PATCH' "/api/v1/files/$($png.id)" $token @{ name = 'company-logo.png' }
Check ([int]$rename.StatusCode -eq 204) 'rename -> 204'
$badRename = Send 'PATCH' "/api/v1/files/$($png.id)" $token @{ name = 'company-logo.pdf' }
Check ([int]$badRename.StatusCode -eq 400 -and (Json $badRename).code -eq 'file.extension_change_not_allowed') 'extension change -> 400'

# ---- Kota (402) --------------------------------------------------------------------------------------------------
$put = Send 'PUT' "/api/v1/platform/organizations/$tenantId/subscription" $platform @{ planCode = 'starter'; overrides = @{ maxStorageMb = 1 } }
Check ($put.StatusCode -eq 200) 'platform: starter plan + maxStorageMb override = 1'
$overLimit = @((Json $put).overLimit)
Check ($overLimit.Count -eq 0) 'subscription update: no over-limit entry while usage (305000 bytes) is below the 1 MiB quota'
$big = Send 'POST' "/api/v1/files?recordType=account&recordId=$account" $token $null (Multipart 'big.pdf' (Pdf 900000))
$bigProblem = Json $big
Check ([int]$big.StatusCode -eq 402 -and $bigProblem.code -eq 'file.quota_exceeded' -and $bigProblem.args.maxBytes -eq 1048576) 'quota: 402 file.quota_exceeded with maxBytes'
Check ((ObjectCount "$tenantId/") -eq 2) 'quota rejection wrote nothing to the object store'
$still = Send 'GET' "/api/v1/files/$($pdf.id)/content" $token
Check ($still.StatusCode -eq 200) 'download stays open while over quota'
$lift = Send 'PUT' "/api/v1/platform/organizations/$tenantId/subscription" $platform @{ planCode = 'internal' }
Check ($lift.StatusCode -eq 200) 'platform: plan lifted to internal'
$bigOk = Send 'POST' "/api/v1/files?recordType=account&recordId=$account" $token $null (Multipart 'big.pdf' (Pdf 900000))
Check ([int]$bigOk.StatusCode -eq 201) 'upload passes after the upgrade'
$usage = Json (Send 'GET' '/api/v1/files/usage' $token)
Check ($usage.usedBytes -eq (300000 + 5000 + 900000) -and $usage.fileCount -eq 3) "usage endpoint: $($usage.usedBytes) bytes / $($usage.fileCount) files"

# ---- Yumusak silme + Worker temizligi -----------------------------------------------------------------------------
$deleteId = (Json $bigOk).items[0].id
$del = Send 'DELETE' "/api/v1/files/$deleteId" $token
Check ([int]$del.StatusCode -eq 204) 'delete -> 204 (soft)'
Check ((Sql "SELECT state FROM files.attachments WHERE id = '$deleteId'") -eq 'deleted') 'row is soft deleted'
Check ((ObjectCount "$tenantId/") -eq 3) 'object still present during the retention window'
[void](Sql "UPDATE files.attachments SET deleted_at = now() - interval '8 days' WHERE id = '$deleteId'")
$deadline = (Get-Date).AddSeconds($WorkerWaitSeconds)
do { Start-Sleep -Seconds 5; $left = Sql "SELECT count(*) FROM files.attachments WHERE id = '$deleteId'" } while ($left -ne '0' -and (Get-Date) -lt $deadline)
Check ($left -eq '0') 'worker purge removed the expired soft-deleted row'
Check ((ObjectCount "$tenantId/") -eq 2) 'worker purge removed the object from the store'

# ---- Salt okunur kip ----------------------------------------------------------------------------------------------
$suspend = Send 'POST' "/api/v1/platform/organizations/$tenantId/suspend" $platform @{ reason = 'smoke'; mode = 'readOnly' }
Check ([int]$suspend.StatusCode -eq 204) 'platform: read-only suspension'
$roUpload = Send 'POST' "/api/v1/files?recordType=account&recordId=$account" $token $null (Multipart 'ro.pdf' (Pdf 1000))
Check ([int]$roUpload.StatusCode -eq 403 -and (Json $roUpload).code -eq 'tenant.suspended') 'read-only: upload -> 403 tenant.suspended'
$roDownload = Send 'GET' "/api/v1/files/$($pdf.id)/content" $token
Check ($roDownload.StatusCode -eq 200) 'read-only: download still works'
[void](Send 'POST' "/api/v1/platform/organizations/$tenantId/reactivate" $platform)

# ---- KVKK silme talebi -> Worker imhasi ---------------------------------------------------------------------------
$erase = Send 'POST' "/api/v1/platform/organizations/$tenantId/deletion-request" $platform @{ reason = 'smoke erasure'; retentionDays = 7 }
Check ($erase.StatusCode -eq 200) 'deletion request accepted'
[void](Sql "UPDATE platform.deletion_requests SET scheduled_for = now() - interval '1 hour' WHERE tenant_id = '$tenantId' AND status = 'scheduled'")
$deadline = (Get-Date).AddSeconds($WorkerWaitSeconds)
do { Start-Sleep -Seconds 5; $tenantRows = Sql "SELECT count(*) FROM identity.tenants WHERE id = '$tenantId'" } while ($tenantRows -ne '0' -and (Get-Date) -lt $deadline)
Check ($tenantRows -eq '0') 'worker erasure removed the tenant'
Check ((ObjectCount "$tenantId/") -eq 0) 'erasure left 0 objects under the tenant prefix'
Check ((Sql "SELECT count(*) FROM files.attachments WHERE tenant_id = '$tenantId'") -eq '0') 'erasure removed the file rows'
$tomb = Sql "SELECT status FROM platform.tenant_accounts WHERE tenant_id = '$tenantId'"
Check ($tomb -eq 'deleted') 'tombstone recorded (account status deleted)'

if ($script:failures -gt 0) { Write-Host "`n$($script:failures) check(s) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "`nAll checks passed." -ForegroundColor Green
