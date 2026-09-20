using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Sense.Crm.Shared.Contracts.Observability;

/// <summary>
/// Uygulamanın iş/işletim metrikleri (C-OPS1, K20). Tek <see cref="Meter"/> (<c>Sense.Crm</c>) ve yalnız <b>düşük kardinaliteli</b> etiketler:
/// <c>module</c>, <c>status</c>, <c>reason</c>, <c>outcome</c>, <c>task_type</c>, <c>plan</c>, <c>step</c>. Kiracı/kullanıcı/kayıt kimliği, e-posta, IP gibi
/// sınırsız ya da kişisel değerler <b>asla</b> etiket olmaz (Prometheus'ta seri patlaması + KVKK); kaydedici metotlar bu yüzden serbest etiket
/// almaz, yalnız sabit değer kümelerinden değer alır. <see cref="ForbiddenLabelNames"/> bir test kapısında dışa aktarılan çıktıyı denetler.
/// Meter ihraççı yokken (metrik uç noktası kapalı) hemen hemen sıfır maliyetlidir.
/// </summary>
public static class CrmMetrics
{
    public const string MeterName = "Sense.Crm";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>Etiket adları (sabit küme).</summary>
    public static class Tag
    {
        public const string Module = "module";
        public const string Status = "status";
        public const string Reason = "reason";
        public const string Outcome = "outcome";
        public const string TaskType = "task_type";
        public const string Plan = "plan";
        public const string Step = "step";
        /// <summary>Arka plan iş adı (<c>job</c> Prometheus'un hedef etiketiyle çakışacağı için <c>background_job</c>).</summary>
        public const string BackgroundJob = "background_job";
    }

    /// <summary>Hiçbir zaman metrik etiketi olamayacak adlar (sınırsız kardinalite / kişisel veri). Test kapısı kullanır.</summary>
    public static IReadOnlyList<string> ForbiddenLabelNames { get; } =
    [
        "tenant_id", "tenantid", "tenant", "user_id", "userid", "user", "email", "ip", "client_ip", "organization_id", "org_id",
        "session_id", "execution_id", "request_id", "correlation_id", "trace_id", "member_id", "record_id", "token",
    ];

    // --- Kimlik --------------------------------------------------------------------------------------------------------

    private static readonly Counter<long> Logins = Meter.CreateCounter<long>(
        "crm.auth.logins", description: "Login attempts by outcome (success | invalid_credentials | rate_limited | locked_out | inactive | no_organization | suspended).");

    private static readonly Counter<long> Lockouts = Meter.CreateCounter<long>(
        "crm.auth.lockouts", description: "Accounts that hit the failed-attempt threshold and were locked.");

    private static readonly Counter<long> RefreshReuse = Meter.CreateCounter<long>(
        "crm.auth.refresh_reuse_detected", description: "Revoked refresh tokens presented again outside the grace window (token theft signal); the token family is revoked.");

    private static readonly Counter<long> RefreshRejected = Meter.CreateCounter<long>(
        "crm.auth.refresh_rejected", description: "Refresh requests rejected (unknown | expired | reuse | user_inactive).");

    // --- Plan / limit zorlaması ------------------------------------------------------------------------------------------

    private static readonly Counter<long> EntitlementRejections = Meter.CreateCounter<long>(
        "crm.entitlement.rejections", description: "Requests rejected by plan/lifecycle enforcement, by reason (suspended status | module_disabled | limit_exceeded), module and plan code.");

    // --- Outbox ----------------------------------------------------------------------------------------------------------

    private static readonly Counter<long> OutboxMessages = Meter.CreateCounter<long>(
        "crm.outbox.messages", description: "Outbox messages handled by outcome (dispatched | retry | dead), per module.");

