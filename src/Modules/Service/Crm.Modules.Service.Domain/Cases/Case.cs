using Crm.Shared.Kernel;
using Crm.Shared.Kernel.Domain;
using Crm.Shared.Kernel.Results;

namespace Crm.Modules.Service.Domain.Cases;

/// <summary>Bir durum/öncelik/atama değişikliğinin ürettiği zaman çizelgesi olayları; <see cref="ResolvedNow"/> <c>resolvedAt</c> yazıldıysa true (→ <c>CaseResolved</c>).</summary>
public sealed record CaseChange(IReadOnlyList<CaseEvent> Events, bool ResolvedNow = false)
{
    public static CaseChange None { get; } = new([]);
}

/// <summary>Yeni talep ve <c>created</c> olayı.</summary>
public sealed record CaseCreation(Case Case, CaseEvent Event);

/// <summary>Yeni yorum ve (ilk herkese açık yorumda) <c>new → open</c> olayı.</summary>
public sealed record CaseCommentOutcome(CaseComment Comment, IReadOnlyList<CaseEvent> Events);

/// <summary>
/// Talep (Zoho "Case"): firma/kişiye yumuşak bağlı müşteri sorunu; durum makinesi, atama, SLA süreleri ve yorumlar. Yumuşak silinir.
/// Zamanlar UTC'dir. Durum makinesi ve SLA hesabı saf (<see cref="CaseRules"/>, <see cref="CaseSlaCalculator"/>);
/// <c>xmin</c> eşzamanlılık belirteci kalıcılık katmanındadır (çakışma → <c>general.concurrency_conflict</c>).
/// </summary>
public sealed class Case : TenantAggregateRoot<Guid>, IAuditLogged, ISoftDelete
{
    private Case()
    {
    }

    private Case(Guid id, Guid tenantId, string number, string subject) : base(id, tenantId)
    {
        Number = number;
        Subject = subject;
    }

    /// <summary>Kiracıda benzersiz, değişmez numara (<c>C-2026-0001</c>).</summary>
    public string Number { get; private set; } = string.Empty;

