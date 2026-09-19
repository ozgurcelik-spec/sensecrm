using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Sales.Domain.Leads;

public enum LeadSource
{
    Web,
    Referral,
    Campaign,
    ColdCall,
    Other,
}

public enum LeadStatus
{
    New,
    Contacted,
    Qualified,
    Unqualified,
    Converted,
}

public enum LeadRating
{
    Hot,
    Warm,
    Cold,
}

/// <summary>
/// Potansiyel müşteri (Zoho "Lead"). Dönüştürülünce (<see cref="Convert"/>) salt-okurdur: güncellenemez ve tekrar
/// dönüştürülemez; oluşan firma/kişi/fırsat kimlikleri üzerinde tutulur. Dönüştürmenin kendisi (firma + kişi + fırsat
/// oluşturma) uygulama katmanında tek transaction'da yapılır.
/// </summary>
public sealed class Lead : TenantAggregateRoot<Guid>, IAuditLogged, ISoftDelete
{
    private Lead()
    {
    }

    private Lead(Guid id, Guid tenantId, string lastName, string company, Guid ownerUserId) : base(id, tenantId)
    {
        LastName = lastName;
        Company = company;
        OwnerUserId = ownerUserId;
        Status = LeadStatus.New;
    }

    public static IReadOnlySet<string> SensitiveFields { get; } =
        new HashSet<string>(StringComparer.Ordinal) { nameof(Email), nameof(Phone) };

    public string? FirstName { get; private set; }

    public string LastName { get; private set; } = string.Empty;

    public string Company { get; private set; } = string.Empty;

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public LeadSource Source { get; private set; } = LeadSource.Other;

    public LeadStatus Status { get; private set; }

    public LeadRating? Rating { get; private set; }

    public Guid OwnerUserId { get; private set; }

    /// <summary>Sahibin son değiştiği an (UTC); hiç değişmediyse null (round-robin için oluşturulma anı kullanılır).</summary>
    public DateTime? OwnerAssignedAt { get; private set; }

    public Guid? ConvertedAccountId { get; private set; }

    public Guid? ConvertedContactId { get; private set; }

    public Guid? ConvertedDealId { get; private set; }

    public DateTime? ConvertedAt { get; private set; }

    public string FullName => string.Join(' ', new[] { FirstName, LastName }.Where(p => !string.IsNullOrWhiteSpace(p)));

    public bool IsConverted => Status == LeadStatus.Converted;

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    public static Lead Create(
        Guid tenantId,
        string? firstName,
        string lastName,
        string company,
        Guid ownerUserId,
        LeadSource source = LeadSource.Other,
        LeadRating? rating = null,
        string? email = null,
        string? phone = null)
    {
        var lead = new Lead(
            Guid.CreateVersion7(),
            Guard.NotDefault(tenantId),
            Guard.MaxLength(Guard.NotEmpty(lastName), SalesLimits.PersonNameMaxLength),
            Guard.MaxLength(Guard.NotEmpty(company), SalesLimits.NameMaxLength),
            Guard.NotDefault(ownerUserId));
        lead.Apply(firstName, email, phone, source, rating);
        return lead;
    }

    /// <summary>Dönüşmüş lead için <c>lead.already_converted</c>; durum yalnız <see cref="Convert"/> ile <c>Converted</c> olur.</summary>
    public Result Update(
        string? firstName,
        string lastName,
        string company,
        Guid ownerUserId,
        LeadSource source,
        LeadStatus status,
        LeadRating? rating,
        string? email,
        string? phone)
    {
        if (IsConverted)
        {
            return Error.Conflict(SalesErrors.LeadAlreadyConverted);
        }

        if (status == LeadStatus.Converted)
        {
            return Error.Validation(SalesErrors.LeadStatusNotSettable);
        }

        LastName = Guard.MaxLength(Guard.NotEmpty(lastName), SalesLimits.PersonNameMaxLength);
        Company = Guard.MaxLength(Guard.NotEmpty(company), SalesLimits.NameMaxLength);
        if (ownerUserId != OwnerUserId)
        {
            OwnerAssignedAt = DateTime.UtcNow;
        }

        OwnerUserId = Guard.NotDefault(ownerUserId);
        Status = status;
        Apply(firstName, email, phone, source, rating);
        return Result.Success();
    }

    /// <summary>
    /// Sahibi değiştirir (workflow atamasında kullanılır) ve <see cref="OwnerAssignedAt"/>'i günceller. Dönüşmüş lead için
    /// <c>lead.already_converted</c>. Sahip aynıysa da atama anı yenilenir (round-robin sırası).
    /// </summary>
    public Result AssignOwner(Guid ownerUserId, DateTime nowUtc)
    {
        if (IsConverted)
        {
            return Error.Conflict(SalesErrors.LeadAlreadyConverted);
        }

        OwnerUserId = Guard.NotDefault(ownerUserId);
        OwnerAssignedAt = nowUtc;
        return Result.Success();
    }

    /// <summary>Lead'i dönüşmüş işaretler ve oluşan kayıtların kimliklerini saklar; ikinci kez dönüştürme <c>lead.already_converted</c>.</summary>
    public Result Convert(Guid accountId, Guid contactId, Guid? dealId, DateTime nowUtc)
    {
        if (IsConverted)
        {
            return Error.Conflict(SalesErrors.LeadAlreadyConverted);
        }

        Status = LeadStatus.Converted;
        ConvertedAccountId = Guard.NotDefault(accountId);
        ConvertedContactId = Guard.NotDefault(contactId);
        ConvertedDealId = dealId;
        ConvertedAt = nowUtc;
        return Result.Success();
    }

    private void Apply(string? firstName, string? email, string? phone, LeadSource source, LeadRating? rating)
    {
        FirstName = Text.Clean(firstName, SalesLimits.PersonNameMaxLength);
        Email = Text.Clean(email, SalesLimits.EmailMaxLength)?.ToLowerInvariant();
        Phone = Text.Clean(phone, SalesLimits.PhoneMaxLength);
        Source = source;
        Rating = rating;
    }
}
