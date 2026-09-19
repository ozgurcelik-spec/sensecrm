using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Modules.Sales.Domain.Contacts;

/// <summary>
/// Kişi (Zoho "Contact"): bir firmaya bağlı olabilir. E-posta, telefon ve cep KVKK gereği denetim kaydında maskelenir.
/// </summary>
public sealed class Contact : TenantAggregateRoot<Guid>, IAuditLogged, ISoftDelete
{
    private Contact()
    {
    }

    private Contact(Guid id, Guid tenantId, string lastName, Guid ownerUserId) : base(id, tenantId)
    {
        LastName = lastName;
        OwnerUserId = ownerUserId;
    }

    public static IReadOnlySet<string> SensitiveFields { get; } =
        new HashSet<string>(StringComparer.Ordinal) { nameof(Email), nameof(Phone), nameof(Mobile) };

    public string? FirstName { get; private set; }

    public string LastName { get; private set; } = string.Empty;

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public string? Mobile { get; private set; }

    public string? Title { get; private set; }

    public Guid? AccountId { get; private set; }

    public Guid OwnerUserId { get; private set; }

    public string? MailingStreet { get; private set; }

    public string? MailingCity { get; private set; }

    public string? MailingState { get; private set; }

    public string? MailingPostalCode { get; private set; }

    public string? MailingCountry { get; private set; }

    public string FullName => string.Join(' ', new[] { FirstName, LastName }.Where(p => !string.IsNullOrWhiteSpace(p)));

    public Address? MailingAddress => Address.Normalize(new Address(MailingStreet, MailingCity, MailingState, MailingPostalCode, MailingCountry));

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    public static Contact Create(
        Guid tenantId,
        string? firstName,
        string lastName,
        Guid ownerUserId,
        Guid? accountId = null,
        string? email = null,
        string? phone = null,
        string? mobile = null,
        string? title = null,
        Address? mailingAddress = null)
    {
        var contact = new Contact(
            Guid.CreateVersion7(),
            Guard.NotDefault(tenantId),
            Guard.MaxLength(Guard.NotEmpty(lastName), SalesLimits.PersonNameMaxLength),
            Guard.NotDefault(ownerUserId));
        contact.Apply(firstName, accountId, email, phone, mobile, title, mailingAddress);
        return contact;
    }

    public void Update(
        string? firstName,
        string lastName,
        Guid ownerUserId,
        Guid? accountId,
        string? email,
        string? phone,
        string? mobile,
        string? title,
        Address? mailingAddress)
    {
        LastName = Guard.MaxLength(Guard.NotEmpty(lastName), SalesLimits.PersonNameMaxLength);
        OwnerUserId = Guard.NotDefault(ownerUserId);
        Apply(firstName, accountId, email, phone, mobile, title, mailingAddress);
    }

    private void Apply(string? firstName, Guid? accountId, string? email, string? phone, string? mobile, string? title, Address? mailingAddress)
    {
        FirstName = Text.Clean(firstName, SalesLimits.PersonNameMaxLength);
        AccountId = accountId;
        Email = Text.Clean(email, SalesLimits.EmailMaxLength)?.ToLowerInvariant();
        Phone = Text.Clean(phone, SalesLimits.PhoneMaxLength);
        Mobile = Text.Clean(mobile, SalesLimits.PhoneMaxLength);
        Title = Text.Clean(title, SalesLimits.TitleMaxLength);

        var address = Address.Normalize(mailingAddress);
        MailingStreet = address?.Street;
        MailingCity = address?.City;
        MailingState = address?.State;
        MailingPostalCode = address?.PostalCode;
        MailingCountry = address?.Country;
    }
}
