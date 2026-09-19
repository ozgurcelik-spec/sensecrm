using Crm.Shared.Kernel;
using Crm.Shared.Kernel.Domain;

namespace Crm.Modules.Sales.Domain.Accounts;

/// <summary>
/// Firma (Zoho "Account"). Kiracıya ait, denetlenen, yumuşak silinen agregat. Ad kiracı içinde benzersiz değildir.
/// E-posta ve telefon kişisel veri sayılır: denetim kaydında maskelenir (KVKK).
/// </summary>
public sealed class Account : TenantAggregateRoot<Guid>, IAuditLogged, ISoftDelete
{
    private Account()
    {
    }

    private Account(Guid id, Guid tenantId, string name, Guid ownerUserId) : base(id, tenantId)
    {
        Name = name;
        OwnerUserId = ownerUserId;
    }

    public static IReadOnlySet<string> SensitiveFields { get; } =
        new HashSet<string>(StringComparer.Ordinal) { nameof(Email), nameof(Phone) };

    public string Name { get; private set; } = string.Empty;

    public string? Industry { get; private set; }

    public string? Website { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string? Description { get; private set; }

    public Guid OwnerUserId { get; private set; }

    public string? BillingStreet { get; private set; }

    public string? BillingCity { get; private set; }

    public string? BillingState { get; private set; }

    public string? BillingPostalCode { get; private set; }

    public string? BillingCountry { get; private set; }

    public Address? BillingAddress => Address.Normalize(new Address(BillingStreet, BillingCity, BillingState, BillingPostalCode, BillingCountry));

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    public static Account Create(
        Guid tenantId,
        string name,
        Guid ownerUserId,
        string? industry = null,
        string? website = null,
        string? phone = null,
        string? email = null,
        Address? billingAddress = null,
        string? description = null)
    {
        var account = new Account(
            Guid.CreateVersion7(),
            Guard.NotDefault(tenantId),
            Guard.MaxLength(Guard.NotEmpty(name), SalesLimits.NameMaxLength),
            Guard.NotDefault(ownerUserId));
        account.Apply(industry, website, phone, email, billingAddress, description);
        return account;
    }

    public void Update(
        string name,
        Guid ownerUserId,
        string? industry,
        string? website,
        string? phone,
        string? email,
        Address? billingAddress,
        string? description)
    {
        Name = Guard.MaxLength(Guard.NotEmpty(name), SalesLimits.NameMaxLength);
        OwnerUserId = Guard.NotDefault(ownerUserId);
        Apply(industry, website, phone, email, billingAddress, description);
    }

    private void Apply(string? industry, string? website, string? phone, string? email, Address? billingAddress, string? description)
    {
        Industry = Text.Clean(industry, SalesLimits.IndustryMaxLength);
        Website = Text.Clean(website, SalesLimits.WebsiteMaxLength);
        Phone = Text.Clean(phone, SalesLimits.PhoneMaxLength);
        Email = Text.Clean(email, SalesLimits.EmailMaxLength)?.ToLowerInvariant();
        Description = Text.Clean(description, SalesLimits.DescriptionMaxLength);

        var address = Address.Normalize(billingAddress);
        BillingStreet = address?.Street;
        BillingCity = address?.City;
        BillingState = address?.State;
        BillingPostalCode = address?.PostalCode;
        BillingCountry = address?.Country;
    }
}
