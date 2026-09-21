using System.Text.Json;
using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Platform.Domain.Deletion;

/// <summary>
/// KVKK silme talebi: talep → bekleme (7–90 gün, varsayılan 30) → Worker işi kalıcı imha. Kiracı başına tek etkin talep
/// (kısmi benzersiz indeks: <c>scheduled | running | failed</c>). İptal yalnız <c>scheduled</c> ve bekleme süresinde. Küresel tablodur; imhadan sonra
/// kanıt olarak kalır (kişisel veri içermez).
/// </summary>
public sealed class DeletionRequest : Entity<Guid>
{
    private DeletionRequest()
    {
    }

    private DeletionRequest(Guid id) : base(id)
    {
    }

    public Guid TenantId { get; private set; }

    public Guid? RequestedByUserId { get; private set; }

    public DateTime RequestedAt { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public int RetentionDays { get; private set; }

    public DateTime ScheduledFor { get; private set; }

    public string Status { get; private set; } = DeletionStatuses.Scheduled;

    /// <summary>Talep anındaki saklanan hesap durumu (<c>active | suspended</c>); iptalde buna dönülür.</summary>
    public string PreviousStatus { get; private set; } = string.Empty;

    public DateTime? CancelledAt { get; private set; }

    public Guid? CancelledByUserId { get; private set; }

    public DateTime? StartedAt { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Tamamlanan imha adımları (yeniden çalıştırma tamamlananları atlar).</summary>
    public List<string> ErasedSteps { get; private set; } = [];

    /// <summary>İmha raporu JSON'u (adım → tablo → silinen satır sayısı; kişisel veri yok).</summary>
    public string? Report { get; private set; }

    public static DeletionRequest Create(Guid tenantId, Guid? requestedByUserId, string reason, int retentionDays, string previousStatus, DateTime nowUtc) =>
        new(Guid.CreateVersion7())
        {
            TenantId = Guard.NotDefault(tenantId),
            RequestedByUserId = requestedByUserId,
            RequestedAt = nowUtc,
            Reason = Guard.MaxLength(Guard.NotEmpty(reason), PlatformLimits.ReasonMaxLength),
            RetentionDays = Guard.InRange(retentionDays, PlatformLimits.MinRetentionDays, PlatformLimits.MaxRetentionDays),
            ScheduledFor = nowUtc.AddDays(retentionDays),
            PreviousStatus = Guard.NotEmpty(previousStatus),
        };

    /// <summary>İptal: yalnız <c>scheduled</c> ve <c>now &lt; scheduled_for</c>; aksi <c>platform.deletion_not_cancellable</c> (409).</summary>
    public Result Cancel(Guid? actorUserId, DateTime nowUtc)
    {
        if (Status != DeletionStatuses.Scheduled || nowUtc >= ScheduledFor)
        {
            return Error.Conflict(PlatformErrors.DeletionNotCancellable);
        }

        Status = DeletionStatuses.Cancelled;
        CancelledAt = nowUtc;
        CancelledByUserId = actorUserId;
        return Result.Success();
    }

    /// <summary>İş kuyruğunda mı: zamanı gelmiş <c>scheduled</c> ya da yeniden denenecek <c>running | failed</c>.</summary>
    public bool IsRunnable(DateTime nowUtc, int maxAttempts) =>
        (Status == DeletionStatuses.Scheduled && ScheduledFor <= nowUtc)
        || (Status is DeletionStatuses.Running or DeletionStatuses.Failed && Attempts < maxAttempts);

    /// <summary>PostgreSQL <c>xmin</c> eşzamanlılık belirteci (EF doldurur): iptal ↔ başlatma ↔ yeniden deneme yarışında kaybeden yazma <c>DbUpdateConcurrencyException</c> alır (M1).</summary>
    public uint Version { get; private set; }

    /// <summary>
    /// İşi başlatır (<c>running</c>). İptal edilmiş (<c>cancelled</c>) ya da tamamlanmış (<c>completed</c>) talep asla yeniden başlatılmaz
    /// (<c>platform.invalid_transition</c>, 409). Zaman kontrolü (<c>scheduled_for</c>) işin yeniden doğrulamasındadır.
    /// </summary>
    public Result Start(DateTime nowUtc)
    {
        if (Status is DeletionStatuses.Cancelled or DeletionStatuses.Completed)
        {
            return Error.Conflict(PlatformErrors.InvalidTransition, ("from", Status), ("to", DeletionStatuses.Running));
        }

        Status = DeletionStatuses.Running;
        StartedAt ??= nowUtc;
        return Result.Success();
    }

    /// <summary>
    /// Başarısız (<c>failed</c>) talebi elle yeniden denemeye alır (C-SEC2 L3): deneme sayacı sıfırlanır (işin <c>MaxAttempts</c> sınırı yeniden açılır), tamamlanan adımlar
    /// korunur, <c>last_error</c> bir sonraki başarıya kadar kalır. Yalnız <c>failed</c>; aksi <c>platform.deletion_not_retryable</c> (409).
    /// </summary>
    public Result Retry()
    {
        if (Status != DeletionStatuses.Failed)
        {
            return Error.Conflict(PlatformErrors.DeletionNotRetryable);
        }

        // Bekleme süresi <c>modified_date</c>'ten başlar (kayıt değiştiği için otomatik güncellenir).
        Attempts = 0;
        return Result.Success();
    }

    /// <summary>
    /// Sistem iptali (M1): imha işi ön koşulu (ör. kiracı sonradan korunan/işletim organizasyonu oldu) bozulduğunda talebi <c>cancelled</c> yapar (kullanıcı yok).
    /// Etkin (<c>scheduled | running | failed</c>) talep için geçerlidir; aksi <c>platform.invalid_transition</c> (409).
    /// </summary>
    public Result CancelBySystem(DateTime nowUtc, string reasonCode)
    {
        if (Status is not (DeletionStatuses.Scheduled or DeletionStatuses.Running or DeletionStatuses.Failed))
        {
            return Error.Conflict(PlatformErrors.InvalidTransition, ("from", Status), ("to", DeletionStatuses.Cancelled));
        }

        Status = DeletionStatuses.Cancelled;
        CancelledAt = nowUtc;
        CancelledByUserId = null;
        LastError = reasonCode.Length > PlatformLimits.LastErrorMaxLength ? reasonCode[..PlatformLimits.LastErrorMaxLength] : reasonCode;
        return Result.Success();
    }

    /// <summary>
    /// Kalıcı başarısızlık (M1): işin otomatik yeniden denemesini durdurur (<c>attempts = maxAttempts</c>, <c>failed</c>); yalnız operatörün <see cref="Retry"/> çağrısı yeniden açar.
    /// Ön koşulun kendiliğinden düzelmeyeceği durumlar içindir (kod <c>erasure.precondition_failed</c>).
    /// </summary>
    public void FailPermanently(string error, int maxAttempts)
    {
        Fail(error);
        Attempts = Math.Max(Attempts, maxAttempts);
    }

    /// <summary>Serbest metin gerekçeyi imhada yer tutucuyla değiştirir (L1; kişisel veri kalmaz). İdempotenttir.</summary>
    public void RedactReason() => Reason = PlatformLimits.RedactedReasonPlaceholder;

    public bool HasCompleted(string step) => ErasedSteps.Contains(step, StringComparer.Ordinal);

    /// <summary>Adımı tamamlandı işaretler ve silinen satır sayılarını rapora ekler (adım → tablo → sayı; kişisel veri yok).</summary>
    public void StepCompleted(string step, IReadOnlyDictionary<string, long> deletedRows)
    {
        ArgumentNullException.ThrowIfNull(deletedRows);
        if (HasCompleted(step))
        {
            return;
        }

        ErasedSteps.Add(step);
        var report = string.IsNullOrEmpty(Report)
            ? new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, IReadOnlyDictionary<string, long>>>(Report) ?? [];
        report[step] = deletedRows;
        Report = JsonSerializer.Serialize(report);
    }

    /// <summary>Adım hatası: <c>attempts++</c>, <c>last_error</c> (kısaltılmış, kişisel veri içermez), <c>failed</c>.</summary>
    public void Fail(string error)
    {
        Attempts++;
        Status = DeletionStatuses.Failed;
        LastError = error.Length > PlatformLimits.LastErrorMaxLength ? error[..PlatformLimits.LastErrorMaxLength] : error;
    }

    public void Complete(DateTime nowUtc)
    {
        Status = DeletionStatuses.Completed;
        CompletedAt = nowUtc;
        LastError = null;
    }
}
