namespace Crm.Modules.Service.Domain.Cases;

/// <summary>Bir önceliğin SLA süreleri (duvar saati dakikası): ilk yanıt ve çözüm.</summary>
public readonly record struct SlaMinutes(int FirstResponseMinutes, int ResolutionMinutes);

/// <summary>Oluşturma / öncelik değişimi / yeniden açmada hesaplanıp saklanan hedef ve uyarı anları (UTC).</summary>
public readonly record struct SlaTargets(DateTime FirstResponseDueAt, DateTime FirstResponseWarnAt, DateTime DueAt, DateTime ResolutionWarnAt);

/// <summary>Okuma anındaki SLA değerlendirmesi (yanıt ve SQL süzgeci aynı tanımı kullanır).</summary>
public readonly record struct SlaEvaluation(bool FirstResponseBreached, bool ResolutionBreached, SlaState State)
{
    public bool IsBreached => FirstResponseBreached || ResolutionBreached;
}

/// <summary>
/// SLA hesabı (docs/plan/m6b-servis.md §3.4): saf, HTTP/DB'siz. Süreler duvar saati dakikasıdır (iş saati/tatil yok);
/// uyarı eşiği toplam sürenin %80'idir ve tam tamsayı saniyeyle (<c>dakika × 48 sn</c>) hesaplanır — kayan nokta yok.
/// Sınır: hedef anın kendisi (<c>now == dueAt</c>) ihlal değil, <c>atRisk</c>'tir; bir tik sonrası ihlaldir.
/// </summary>
public static class CaseSlaCalculator
{
    /// <summary>Uyarı eşiği: dakika başına 48 saniye = süresinin %80'i (sabit, ayarlanamaz).</summary>
    public const int WarnSecondsPerMinute = 48;

    public static SlaTargets Targets(DateTime createdAt, DateTime slaAnchorAt, SlaMinutes sla) => new(
        FirstResponseDue(createdAt, sla),
        FirstResponseWarn(createdAt, sla),
        ResolutionDue(slaAnchorAt, sla),
        ResolutionWarn(slaAnchorAt, sla));

    public static DateTime FirstResponseDue(DateTime createdAt, SlaMinutes sla) => createdAt.AddMinutes(sla.FirstResponseMinutes);

    public static DateTime FirstResponseWarn(DateTime createdAt, SlaMinutes sla) => createdAt.AddSeconds((long)sla.FirstResponseMinutes * WarnSecondsPerMinute);

    public static DateTime ResolutionDue(DateTime slaAnchorAt, SlaMinutes sla) => slaAnchorAt.AddMinutes(sla.ResolutionMinutes);

    public static DateTime ResolutionWarn(DateTime slaAnchorAt, SlaMinutes sla) => slaAnchorAt.AddSeconds((long)sla.ResolutionMinutes * WarnSecondsPerMinute);

    /// <summary>Durum aktif mi (<c>new | open | pending</c>).</summary>
    public static bool IsActive(CaseStatus status) => status is CaseStatus.New or CaseStatus.Open or CaseStatus.Pending;

    /// <summary>
    /// Okuma anı değerlendirmesi. <c>firstResponseBreached = (firstResponseAt ?? now) &gt; firstResponseDueAt</c>,
    /// <c>resolutionBreached = (resolvedAt ?? now) &gt; dueAt</c>; ihlal yoksa aktif talep uyarı eşiğine ulaştıysa <c>atRisk</c>
    /// (ilk yanıt eşiği yalnız ilk yanıt henüz gelmemişken), aksi <c>ok</c>. Çözülmüş/kapalı ve ihlalsiz talep hep <c>ok</c>.
    /// </summary>
    public static SlaEvaluation Evaluate(
        CaseStatus status,
        DateTime? firstResponseAt,
        DateTime? resolvedAt,
        DateTime firstResponseDueAt,
        DateTime firstResponseWarnAt,
        DateTime dueAt,
        DateTime resolutionWarnAt,
        DateTime now)
    {
        var firstResponseBreached = (firstResponseAt ?? now) > firstResponseDueAt;
        var resolutionBreached = (resolvedAt ?? now) > dueAt;
        if (firstResponseBreached || resolutionBreached)
        {
            return new SlaEvaluation(firstResponseBreached, resolutionBreached, SlaState.Breached);
        }

        var atRisk = IsActive(status) && ((firstResponseAt is null && now >= firstResponseWarnAt) || now >= resolutionWarnAt);
        return new SlaEvaluation(false, false, atRisk ? SlaState.AtRisk : SlaState.Ok);
    }
}

/// <summary>Durum makinesi (§3.2) ve yeniden açma kuralı sabitleri.</summary>
public static class CaseRules
{
    /// <summary>Kapalı talebin yeniden açılabildiği gün sayısı (<c>closedAt</c>'tan itibaren; <c>Service:ReopenWindowDays</c> ile ayarlanabilir).</summary>
    public const int ReopenWindowDays = 14;

    /// <summary>
    /// İzinli geçişler (satır = mevcut, sütun = hedef): new → {open, pending, resolved, closed}; open → {pending, resolved, closed};
    /// pending → {open, resolved, closed}; resolved → {open, closed}; closed → {open}. <c>new</c>'e dönüş yoktur.
    /// Aynı duruma geçiş burada değil, domain'de idempotent ele alınır.
    /// </summary>
    public static bool IsTransitionAllowed(CaseStatus from, CaseStatus to) => (from, to) switch
    {
        (CaseStatus.New, CaseStatus.Open or CaseStatus.Pending or CaseStatus.Resolved or CaseStatus.Closed) => true,
        (CaseStatus.Open, CaseStatus.Pending or CaseStatus.Resolved or CaseStatus.Closed) => true,
        (CaseStatus.Pending, CaseStatus.Open or CaseStatus.Resolved or CaseStatus.Closed) => true,
        (CaseStatus.Resolved, CaseStatus.Open or CaseStatus.Closed) => true,
        (CaseStatus.Closed, CaseStatus.Open) => true,
        _ => false,
    };
}
