using Sense.Crm.Modules.Sales.Domain.Leads;
using Sense.Crm.Modules.Sales.Domain.Pipelines;

namespace Sense.Crm.Modules.Sales.Application;

// HTTP sözleşmesi (docs/plan/m2-api-kontrat.md): alanlar camelCase serileştirilir, null alanlar yazılmaz, enum'lar camelCase string.

public sealed record AddressDto(string? Street, string? City, string? State, string? PostalCode, string? Country);

/// <summary><see cref="ContactCount"/>/<see cref="DealCount"/> yalnız detay yanıtında dolar.</summary>
public sealed record AccountDto(
    Guid Id,
    string Name,
    string? Industry,
    string? Website,
    string? Phone,
    string? Email,
    AddressDto? BillingAddress,
    string? Description,
    Guid OwnerUserId,
    string? OwnerName,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    int? ContactCount = null,
    int? DealCount = null);

public sealed record ContactDto(
    Guid Id,
    string? FirstName,
    string LastName,
    string FullName,
    string? Email,
    string? Phone,
    string? Mobile,
    string? Title,
    Guid? AccountId,
    string? AccountName,
    AddressDto? MailingAddress,
    Guid OwnerUserId,
    string? OwnerName,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record LeadDto(
    Guid Id,
    string? FirstName,
    string LastName,
    string FullName,
    string Company,
    string? Email,
    string? Phone,
    LeadSource Source,
    LeadStatus Status,
    LeadRating? Rating,
    Guid OwnerUserId,
    string? OwnerName,
    Guid? ConvertedAccountId,
    Guid? ConvertedContactId,
    Guid? ConvertedDealId,
    DateTime? ConvertedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record ConvertLeadResult(Guid AccountId, Guid ContactId, Guid? DealId);

public sealed record PipelineStageDto(Guid Id, string Name, int Order, int Probability, StageKind Kind);

public sealed record PipelineDto(Guid Id, string Name, bool IsDefault, IReadOnlyList<PipelineStageDto> Stages);

public sealed record DealDto(
    Guid Id,
    string Name,
    Guid AccountId,
    string AccountName,
    Guid? ContactId,
    string? ContactName,
    Guid PipelineId,
    string PipelineName,
    Guid StageId,
    string StageName,
    StageKind StageKind,
    int Probability,
    decimal? Amount,
    string Currency,
    DateOnly? ClosingDate,
    DateTime? ClosedAt,
    string? LostReason,
    Guid OwnerUserId,
    string? OwnerName,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>Kanban kartı özeti.</summary>
public sealed record BoardDealDto(Guid Id, string Name, string AccountName, decimal? Amount, string Currency, DateOnly? ClosingDate, string? OwnerName);

/// <summary><see cref="TotalAmount"/> ve <see cref="Count"/> aşamadaki tüm fırsatları kapsar; <see cref="Deals"/> en çok 100 kartla sınırlıdır.</summary>
public sealed record BoardStageDto(
    Guid Id,
    string Name,
    StageKind Kind,
    int Probability,
    decimal TotalAmount,
    int Count,
    IReadOnlyList<BoardDealDto> Deals);

public sealed record DealBoardDto(Guid PipelineId, IReadOnlyList<BoardStageDto> Stages);

/// <summary>Fırsat listesi filtreleri (sözleşme: pipelineId, stageId, stageKind, ownerUserId, accountId).</summary>
public sealed record DealFilter(Guid? PipelineId, Guid? StageId, StageKind? StageKind, Guid? OwnerUserId, Guid? AccountId);
