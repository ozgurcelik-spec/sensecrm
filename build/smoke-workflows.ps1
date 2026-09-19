<#
.SYNOPSIS
  Canli duman testi (Milestone 4): gercek Conductor OSS konteynerine karsi iki workflow'u uctan uca dogrular.

.DESCRIPTION
  On kosullar (hepsi calisir durumda olmali):
    - postgres + Conductor:  POSTGRES_PORT=15433 docker compose -f infra/docker-compose.yml up -d conductor   (http://localhost:18090)
    - Migrator uygulanmis:   dotnet run --project src/Sense.Crm.Migrator
    - API:                   dotnet run --project src/Sense.Crm.Api    --no-launch-profile --urls http://localhost:5080   (Development)
    - Worker:                dotnet run --project src/Sense.Crm.Worker --no-launch-profile                                  (Development)
  Yeni bir organizasyon acar (her calistirmada benzersiz), roller/uyeler/kurallar kurar ve dogrular:
    1. Potansiyel atama: 3 lead -> round-robin atama + takip gorevi + yurutme "completed" (Conductor'da COMPLETED)
    2. Firsat onayi: buyuk firsat kazan -> onay talepleri -> karar -> not + diger onay iptal + yurutme "completed"
    3. Hata yolu: bos rol -> "no_assignee" (Conductor FAILED_WITH_TERMINAL_ERROR) -> uye ekle -> yeniden dene -> tamamlanir
    4. Sonlandirma: bekleyen onayli yurutmeyi terminate et -> "terminated", onaylar iptal
  Yalnizca ASCII metin kullanir (Git Bash curl / konsol kodlamasi sorunlarini onlemek icin). Cikis kodu 0 = hepsi gecti.
#>
[CmdletBinding()]
param(
    [string]$Api = 'http://localhost:5080',
    [string]$Conductor = 'http://localhost:18090',
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$Base = "$Api/api/v1"
$script:Failures = @()

function Pass([string]$what) { Write-Host "  PASS  $what" -ForegroundColor Green }
function Fail([string]$what) { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:Failures += $what }
function Check([bool]$ok, [string]$what) { if ($ok) { Pass $what } else { Fail $what } }

function Call([string]$Method, [string]$Url, $Body = $null, [string]$Token = $null, [int[]]$Expect = @(200, 201, 204)) {
    $headers = @{}
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }
    $req = @{ Method = $Method; Uri = $Url; Headers = $headers; UseBasicParsing = $true }
    if ($null -ne $Body) { $req['Body'] = ($Body | ConvertTo-Json -Depth 10 -Compress); $req['ContentType'] = 'application/json' }
    $status = 0
    $content = $null
    try {
        $resp = Invoke-WebRequest @req
        $status = [int]$resp.StatusCode
        $content = $resp.Content
    }
    catch [System.Net.WebException] {
        # Windows PowerShell 5.1: HTTP hata durumlari istisna olarak gelir; durum kodu ve govde yanittan okunur.
        $r = $_.Exception.Response
        if ($null -eq $r) { throw }
        $status = [int]$r.StatusCode
        $content = (New-Object System.IO.StreamReader($r.GetResponseStream())).ReadToEnd()
    }
    if ($Expect -notcontains $status) { throw "$Method $Url -> $($status): $content" }
    if ($content) { return ($content | ConvertFrom-Json) }
    return $null
}

function WaitFor([string]$What, [scriptblock]$Condition) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 700
    }
    throw "Timed out waiting for: $What"
}

function NewMember([string]$Token, [string]$RoleId, [string]$Name) {
    $email = "$($Name.ToLower())-$([guid]::NewGuid().ToString('N').Substring(0, 8))@example.com"
    $member = Call POST "$Base/organization/members" @{ email = $email; displayName = $Name; password = 'Sifre.12345'; roleId = $RoleId } $Token
    $auth = Call POST "$Base/auth/login" @{ email = $email; password = 'Sifre.12345' }
    return [pscustomobject]@{ UserId = $member.userId; Token = $auth.accessToken; Name = $Name }
}

Write-Host "== Preflight" -ForegroundColor Cyan
$health = Invoke-RestMethod "$Conductor/health"
Check ($health.healthy -eq $true) "Conductor healthy ($Conductor)"
Check ((Invoke-WebRequest "$Api/health/live" -UseBasicParsing).StatusCode -eq 200) "API live ($Api)"

Write-Host "== Setup organization, roles, members, rules" -ForegroundColor Cyan
$suffix = [guid]::NewGuid().ToString('N').Substring(0, 8)
$auth = Call POST "$Base/auth/signup" @{ organizationName = "Smoke $suffix"; displayName = 'Smoke Admin'; email = "admin-$suffix@example.com"; password = 'Sifre.12345'; locale = 'en' }
$admin = $auth.accessToken
$me = Call GET "$Base/me" $null $admin
Check ($me.permissions -contains 'org.workflows.manage' -and $me.permissions -contains 'crm.approvals.decide') 'Administrator has org.workflows.manage + crm.approvals.decide'