    public string Subject { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public Guid? AccountId { get; private set; }

    public Guid? ContactId { get; private set; }

    public CaseStatus Status { get; private set; }

    public CasePriority Priority { get; private set; } = CasePriority.Normal;

    public CaseChannel Channel { get; private set; } = CaseChannel.Other;

    public Guid? AssignedUserId { get; private set; }

    public string? ResolutionNote { get; private set; }

    public DateTime? FirstResponseAt { get; private set; }

    public DateTime? ResolvedAt { get; private set; }

    public DateTime? ClosedAt { get; private set; }

    public int ReopenCount { get; private set; }

    /// <summary>İlk yanıt hedefi: <c>createdAt + firstResponseMinutes</c>.</summary>
    public DateTime FirstResponseDueAt { get; private set; }

    /// <summary>Çözüm hedefi: <c>slaAnchorAt + resolutionMinutes</c>.</summary>
    public DateTime DueAt { get; private set; }

    /// <summary>Çözüm SLA'sının başlangıcı: oluşturma anı, yeniden açmada açılış anı.</summary>
    public DateTime SlaAnchorAt { get; private set; }

    public DateTime FirstResponseWarnAt { get; private set; }

    public DateTime ResolutionWarnAt { get; private set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    public bool IsActive => CaseSlaCalculator.IsActive(Status);

    /// <summary>
    /// Yeni talep: durum her zaman <c>new</c>; SLA hedefleri verilen süreler ve <paramref name="nowUtc"/> ile hesaplanıp saklanır
    /// (politika sonradan değişirse değişmez). <paramref name="assignedUserId"/> verilmezse atanmamış (temsilci kuyruğu).
    /// </summary>
    public static CaseCreation Create(
        Guid tenantId,
        string number,
        string subject,
        string? description,
        Guid? accountId,
        Guid? contactId,
        CasePriority priority,
        CaseChannel channel,
        Guid? assignedUserId,
        SlaMinutes sla,
        Guid? actorUserId,
        DateTime nowUtc)
    {
        var created = new Case(
            Guid.CreateVersion7(),
            Guard.NotDefault(tenantId),
            Guard.MaxLength(Guard.NotEmpty(number), ServiceLimits.NumberMaxLength),
            Guard.MaxLength(Guard.NotEmpty(subject), ServiceLimits.SubjectMaxLength))
        {
            Description = Clean(description, ServiceLimits.DescriptionMaxLength),
            AccountId = OptionalId(accountId),
            ContactId = OptionalId(contactId),
            Status = CaseStatus.New,
            Priority = priority,
            Channel = channel,
            AssignedUserId = OptionalId(assignedUserId),
            SlaAnchorAt = nowUtc,
        };
        created.ApplyTargets(CaseSlaCalculator.Targets(nowUtc, nowUtc, sla), includeFirstResponse: true);

        var timeline = CaseEvent.Create(tenantId, created.Id, CaseEventType.Created, actorUserId, toValue: EnumText.Camel(CaseStatus.New));
        return new CaseCreation(created, timeline);
    }

    /// <summary>
    /// Tam değiştirme (PUT): konu, açıklama, firma/kişi; <paramref name="channel"/> verilmezse korunur. Durum/öncelik/atanan/SLA'yı
    /// değiştirmez. Yalnız aktif talepte (<c>case.not_active</c>).
    /// </summary>
    public Result Update(string subject, string? description, Guid? accountId, Guid? contactId, CaseChannel? channel)
    {
        var cleanSubject = Guard.MaxLength(Guard.NotEmpty(subject), ServiceLimits.SubjectMaxLength);
        if (!IsActive)
        {
            return Error.Conflict(ServiceErrors.NotActive);
        }

        Subject = cleanSubject;
        Description = Clean(description, ServiceLimits.DescriptionMaxLength);
        AccountId = OptionalId(accountId);
        ContactId = OptionalId(contactId);
        Channel = channel ?? Channel;
        return Result.Success();
    }

    /// <summary>
    /// Durum geçişi (§3.2–3.3). Aynı duruma geçiş idempotent (olay yok). Tablo dışı → <c>case.invalid_transition</c>; çözme /
    /// çözülmeden kapatmada not zorunlu (<c>case.resolution_required</c>). Çözüm: <c>resolvedAt = şimdi</c> ve <c>firstResponseAt</c>
    /// hâlâ boşsa çözüm anı ilk yanıt sayılır. Yeniden açma: <c>closed → open</c> yalnız <paramref name="reopenWindowDays"/> içinde
    /// (<c>case.reopen_window_expired</c>); sayaç/alan sıfırlama ve <c>dueAt</c> yeniden hesabı için <paramref name="slaForReopen"/> gerekir.
    /// </summary>
    public Result<CaseChange> ChangeStatus(CaseStatus target, string? resolutionNote, Guid? actorUserId, DateTime nowUtc, int reopenWindowDays, SlaMinutes? slaForReopen)
    {
        if (target == Status)
        {
            return CaseChange.None;
        }

        if (!CaseRules.IsTransitionAllowed(Status, target))
        {
            return Error.Conflict(ServiceErrors.InvalidTransition, ("from", EnumText.Camel(Status)), ("to", EnumText.Camel(target)));
        }

        var from = Status;
        var isReopen = target == CaseStatus.Open && from is CaseStatus.Resolved or CaseStatus.Closed;
        var writesResolution = target == CaseStatus.Resolved || (target == CaseStatus.Closed && from != CaseStatus.Resolved);

        var note = Clean(resolutionNote, ServiceLimits.ResolutionNoteMaxLength);
        if (writesResolution && note is null)
        {
            return Error.Validation(ServiceErrors.ResolutionRequired);
        }

        if (isReopen)
        {
            if (from == CaseStatus.Closed && ClosedAt is { } closedAt && nowUtc > closedAt.AddDays(reopenWindowDays))
            {
                return Error.Conflict(ServiceErrors.ReopenWindowExpired);
            }

            Guard.Against(slaForReopen is null, "SLA minutes are required to reopen a case.");
            ReopenCount++;
            ResolvedAt = null;
            ClosedAt = null;
            ResolutionNote = null;
            SlaAnchorAt = nowUtc;
            ApplyTargets(CaseSlaCalculator.Targets(CreatedAt, nowUtc, slaForReopen!.Value), includeFirstResponse: false);
        }
        else if (writesResolution)
        {
            ResolvedAt = nowUtc;
            ResolutionNote = note;
            FirstResponseAt ??= nowUtc;
        }

        if (target == CaseStatus.Closed)
        {
            ClosedAt = nowUtc;
        }

        Status = target;
        var timeline = CaseEvent.Create(
            TenantId,
            Id,
            CaseEventType.StatusChanged,
            actorUserId,
            EnumText.Camel(from),
            EnumText.Camel(target),
            writesResolution ? note : null);
        return new CaseChange([timeline], ResolvedNow: writesResolution);
    }

    /// <summary>
    /// Öncelik değişimi (yalnız aktif talepte): SLA hedefleri <b>özgün başlangıçtan</b> yeniden hesaplanır — <c>firstResponseDueAt</c>
    /// yalnız ilk yanıt yoksa <c>createdAt + yeni süre</c>, <c>dueAt</c> <c>slaAnchorAt + yeni süre</c> (yükseltme hedefi geçmişe
    /// düşürüp ihlal ettirebilir, düşürme uzatır — kasıtlı). Aynı öncelik idempotent.
    /// </summary>
    public Result<CaseChange> ChangePriority(CasePriority priority, SlaMinutes sla, Guid? actorUserId)
    {
        if (!IsActive)
        {
            return Error.Conflict(ServiceErrors.NotActive);
        }

        if (priority == Priority)
        {
            return CaseChange.None;
        }

        var from = Priority;
        Priority = priority;
        ApplyTargets(CaseSlaCalculator.Targets(CreatedAt, SlaAnchorAt, sla), includeFirstResponse: FirstResponseAt is null);

        var timeline = CaseEvent.Create(TenantId, Id, CaseEventType.PriorityChanged, actorUserId, EnumText.Camel(from), EnumText.Camel(priority));
        return new CaseChange([timeline]);
    }

    /// <summary>Atama / atamayı kaldırma (<c>null</c>); yalnız aktif talepte. Aynı atanan idempotent.</summary>
    public Result<CaseChange> Assign(Guid? assignedUserId, Guid? actorUserId)
    {
        if (!IsActive)
        {
            return Error.Conflict(ServiceErrors.NotActive);
        }

        var target = OptionalId(assignedUserId);
        if (target == AssignedUserId)
        {
            return CaseChange.None;
        }

        var from = AssignedUserId;
        AssignedUserId = target;
        var timeline = CaseEvent.Create(TenantId, Id, CaseEventType.Assigned, actorUserId, from?.ToString(), target?.ToString());
        return new CaseChange([timeline]);
    }

    /// <summary>
    /// Yorum ekler (kapalı talepte <c>case.closed</c>; çözülmüşte serbest). <c>firstResponseAt</c> boşken <b>herkese açık</b> yorum
    /// ilk yanıttır (<c>= şimdi</c>); dahili yorum sayılmaz, sonraki yorumlar değiştirmez. <c>new</c> durumundaki talepte herkese açık
    /// yorum durumu <c>open</c> yapar (olay aktörü yorumu yazan).
    /// </summary>
    public Result<CaseCommentOutcome> AddComment(CommentVisibility visibility, string body, Guid authorUserId, DateTime nowUtc)
    {
        var cleanBody = Guard.MaxLength(Guard.NotEmpty(body), ServiceLimits.CommentBodyMaxLength);
        if (Status == CaseStatus.Closed)
        {
            return Error.Conflict(ServiceErrors.Closed);
        }

        var comment = CaseComment.Create(TenantId, Id, visibility, cleanBody, authorUserId);
        var events = new List<CaseEvent>();
        if (visibility == CommentVisibility.Public)
        {
            FirstResponseAt ??= nowUtc;
            if (Status == CaseStatus.New)
            {
                Status = CaseStatus.Open;
                events.Add(CaseEvent.Create(TenantId, Id, CaseEventType.StatusChanged, authorUserId, EnumText.Camel(CaseStatus.New), EnumText.Camel(CaseStatus.Open)));
            }
        }

        return new CaseCommentOutcome(comment, events);
    }

    /// <summary>Okuma anındaki SLA değerlendirmesi (yanıt ve SQL süzgeci aynı tanımı kullanır).</summary>
    public SlaEvaluation EvaluateSla(DateTime nowUtc) =>
        CaseSlaCalculator.Evaluate(Status, FirstResponseAt, ResolvedAt, FirstResponseDueAt, FirstResponseWarnAt, DueAt, ResolutionWarnAt, nowUtc);

    /// <summary>Oluşturmadan çözüme tam dakika (aşağı yuvarlanır); çözülmemişse null.</summary>
    public int? ResolutionMinutes => ResolvedAt is { } resolved ? (int)Math.Max(0, Math.Floor((resolved - CreatedAt).TotalMinutes)) : null;

    private void ApplyTargets(SlaTargets targets, bool includeFirstResponse)
    {
        if (includeFirstResponse)
        {
            FirstResponseDueAt = targets.FirstResponseDueAt;
            FirstResponseWarnAt = targets.FirstResponseWarnAt;
        }

        DueAt = targets.DueAt;
        ResolutionWarnAt = targets.ResolutionWarnAt;
    }

    private static Guid? OptionalId(Guid? id) => id is { } value ? Guard.NotDefault(value) : null;

    private static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : Guard.MaxLength(trimmed, maxLength);
    }
}
