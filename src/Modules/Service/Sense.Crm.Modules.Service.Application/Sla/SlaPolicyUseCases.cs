using FluentValidation;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Service.Domain;
using Sense.Crm.Modules.Service.Domain.Cases;
using Sense.Crm.Modules.Service.Domain.Sla;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Service.Application.Sla;

/// <summary>
/// SLA politikaları (<c>org.settings.manage</c>): her zaman dört satır, <c>low, normal, high, urgent</c> sırasıyla (eksikse tembel
/// tohumlanır).
/// </summary>
[RequiresPermission(OrgPermissions.SettingsManage)]
public sealed record ListSlaPoliciesQuery : IQuery<IReadOnlyList<SlaPolicyDto>>;

public sealed class ListSlaPoliciesHandler(SlaPolicyProvider provider) : IQueryHandler<ListSlaPoliciesQuery, IReadOnlyList<SlaPolicyDto>>
{
    public async Task<Result<IReadOnlyList<SlaPolicyDto>>> Handle(ListSlaPoliciesQuery query, CancellationToken cancellationToken)
    {
        var policies = await provider.EnsureAndListAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<SlaPolicyDto>>(policies
            .OrderBy(p => p.Priority)
            .Select(p => new SlaPolicyDto(p.Priority, p.FirstResponseMinutes, p.ResolutionMinutes))
            .ToList());
    }
}

/// <summary>Bir önceliğin yeni SLA süreleri (PUT gövdesi öğesi).</summary>
public sealed record SlaPolicyInput(CasePriority? Priority, int FirstResponseMinutes, int ResolutionMinutes);

/// <summary>
/// SLA politikalarını değiştirir: dizi tam dört önceliği (her biri bir kez) içermeli (<c>errors["policies"]</c>); süreler
/// <c>1 ≤ ilk yanıt ≤ çözüm ≤ 525600</c> (<c>errors["policies[2].firstResponseMinutes"]</c>). Değişiklik yalnız yeni hesaplamaları etkiler.
/// </summary>
[RequiresPermission(OrgPermissions.SettingsManage)]
public sealed record UpdateSlaPoliciesCommand(IReadOnlyList<SlaPolicyInput>? Policies) : ICommand;

public sealed class UpdateSlaPoliciesValidator : AbstractValidator<UpdateSlaPoliciesCommand>
{
    public UpdateSlaPoliciesValidator()
    {
        RuleFor(x => x.Policies)
            .NotNull()
            .Must(HasEveryPriorityOnce)
            .WithMessage(ServiceErrors.SlaPoliciesIncomplete);

        RuleForEach(x => x.Policies).ChildRules(policy =>
        {
            policy.RuleFor(p => p.Priority).NotNull();
            policy.RuleFor(p => p.FirstResponseMinutes)
                .Must(v => v >= 1).WithMessage(ServiceErrors.SlaMinutesRange);
            policy.RuleFor(p => p.FirstResponseMinutes)
                .Must((p, first) => first <= p.ResolutionMinutes).WithMessage(ServiceErrors.SlaMinutesRange);
            policy.RuleFor(p => p.ResolutionMinutes)
                .Must(v => v >= 1 && v <= ServiceLimits.MaxSlaMinutes).WithMessage(ServiceErrors.SlaMinutesRange);
        }).When(x => x.Policies is not null);
    }

    private static bool HasEveryPriorityOnce(IReadOnlyList<SlaPolicyInput>? policies) =>
        policies is not null
        && policies.Count == SlaPolicyDefaults.Priorities.Count
        && policies.All(p => p.Priority is not null)
        && policies.Select(p => p.Priority!.Value).Distinct().Count() == SlaPolicyDefaults.Priorities.Count;
}

public sealed class UpdateSlaPoliciesHandler(SlaPolicyProvider provider) : ICommandHandler<UpdateSlaPoliciesCommand>
{
    public async Task<Result> Handle(UpdateSlaPoliciesCommand command, CancellationToken cancellationToken)
    {
        var existing = await provider.EnsureAndListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var input in command.Policies!)
        {
            var policy = existing.Single(p => p.Priority == input.Priority!.Value);
            policy.Update(new SlaMinutes(input.FirstResponseMinutes, input.ResolutionMinutes));
        }

        return Result.Success();
    }
}

/// <summary>
/// Yeni organizasyon olayı (Identity kayıt): varsayılan SLA politikalarını tohumlar (idempotent). Outbox üzerinden Worker'da
/// (veya Worker yokken API başlangıcı/tembel yolla) çalışır; Sales'in aynı olay için mevcut tüketicisiyle birlikte kayıtlıdır.
/// </summary>
public sealed class OrganizationCreatedSlaHandler(IDefaultSlaPolicySeeder seeder) : IIntegrationEventHandler<OrganizationCreated>
{
    public async Task Handle(OrganizationCreated integrationEvent, CancellationToken cancellationToken) =>
        await seeder.EnsureAsync(integrationEvent.TenantId, cancellationToken).ConfigureAwait(false);
}