$repsRole = Call POST "$Base/organization/roles" @{ name = 'Reps'; permissions = @('crm.leads.read', 'crm.activities.read') } $admin
$approversRole = Call POST "$Base/organization/roles" @{ name = 'Approvers'; permissions = @('crm.approvals.decide', 'crm.deals.read') } $admin
$emptyRole = Call POST "$Base/organization/roles" @{ name = 'Empty'; permissions = @('crm.leads.read') } $admin
$rep1 = NewMember $admin $repsRole.id 'Rep1'
$rep2 = NewMember $admin $repsRole.id 'Rep2'
$app1 = NewMember $admin $approversRole.id 'Approver1'
$app2 = NewMember $admin $approversRole.id 'Approver2'

$leadRule = Call POST "$Base/workflows/rules" @{ name = 'Round robin'; kind = 'leadAssignment'; params = @{ assigneeRoleId = $repsRole.id; followUpHours = 24 } } $admin
$dealRule = Call POST "$Base/workflows/rules" @{ name = 'Big deals'; kind = 'dealApproval'; params = @{ minAmount = 50000; approverRoleId = $approversRole.id } } $admin
Check ($leadRule.isEnabled -and $dealRule.isEnabled) 'both rules created and enabled'

Write-Host "== 1. Lead assignment (round-robin) against real Conductor" -ForegroundColor Cyan
$leadIds = @()
foreach ($n in 1..3) {
    $lead = Call POST "$Base/leads" @{ firstName = 'Ali'; lastName = "Smoke$n"; company = 'Acme'; source = 'web' } $admin
    $leadIds += $lead.id
    WaitFor "lead $n reassigned" { (Call GET "$Base/leads/$($lead.id)" $null $admin).ownerUserId -ne $me.user.id } | Out-Null
}
$owners = $leadIds | ForEach-Object { (Call GET "$Base/leads/$_" $null $admin).ownerUserId }
Check ((@($owners | Sort-Object -Unique).Count) -eq 2 -and @($owners | Where-Object { $_ -eq $rep1.UserId -or $_ -eq $rep2.UserId }).Count -eq 3) "3 leads distributed across the 2 reps: $($owners -join ', ')"
Check ($owners[2] -eq $owners[0]) 'third lead goes to the rep assigned longest ago'
foreach ($i in 0..2) {
    $task = (WaitFor "follow-up task for lead $i" { $t = (Call GET "$Base/activities?relatedType=lead&relatedId=$($leadIds[$i])" $null $admin).items; if (@($t).Count -ge 1) { $t } })[0]
    Check ($task.subject -eq "New lead: Ali Smoke$($i + 1)" -and $task.assignedUserId -eq $owners[$i]) "follow-up task for lead $($i + 1): '$($task.subject)' assigned to owner"
}
$leadExecs = WaitFor 'lead executions completed' { $e = (Call GET "$Base/workflows/executions?subjectType=lead" $null $admin).items; if (@($e).Count -eq 3 -and @($e | Where-Object status -ne 'completed').Count -eq 0) { $e } }
Check (@($leadExecs).Count -eq 3) 'all 3 lead executions reached "completed" (status synced from Conductor)'
$detail = Call GET "$Base/workflows/executions/$($leadExecs[0].id)" $null $admin
Check (@($detail.steps | Where-Object status -eq 'COMPLETED').Count -eq 2) "execution detail lists 2 COMPLETED steps from Conductor: $($detail.steps.name -join ', ')"

Write-Host "== 2. Deal approval against real Conductor" -ForegroundColor Cyan
$account = Call POST "$Base/accounts" @{ name = "Smoke Account $suffix" } $admin
$deal = Call POST "$Base/deals" @{ name = 'Smoke Big Deal'; accountId = $account.id; amount = 120000 } $admin
$pipeline = (Call GET "$Base/pipelines" $null $admin) | Where-Object isDefault | Select-Object -First 1
$won = $pipeline.stages | Where-Object kind -eq 'won' | Select-Object -First 1
Call POST "$Base/deals/$($deal.id)/stage" @{ stageId = $won.id } $admin | Out-Null
$pending = WaitFor 'approvals created for both approvers' { $a = (Call GET "$Base/approvals?mine=false&status=pending" $null $admin).items; if (@($a).Count -eq 2) { $a } }
Check (@($pending).Count -eq 2 -and @($pending | Where-Object title -eq 'Deal approval: Smoke Big Deal').Count -eq 2) 'two pending approvals "Deal approval: Smoke Big Deal"'
$dealExec = (Call GET "$Base/workflows/executions?subjectType=deal&subjectId=$($deal.id)" $null $admin).items[0]
Check ($dealExec.status -eq 'running') 'deal execution is running (waiting for the decision)'
WaitFor 'HUMAN task in progress' { $d = Call GET "$Base/workflows/executions/$($dealExec.id)" $null $admin; if (@($d.steps | Where-Object { $_.name -eq 'wait_for_decision' -and $_.status -eq 'IN_PROGRESS' }).Count -eq 1) { $d } } | Out-Null
Pass 'Conductor HUMAN task wait_for_decision is IN_PROGRESS'
Check ((Call GET "$Base/approvals/summary" $null $app1.Token).pendingCount -eq 1) 'approver badge shows 1 pending'
$myApproval = (Call GET "$Base/approvals?mine=true&status=pending" $null $app1.Token).items[0]
Call POST "$Base/approvals/$($myApproval.id)/decision" @{ decision = 'reject' } $app1.Token @(400) | Out-Null
Pass 'reject without comment -> 400 validation'
Call POST "$Base/approvals/$($myApproval.id)/decision" @{ decision = 'approve'; comment = 'Looks good' } $app1.Token | Out-Null
$dealDone = WaitFor 'deal execution completed' { $e = Call GET "$Base/workflows/executions/$($dealExec.id)" $null $admin; if ($e.status -eq 'completed') { $e } }
Check ($dealDone.status -eq 'completed') 'deal execution reached "completed" after the decision'
Check ((($dealDone.approvals | ForEach-Object status) | Sort-Object) -join ',' -eq 'approved,cancelled') 'first decision approved, the other approval cancelled'
$note = ((Call GET "$Base/activities?relatedType=deal&relatedId=$($deal.id)" $null $admin).items | Where-Object type -eq 'note')[0]
Check ($note.subject -eq 'Approval: approved (Looks good)') "decision recorded as a note on the deal: '$($note.subject)'"
Call POST "$Base/approvals/$($pending | Where-Object approverUserId -eq $app2.UserId | ForEach-Object id)/decision" @{ decision = 'approve' } $app2.Token @(409) | Out-Null
Pass 'late decision by the second approver -> 409 approval.already_decided'
Check ((Call GET "$Base/deals/$($deal.id)" $null $admin).stageKind -eq 'won') 'deal stage unchanged (informational approval)'