    private static readonly Histogram<double> OutboxLag = Meter.CreateHistogram(
        "crm.outbox.dispatch_lag", unit: "s", description: "Seconds between an outbox message being written and dispatched, per module.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.05, 0.25, 1, 5, 15, 60, 300, 900, 3600] });

    private static readonly Counter<long> OutboxPollFailures = Meter.CreateCounter<long>(
        "crm.outbox.poll_failures", description: "Outbox polling rounds that failed as a whole (database down, not migrated), per module.");

    // --- Conductor / workflow ---------------------------------------------------------------------------------------------

    private static readonly Counter<long> ConductorPolls = Meter.CreateCounter<long>(
        "crm.conductor.polls", description: "Conductor poll rounds by outcome (tasks | empty | error).");

    private static readonly Counter<long> ConductorTasks = Meter.CreateCounter<long>(
        "crm.conductor.tasks", description: "Conductor tasks handled by task type and outcome (completed | failed | failed_terminal | blocked | report_failed).");

    private static readonly Histogram<double> ConductorTaskDuration = Meter.CreateHistogram(
        "crm.conductor.task_duration", unit: "s", description: "Task handler duration by task type.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.01, 0.05, 0.25, 1, 5, 30] });

    private static readonly Counter<long> WorkflowExecutions = Meter.CreateCounter<long>(
        "crm.workflow.executions", description: "Workflow executions that reached a terminal status (completed | failed | terminated).");

    // --- Kiracı yaşam döngüsü ---------------------------------------------------------------------------------------------

    private static readonly Counter<long> DeletionRuns = Meter.CreateCounter<long>(
        "crm.tenant_deletion.runs", description: "Tenant erasure requests processed by outcome (completed | failed).");

    private static readonly Counter<long> DeletionSteps = Meter.CreateCounter<long>(
        "crm.tenant_deletion.steps", description: "Erasure steps by step name (module) and outcome (completed | failed).");

    private static readonly Counter<long> DeletionRows = Meter.CreateCounter<long>(
        "crm.tenant_deletion.rows_deleted", description: "Rows permanently erased by step name (module).");

    private static readonly Counter<long> UsageSnapshots = Meter.CreateCounter<long>(
        "crm.usage_snapshot.tenants", description: "Tenants processed by the daily usage snapshot job by outcome (written | failed).");

    private static readonly Counter<long> BackgroundFailures = Meter.CreateCounter<long>(
        "crm.background.failures", description: "Background job rounds that failed as a whole, by job name.");

    // --- Ön ısıtma (sıfır değerli seriler) -----------------------------------------------------------------------------------

    /// <summary>
    /// Kimlik sayaçlarını <b>0 değeriyle</b> oluşturur. Prometheus'ta bir sayaç ilk olayda birden çıkıyorsa <c>increase()</c>/<c>rate()</c> ilk artışı
    /// göremez (önceki örnek yok): "refresh yeniden kullanımı" gibi nadir güvenlik olaylarının ilk oluşumu alarm üretemezdi. İhraççı (MeterProvider) kurulduktan
    /// <b>sonra</b> çağrılmalıdır (dinleyici yokken yapılan Add(0) kaybolur).
    /// </summary>
    public static void Prime()
    {
        foreach (var outcome in LoginOutcomeValues)
        {
            Logins.Add(0, new KeyValuePair<string, object?>(Tag.Outcome, outcome));
        }

        Lockouts.Add(0);
        RefreshReuse.Add(0);
        foreach (var reason in RefreshRejectReasons)
        {
            RefreshRejected.Add(0, new KeyValuePair<string, object?>(Tag.Reason, reason));
        }
    }

    /// <summary>Worker'ın arka plan sayaçları (Conductor yoklama, workflow durumları, silme hattı, kullanım işi); Worker örnekleyicisi çağırır.</summary>
    public static void PrimeBackground()
    {
        foreach (var outcome in new[] { "tasks", "empty", "error" })
        {
            ConductorPolls.Add(0, new KeyValuePair<string, object?>(Tag.Outcome, outcome));
        }

        foreach (var status in new[] { "completed", "failed", "terminated" })
        {
            WorkflowExecutions.Add(0, new KeyValuePair<string, object?>(Tag.Status, status));
        }

        foreach (var outcome in new[] { "completed", "failed" })
        {
            DeletionRuns.Add(0, new KeyValuePair<string, object?>(Tag.Outcome, outcome));
        }

        foreach (var outcome in new[] { "written", "failed" })
        {
            UsageSnapshots.Add(0, new KeyValuePair<string, object?>(Tag.Outcome, outcome));
        }
    }

    /// <summary>Bir modülün outbox sayaçlarını sıfırla oluşturur (Worker örnekleyicisi modülleri bilir).</summary>
    public static void PrimeModule(string module)
    {
        foreach (var outcome in new[] { "dispatched", "retry", "dead" })
        {
            OutboxMessages.Add(0, new KeyValuePair<string, object?>(Tag.Module, module), new KeyValuePair<string, object?>(Tag.Outcome, outcome));
        }

        OutboxPollFailures.Add(0, new KeyValuePair<string, object?>(Tag.Module, module));
    }

    /// <summary>Conductor görev türleri için sonuç sayaçlarını sıfırla oluşturur.</summary>
    public static void PrimeTaskType(string taskType)
    {
        foreach (var outcome in new[] { "completed", "failed", "failed_terminal", "blocked", "report_failed" })
        {
            ConductorTasks.Add(0, new KeyValuePair<string, object?>(Tag.TaskType, taskType), new KeyValuePair<string, object?>(Tag.Outcome, outcome));
        }
    }

    /// <summary>Arka plan işi hata sayacını sıfırla oluşturur.</summary>
    public static void PrimeBackgroundJob(string job) => BackgroundFailures.Add(0, new KeyValuePair<string, object?>(Tag.BackgroundJob, job));

    private static readonly string[] LoginOutcomeValues = ["success", "invalid_credentials", "rate_limited", "locked_out", "inactive", "no_organization", "suspended"];

    private static readonly string[] RefreshRejectReasons = ["unknown", "expired", "reuse", "concurrent", "user_inactive"];

    // --- Kaydediciler ----------------------------------------------------------------------------------------------------

    public static void LoginOutcome(string outcome) => Logins.Add(1, new KeyValuePair<string, object?>(Tag.Outcome, outcome));

    public static void LockoutStarted() => Lockouts.Add(1);

    public static void RefreshReuseDetected() => RefreshReuse.Add(1);

    public static void RefreshRejectedFor(string reason) => RefreshRejected.Add(1, new KeyValuePair<string, object?>(Tag.Reason, reason));

    public static void EntitlementRejected(string reason, string? module, string? plan) =>
        EntitlementRejections.Add(
            1,
            new KeyValuePair<string, object?>(Tag.Reason, reason),
            new KeyValuePair<string, object?>(Tag.Module, module ?? "none"),
            new KeyValuePair<string, object?>(Tag.Plan, plan ?? "unknown"));

    public static void OutboxHandled(string module, string outcome, int count = 1) =>
        OutboxMessages.Add(count, new KeyValuePair<string, object?>(Tag.Module, module), new KeyValuePair<string, object?>(Tag.Outcome, outcome));

    public static void OutboxDispatchLag(string module, TimeSpan lag) =>
        OutboxLag.Record(Math.Max(lag.TotalSeconds, 0), new KeyValuePair<string, object?>(Tag.Module, module));

    public static void OutboxPollFailed(string module) => OutboxPollFailures.Add(1, new KeyValuePair<string, object?>(Tag.Module, module));

    public static void ConductorPoll(string outcome) => ConductorPolls.Add(1, new KeyValuePair<string, object?>(Tag.Outcome, outcome));

    public static void ConductorTaskHandled(string taskType, string outcome, TimeSpan duration)
    {
        ConductorTasks.Add(1, new KeyValuePair<string, object?>(Tag.TaskType, taskType), new KeyValuePair<string, object?>(Tag.Outcome, outcome));
        ConductorTaskDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>(Tag.TaskType, taskType));
    }

    public static void WorkflowExecutionFinished(string status) => WorkflowExecutions.Add(1, new KeyValuePair<string, object?>(Tag.Status, status));

    public static void DeletionRunFinished(string outcome) => DeletionRuns.Add(1, new KeyValuePair<string, object?>(Tag.Outcome, outcome));

    public static void DeletionStepFinished(string step, string outcome, long rows = 0)
    {
        DeletionSteps.Add(1, new KeyValuePair<string, object?>(Tag.Step, step), new KeyValuePair<string, object?>(Tag.Outcome, outcome));
        if (rows > 0)
        {
            DeletionRows.Add(rows, new KeyValuePair<string, object?>(Tag.Step, step));
        }
    }

    public static void UsageSnapshotTenants(string outcome, int count = 1)
    {
        if (count > 0)
        {
            UsageSnapshots.Add(count, new KeyValuePair<string, object?>(Tag.Outcome, outcome));
        }
    }

    public static void BackgroundFailed(string job) => BackgroundFailures.Add(1, new KeyValuePair<string, object?>(Tag.BackgroundJob, job));

    /// <summary>Ölçüm için basit süre sayacı başlangıcı.</summary>
    public static long StartTimer() => Stopwatch.GetTimestamp();

    public static TimeSpan Elapsed(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp);

    // --- Örneklenen (gauge) değerler ----------------------------------------------------------------------------------------

    /// <summary>
    /// Worker'daki örnekleyicilerin yazdığı son değerler (modül/durum → sayı); ObservableGauge'lar okur. Kilit gerektirmez (liste referansı
    /// değiştirilir). Api sürecinde örnekleyici çalışmadığı için bu seriler yalnız Worker'dan gelir.
    /// </summary>
    public static class Gauges
    {
        private static volatile IReadOnlyList<Measurement<long>> outboxPending = [];
        private static volatile IReadOnlyList<Measurement<long>> outboxDead = [];
        private static volatile IReadOnlyList<Measurement<double>> outboxOldestPendingAge = [];
        private static volatile IReadOnlyList<Measurement<long>> deletionRequests = [];
        private static volatile IReadOnlyList<Measurement<double>> deletionOldestDueAge = [];
        private static volatile IReadOnlyList<Measurement<long>> workflowsRunning = [];

        static Gauges()
        {
            Meter.CreateObservableGauge("crm.outbox.pending", () => outboxPending, description: "Outbox messages waiting to be dispatched (not dead), per module.");
            Meter.CreateObservableGauge("crm.outbox.dead", () => outboxDead, description: "Dead-lettered outbox messages (gave up after MaxAttempts; needs an operator), per module.");
            Meter.CreateObservableGauge("crm.outbox.oldest_pending_age", () => outboxOldestPendingAge, unit: "s", description: "Age of the oldest pending outbox message, per module (0 when none).");
            Meter.CreateObservableGauge("crm.tenant_deletion.requests", () => deletionRequests, description: "Deletion requests by status (scheduled | running | failed).");
            Meter.CreateObservableGauge("crm.tenant_deletion.oldest_due_age", () => deletionOldestDueAge, unit: "s", description: "Seconds the oldest actionable deletion request has been overdue (0 when none).");
            Meter.CreateObservableGauge("crm.workflow.executions_running", () => workflowsRunning, description: "Workflow executions currently in running status.");
        }

        /// <summary>Statik yapıcıyı (gauge kaydını) tetikler.</summary>
        public static void Ensure()
        {
        }

        public static void SetOutbox(IEnumerable<(string Module, long Pending, long Dead, double OldestPendingAgeSeconds)> values)
        {
            var list = values.ToList();
            outboxPending = [.. list.Select(v => new Measurement<long>(v.Pending, new KeyValuePair<string, object?>(Tag.Module, v.Module)))];
            outboxDead = [.. list.Select(v => new Measurement<long>(v.Dead, new KeyValuePair<string, object?>(Tag.Module, v.Module)))];
            outboxOldestPendingAge = [.. list.Select(v => new Measurement<double>(v.OldestPendingAgeSeconds, new KeyValuePair<string, object?>(Tag.Module, v.Module)))];
        }

        public static void SetDeletion(IEnumerable<(string Status, long Count)> byStatus, double oldestDueAgeSeconds)
        {
            deletionRequests = [.. byStatus.Select(v => new Measurement<long>(v.Count, new KeyValuePair<string, object?>(Tag.Status, v.Status)))];
            deletionOldestDueAge = [new Measurement<double>(oldestDueAgeSeconds)];
        }

        public static void SetWorkflowsRunning(long running) => workflowsRunning = [new Measurement<long>(running)];
    }
}
