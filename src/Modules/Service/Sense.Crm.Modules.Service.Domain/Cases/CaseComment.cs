using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Modules.Service.Domain.Cases;

/// <summary>
/// Talep yorumu: herkese açık yanıt veya dahili not. Değişmez (düzenleme/silme yok; denetim kaydı bütünlüğü). Yazarlar
/// organizasyon üyeleridir ("temsilci"). <c>Body</c> kişisel veri taşıyabildiğinden denetim kaydında <c>***</c> maskelenir (K13).
/// </summary>
public sealed class CaseComment : TenantAggregateRoot<Guid>, IAuditLogged
{
    private CaseComment()
    {
    }

    private CaseComment(Guid id, Guid tenantId, Guid caseId, CommentVisibility visibility, string body, Guid authorUserId)
        : base(id, tenantId)
    {
        CaseId = caseId;
        Visibility = visibility;
        Body = body;
        AuthorUserId = authorUserId;
    }

    public static IReadOnlySet<string> SensitiveFields { get; } =
        new HashSet<string>(StringComparer.Ordinal) { nameof(Body) };

    public Guid CaseId { get; private set; }

    public CommentVisibility Visibility { get; private set; }

    public string Body { get; private set; } = string.Empty;

    public Guid AuthorUserId { get; private set; }

    public static CaseComment Create(Guid tenantId, Guid caseId, CommentVisibility visibility, string body, Guid authorUserId) =>
        new(
            Guid.CreateVersion7(),
            Guard.NotDefault(tenantId),
            Guard.NotDefault(caseId),
            visibility,
            Guard.MaxLength(Guard.NotEmpty(body), ServiceLimits.CommentBodyMaxLength),
            Guard.NotDefault(authorUserId));
}