Write-Host "== 3. Failure path: empty role -> no_assignee -> add member -> retry" -ForegroundColor Cyan
Call PUT "$Base/workflows/rules/$($leadRule.id)" @{ name = 'Round robin'; kind = 'leadAssignment'; params = @{ assigneeRoleId = $emptyRole.id; followUpHours = 24 } } $admin | Out-Null
$orphan = Call POST "$Base/leads" @{ firstName = 'Ali'; lastName = 'Orphan'; company = 'Acme' } $admin
$failed = WaitFor 'orphan execution failed' { $e = (Call GET "$Base/workflows/executions?status=failed&subjectId=$($orphan.id)" $null $admin).items; if (@($e).Count -ge 1) { $e[0] } }
Check ($failed.error -eq 'no_assignee') 'execution failed with error "no_assignee"'
Check ((Call GET "$Base/leads/$($orphan.id)" $null $admin).ownerUserId -eq $me.user.id) 'lead owner unchanged'
$failDetail = Call GET "$Base/workflows/executions/$($failed.id)" $null $admin
Check ($failDetail.steps[0].status -eq 'FAILED_WITH_TERMINAL_ERROR') 'Conductor task status is FAILED_WITH_TERMINAL_ERROR'
$late = NewMember $admin $emptyRole.id 'LateRep'
Call POST "$Base/workflows/executions/$($failed.id)/retry" $null $admin | Out-Null
WaitFor 'retry assigned the lead' { (Call GET "$Base/leads/$($orphan.id)" $null $admin).ownerUserId -eq $late.UserId } | Out-Null
$retried = WaitFor 'retry execution completed' { $e = (Call GET "$Base/workflows/executions?subjectId=$($orphan.id)" $null $admin).items; if (@($e | Where-Object status -eq 'completed').Count -eq 1) { $e } }
Check (@($retried).Count -eq 2) 'retry opened a new execution (2 attempts listed, newest completed)'

Write-Host "== 4. Terminate a waiting execution" -ForegroundColor Cyan
$deal2 = Call POST "$Base/deals" @{ name = 'Smoke Deal To Terminate'; accountId = $account.id; amount = 90000 } $admin
Call POST "$Base/deals/$($deal2.id)/stage" @{ stageId = $won.id } $admin | Out-Null
$exec2 = WaitFor 'second deal execution waiting' { $e = (Call GET "$Base/workflows/executions?subjectId=$($deal2.id)" $null $admin).items; if (@($e).Count -eq 1 -and @((Call GET "$Base/approvals?mine=false&status=pending" $null $admin).items).Count -ge 2) { $e[0] } }
Call POST "$Base/workflows/executions/$($exec2.id)/terminate" $null $admin | Out-Null
$term = Call GET "$Base/workflows/executions/$($exec2.id)" $null $admin
Check ($term.status -eq 'terminated' -and (@($term.approvals | Where-Object status -ne 'cancelled').Count -eq 0)) 'execution terminated, pending approvals cancelled'
Call POST "$Base/workflows/executions/$($exec2.id)/terminate" $null $admin @(409) | Out-Null
Pass 'terminate again -> 409 workflow.not_running'

Write-Host ''
if ($script:Failures.Count -eq 0) {
    Write-Host "SMOKE PASSED (organization 'Smoke $suffix')" -ForegroundColor Green
    exit 0
}
Write-Host "SMOKE FAILED: $($script:Failures.Count) check(s)" -ForegroundColor Red
$script:Failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
