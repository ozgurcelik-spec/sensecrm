using Sense.Crm.Shared.Contracts.Events;

namespace Sense.Crm.Modules.Service.Contracts;

/// <summary>
/// Talep oluşturuldu (<c>CreateCaseHandler</c>): oluşturma transaction'ıyla aynı anda outbox'a yazılır (M8B webhook <c>case.created</c>). <see cref="Priority"/> ve <see cref="Channel"/> camelCase
/// tel değerleridir (<c>low|normal|high|urgent</c>, <c>email|phone|web|other</c>).
/// </summary>
public sealed record CaseCreated(
    Guid TenantId,
    Guid CaseId,
    string CaseNumber,
    Guid? AccountId,
    Guid? ContactId,
    string Priority,
    string Channel,
    Guid? AssignedUserId,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);
