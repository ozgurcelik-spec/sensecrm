using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Modules.Service.Domain;
using Sense.Crm.Modules.Service.Domain.Cases;
using Sense.Crm.Modules.Service.Domain.Sla;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Service.Application;

/// <summary>Modül ayarları (<c>Service</c> bölümü): kapalı talebin yeniden açılabildiği gün sayısı.</summary>
public sealed class ServiceSettings
{
    public const string SectionName = "Service";

    public int ReopenWindowDays { get; set; } = CaseRules.ReopenWindowDays;
}

/// <summary>
/// Atanan kullanıcı kuralı: verilmezse atanmamış (temsilci kuyruğu); verilen (ve mevcut atanandan farklı) kullanıcı aktif
/// organizasyonun aktif üyesi olmalıdır (<c>owner.not_member</c>, 400). Mevcut atanan korunurken üyelik yeniden sorgulanmaz.
/// </summary>
public sealed class CaseAssigneeVerifier(IMemberLookup members)
{
    public async Task<Result> VerifyAsync(Guid? requested, Guid? current, CancellationToken ct)
    {
        if (requested is { } candidate && candidate != current && !await members.IsActiveMemberAsync(candidate, ct).ConfigureAwait(false))
        {
            return Error.Validation(ServiceErrors.OwnerNotMember);
        }

        return Result.Success();
    }
}

/// <summary>Doğrulanmış (ve gerekirse türetilmiş) firma/kişi bağı.</summary>
public sealed record ResolvedCaseLinks(Guid? AccountId, Guid? ContactId);

/// <summary>
/// Firma/kişi bağı kuralları (yumuşak bağ, yalnız <c>Sales.Contracts</c> üzerinden): bağ yalnız değiştiğinde yeniden doğrulanır
/// (bağlı kayıt sonradan silinmiş talep başka alanlar için düzenlenebilir kalır). <c>contactId</c> verilip <c>accountId</c> verilmezse ve kişinin
/// firması varsa firma kişiden türetilir (yalnız kişi değiştiyse); ikisi de verilir ve kişinin firması farklıysa
/// <c>case.contact_account_mismatch</c> (400); firması olmayan kişi herhangi bir firmayla verilebilir. Sıra: firma 404, kişi 404, uyuşmazlık 400.
/// </summary>
public sealed class CaseLinkResolver(IRecordLookup records, IContactAccountLookup contacts)
{
    public async Task<Result<ResolvedCaseLinks>> ResolveAsync(Guid? accountId, Guid? contactId, Guid? currentAccountId, Guid? currentContactId, CancellationToken ct)
    {
        var accountChanged = accountId != currentAccountId;
        var contactChanged = contactId != currentContactId;

        if (accountChanged && accountId is { } account && !await records.ExistsAsync(RecordType.Account, account, ct).ConfigureAwait(false))
        {
            return Error.NotFound(ServiceErrors.AccountNotFound);
        }

        ContactAccountLink? link = null;
        if (contactId is { } contact && (contactChanged || accountChanged))
        {
            link = await contacts.FindAsync(contact, ct).ConfigureAwait(false);
            if (link is null && contactChanged)
            {
                return Error.NotFound(ServiceErrors.ContactNotFound);
            }
        }

        var resolvedAccount = accountId;
        if (link?.AccountId is { } contactAccount)
        {
            if (accountId is null)
            {
                if (contactChanged)
                {
                    resolvedAccount = contactAccount;
                }
            }
            else if (accountId != contactAccount)
            {
                return Error.Validation(ServiceErrors.ContactAccountMismatch);
            }
        }

        return new ResolvedCaseLinks(resolvedAccount, contactId);
    }
}

/// <summary>
/// Öncelik başına SLA süresi: politika satırı yoksa (kayıt olayı henüz işlenmemiş olabilir) tembel olarak tohumlar, böylece kayıttan
/// hemen sonraki ilk talep de çalışır. Politika sonradan değişirse mevcut taleplerin hedefleri değişmez (anlık görüntü).
/// </summary>
public sealed class SlaPolicyProvider(ISlaPolicyRepository policies, IDefaultSlaPolicySeeder seeder, ITenantContext tenant)
{
    public async Task<SlaMinutes> GetAsync(CasePriority priority, CancellationToken ct)
    {
        var policy = await policies.GetAsync(priority, ct).ConfigureAwait(false);
        if (policy is null)
        {
            await seeder.EnsureAsync(tenant.TenantId, ct).ConfigureAwait(false);
            policy = await policies.GetAsync(priority, ct).ConfigureAwait(false);
        }

        return policy?.Minutes ?? SlaPolicyDefaults.For(priority);
    }

    /// <summary>Kiracının dört politikasını (eksikse tohumlayarak) öncelik sırasıyla döner.</summary>
    public async Task<IReadOnlyList<SlaPolicy>> EnsureAndListAsync(CancellationToken ct)
    {
        var existing = await policies.ListAsync(ct).ConfigureAwait(false);
        if (existing.Count >= SlaPolicyDefaults.Priorities.Count)
        {
            return existing;
        }

        await seeder.EnsureAsync(tenant.TenantId, ct).ConfigureAwait(false);
        return await policies.ListAsync(ct).ConfigureAwait(false);
    }
}
