using Crm.Modules.Marketing.Contracts;
using Crm.Modules.Marketing.Domain;
using Crm.Modules.Marketing.Domain.Members;
using Crm.Modules.Sales.Contracts;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Events;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;
using FluentValidation;

namespace Crm.Modules.Marketing.Application.Members;

/// <summary>Kampanya üyeleri (sayfalı): filtreler <c>memberType</c>, <c>status</c> (virgülle çoklu); ada göre arama/sıralama yoktur (ad Sales'te).</summary>
[RequiresPermission(MarketingPermissions.Read)]
public sealed record ListCampaignMembersQuery(Guid CampaignId, PagedQuery Paging, CampaignMemberType? MemberType, string? Status)
    : IQuery<PagedResult<CampaignMemberDto>>;

public sealed class ListCampaignMembersValidator : AbstractValidator<ListCampaignMembersQuery>
{
    public ListCampaignMembersValidator() =>
        RuleFor(x => x.Status).Must(v => EnumListParser.TryParse<CampaignMemberStatus>(v, out _)).WithMessage(MarketingErrors.InvalidMemberStatus);
}

public sealed class ListCampaignMembersHandler(IMarketingReadStore store) : IQueryHandler<ListCampaignMembersQuery, PagedResult<CampaignMemberDto>>
{
    public async Task<Result<PagedResult<CampaignMemberDto>>> Handle(ListCampaignMembersQuery query, CancellationToken cancellationToken)
    {
        if (!await store.CampaignExistsAsync(query.CampaignId, cancellationToken).ConfigureAwait(false))
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var filter = new MemberFilter(query.MemberType, EnumListParser.ParseOrEmpty<CampaignMemberStatus>(query.Status));
        return await store.ListMembersAsync(query.CampaignId, query.Paging, filter, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Toplu ekleme (tek üye = tek elemanlı dizi), idempotent: zaten üye olanlar <c>alreadyMemberCount</c>'a girer; kiracıda olmayan
/// (yok/silinmiş/başka kiracı) kimlikler 404 vermez, <c>skipped: not_found</c> olur; dönüşmüş lead <c>skipped: lead_converted</c>.
/// Kampanya <c>completed</c>/<c>cancelled</c> ise <c>campaign.closed</c> (409, hiçbir şey eklenmez). 1–500 kimlik, tekrarlar tekilleştirilir.
/// </summary>
[RequiresPermission(MarketingPermissions.Write)]
public sealed record AddCampaignMembersCommand(Guid CampaignId, CampaignMemberType? MemberType, IReadOnlyList<Guid>? MemberIds) : ICommand<AddMembersResult>;

public sealed class AddCampaignMembersValidator : AbstractValidator<AddCampaignMembersCommand>
{
    public AddCampaignMembersValidator()
    {
        RuleFor(x => x.CampaignId).NotEmpty();
        RuleFor(x => x.MemberType).NotNull();
        RuleFor(x => x.MemberIds).ValidMemberIds();
    }
}

public sealed class AddCampaignMembersHandler(
    ICampaignRepository campaigns,
    ICampaignMemberRepository members,
    IRecordLookup records,
    ILeadStatusLookup leads,
    ITenantContext tenant,
    ICurrentUser user,
    TimeProvider clock) : ICommandHandler<AddCampaignMembersCommand, AddMembersResult>
{
    public async Task<Result<AddMembersResult>> Handle(AddCampaignMembersCommand command, CancellationToken cancellationToken)
    {
        var campaign = await campaigns.GetByIdAsync(command.CampaignId, cancellationToken).ConfigureAwait(false);
        if (campaign is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (!campaign.AcceptsNewMembers)
        {
            return Error.Conflict(MarketingErrors.CampaignClosed);
        }

        var type = command.MemberType!.Value;
        var ids = command.MemberIds!.Distinct().ToList();

        var already = await members.GetExistingMemberIdsAsync(campaign.Id, type, ids, cancellationToken).ConfigureAwait(false);
        var candidates = ids.Where(id => !already.Contains(id)).ToList();

        // Kiracıda olmayan kayıtlar 404 değil skipped (tür başına tek sorgu; Sales kiracı + yumuşak silme filtresi altındadır).
        var recordType = MemberTypeMapping.ToRecordType(type);
        var known = candidates.Count == 0
            ? new Dictionary<RecordRef, string>()
            : await records.GetDisplayNamesAsync(candidates.Select(id => new RecordRef(recordType, id)).ToList(), cancellationToken).ConfigureAwait(false);
        var existing = candidates.Where(id => known.ContainsKey(new RecordRef(recordType, id))).ToHashSet();

        var converted = type == CampaignMemberType.Lead && existing.Count > 0
            ? await leads.GetConvertedLeadIdsAsync(existing, cancellationToken).ConfigureAwait(false)
            : new HashSet<Guid>();

        var skipped = new List<SkippedMember>();
        var toAdd = new List<CampaignMember>();
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var id in candidates)
        {
            if (!existing.Contains(id))
            {
                skipped.Add(new SkippedMember(id, MarketingErrors.SkipReasons.NotFound));
            }
            else if (converted.Contains(id))
            {
                skipped.Add(new SkippedMember(id, MarketingErrors.SkipReasons.LeadConverted));
            }
            else
            {
                toAdd.Add(CampaignMember.Create(tenant.TenantId, campaign.Id, type, id, user.UserId, now));
            }
        }

        var added = toAdd.Count == 0
            ? new HashSet<Guid>()
            : await members.AddRangeIgnoringDuplicatesAsync(toAdd, cancellationToken).ConfigureAwait(false);
        var addedCount = toAdd.Count(m => added.Contains(m.Id));

        // Eşzamanlı istek aynı üyeyi önce eklediyse (benzersiz ihlali) hata değil "zaten üye" sayılır.
        var alreadyMemberCount = already.Count + (toAdd.Count - addedCount);
        return new AddMembersResult(addedCount, alreadyMemberCount, skipped);
    }
}

/// <summary>
/// Toplu üye durumu (<c>added|sent|responded|unsubscribed</c>; <c>converted</c> → <c>validation</c>). <c>memberIds</c> üyelik <b>satır</b>
/// kimlikleridir; bu kampanyada olmayanlar sessizce yok sayılır. <c>converted</c> kilitli satırlar <c>skippedCount</c>'a girer. Kapalı kampanyada da çalışır.
/// </summary>
[RequiresPermission(MarketingPermissions.Write)]
public sealed record SetCampaignMemberStatusCommand(Guid CampaignId, IReadOnlyList<Guid>? MemberIds, CampaignMemberStatus? Status) : ICommand<SetMemberStatusResult>;

public sealed class SetCampaignMemberStatusValidator : AbstractValidator<SetCampaignMemberStatusCommand>
{
    public SetCampaignMemberStatusValidator()
    {
        RuleFor(x => x.CampaignId).NotEmpty();
        RuleFor(x => x.MemberIds).ValidMemberIds();
        RuleFor(x => x.Status).NotNull();
        RuleFor(x => x.Status).NotEqual(CampaignMemberStatus.Converted).WithMessage(MarketingErrors.ManualConvertedStatus);
    }
}

public sealed class SetCampaignMemberStatusHandler(ICampaignRepository campaigns, ICampaignMemberRepository members, TimeProvider clock)
    : ICommandHandler<SetCampaignMemberStatusCommand, SetMemberStatusResult>
{
    public async Task<Result<SetMemberStatusResult>> Handle(SetCampaignMemberStatusCommand command, CancellationToken cancellationToken)
    {
        if (await campaigns.GetByIdAsync(command.CampaignId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var rows = await members.GetByIdsAsync(command.CampaignId, command.MemberIds!.Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow().UtcDateTime;
        int updated = 0, skipped = 0;
        foreach (var row in rows)
        {
            var change = row.ChangeStatus(command.Status!.Value, now);
            if (change.IsFailure)
            {
                return change.Error;
            }

            switch (change.Value)
            {
                case MemberStatusChange.Changed:
                    updated++;
                    break;
                case MemberStatusChange.Locked:
                    skipped++;
                    break;
                default:
                    break;
            }
        }

        return new SetMemberStatusResult(updated, skipped);
    }
}

/// <summary><c>memberIds</c> üyelik satır kimlikleridir; olmayanlar yok sayılır (idempotent). Üyelik fiziksel silinir (denetim kaydı <c>deleted</c>). Kapalı kampanyada da çalışır.</summary>
[RequiresPermission(MarketingPermissions.Write)]
public sealed record RemoveCampaignMembersCommand(Guid CampaignId, IReadOnlyList<Guid>? MemberIds) : ICommand<RemoveMembersResult>;

public sealed class RemoveCampaignMembersValidator : AbstractValidator<RemoveCampaignMembersCommand>
{
    public RemoveCampaignMembersValidator()
    {
        RuleFor(x => x.CampaignId).NotEmpty();
        RuleFor(x => x.MemberIds).ValidMemberIds();
    }
}

public sealed class RemoveCampaignMembersHandler(ICampaignRepository campaigns, ICampaignMemberRepository members)
    : ICommandHandler<RemoveCampaignMembersCommand, RemoveMembersResult>
{
    public async Task<Result<RemoveMembersResult>> Handle(RemoveCampaignMembersCommand command, CancellationToken cancellationToken)
    {
        if (await campaigns.GetByIdAsync(command.CampaignId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var rows = await members.GetByIdsAsync(command.CampaignId, command.MemberIds!.Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            members.Remove(row);
        }

        return new RemoveMembersResult(rows.Count);
    }
}

/// <summary>
/// Kayıt bazlı üyelikler (<c>GET /campaigns/by-member</c>): lead/kişi <c>IRecordLookup.ExistsAsync</c> ile doğrulanır (yoksa <c>not_found</c>);
/// yalnız silinmemiş kampanyalar, en çok 200 satır, <c>addedAt</c> azalan.
/// </summary>
[RequiresPermission(MarketingPermissions.Read)]
public sealed record GetRecordCampaignsQuery(CampaignMemberType? MemberType, Guid? MemberId) : IQuery<IReadOnlyList<RecordCampaignDto>>;

public sealed class GetRecordCampaignsValidator : AbstractValidator<GetRecordCampaignsQuery>
{
    public GetRecordCampaignsValidator()
    {
        RuleFor(x => x.MemberType).NotNull();
        RuleFor(x => x.MemberId).NotNull().NotEqual(Guid.Empty);
    }
}

public sealed class GetRecordCampaignsHandler(IMarketingReadStore store, IRecordLookup records) : IQueryHandler<GetRecordCampaignsQuery, IReadOnlyList<RecordCampaignDto>>
{
    public async Task<Result<IReadOnlyList<RecordCampaignDto>>> Handle(GetRecordCampaignsQuery query, CancellationToken cancellationToken)
    {
        var type = query.MemberType!.Value;
        var id = query.MemberId!.Value;
        if (!await records.ExistsAsync(MemberTypeMapping.ToRecordType(type), id, cancellationToken).ConfigureAwait(false))
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        return Result.Success(await store.ListRecordCampaignsAsync(type, id, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>
/// <see cref="LeadConverted"/> tüketicisi (Worker'da outbox → event bus; API'de aynı handler kayıtlıdır): olayın kiracı kapsamında
/// <c>(lead, LeadId)</c> olan <b>tüm</b> üyelikleri (her kampanya, kampanya durumu ne olursa olsun, silinmiş kampanya hariç)
/// <c>converted</c> yapar. Zaten dönüşmüş satırlara dokunmaz → idempotent; üyeliği olmayan lead için no-op. Eşleşen kişi kampanyaya
/// eklenmez. Değişiklikler <c>CampaignMember</c> denetim kaydı üretir (kullanıcı boş, sistem bağlamı).
/// </summary>
public sealed class LeadConvertedMarketingHandler(ICampaignMemberRepository members, IMarketingUnitOfWork unitOfWork, TimeProvider clock)
    : IIntegrationEventHandler<LeadConverted>
{
    public async Task Handle(LeadConverted integrationEvent, CancellationToken cancellationToken)
    {
        var memberships = await members.GetOpenLeadMembershipsAsync(integrationEvent.LeadId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow().UtcDateTime;
        var changed = 0;
        foreach (var membership in memberships)
        {
            if (membership.MarkConverted(now))
            {
                changed++;
            }
        }

        if (changed > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
