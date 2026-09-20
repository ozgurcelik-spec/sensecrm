using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Modules.Commerce.Domain.Vendors;

/// <summary>
/// Tedarikçi (Zoho "Vendor"): satın alma emirlerinin muhatabı; ürünlerin birincil tedarikçisi olabilir (<c>Product.VendorId</c>).
/// Kiracıya ait, denetlenen, yumuşak silinen agregat; ad benzersiz değildir. E-posta ve telefon kişisel veri sayılır: denetim kaydında maskelenir (KVKK).
/// Adres düz kolonlardır (<c>address_*</c>; denetim alan bazında fark üretsin diye). <see cref="EmailOptOut"/> v1'de yalnız saklanır.
/// </summary>
public sealed class Vendor : TenantAggregateRoot<Guid>, IAuditLogged, ISoftDelete
{
    private Vendor()
    {
    }

    private Vendor(Guid id, Guid tenantId) : base(id, tenantId)
    {
    }

    public static IReadOnlySet<string> SensitiveFields { get; } =
        new HashSet<string>(StringComparer.Ordinal) { nameof(Email), nameof(Phone) };

    public string Name { get; private set; } = string.Empty;

    public Guid OwnerUserId { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string? Website { get; private set; }

    public string? Category { get; private set; }

    /// <summary>"DK hesabı": serbest metin (muhasebe eşlemesi yok).</summary>
    public string? GlAccount { get; private set; }

    public string? AddressStreet { get; private set; }

    public string? AddressBuilding { get; private set; }

    public string? AddressCity { get; private set; }

    public string? AddressState { get; private set; }

    public string? AddressPostalCode { get; private set; }

    public string? AddressCountry { get; private set; }

    public DocumentAddress? Address =>
        DocumentAddress.Normalize(new DocumentAddress(AddressStreet, AddressBuilding, AddressCity, AddressState, AddressPostalCode, AddressCountry));

    public string? Description { get; private set; }

    public bool EmailOptOut { get; private set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    public static Vendor Create(
        Guid tenantId,
        string name,
        Guid ownerUserId,
        string? phone,
        string? email,
        string? website,
        string? category,
        string? glAccount,
        DocumentAddress? address,
        string? description,
        bool emailOptOut)
    {
        var vendor = new Vendor(Guid.CreateVersion7(), Guard.NotDefault(tenantId));
        vendor.Apply(name, ownerUserId, phone, email, website, category, glAccount, address, description, emailOptOut);
        return vendor;
    }

    /// <summary>Tam değiştirme (PUT): gönderilmeyen isteğe bağlı alan temizlenir; <paramref name="emailOptOut"/> verilmezse mevcut korunur.</summary>
    public void Update(
        string name,
        Guid ownerUserId,
        string? phone,
        string? email,
        string? website,
        string? category,
        string? glAccount,
        DocumentAddress? address,
        string? description,
        bool? emailOptOut) =>
        Apply(name, ownerUserId, phone, email, website, category, glAccount, address, description, emailOptOut ?? EmailOptOut);

    private void Apply(
        string name,
        Guid ownerUserId,
        string? phone,
        string? email,
        string? website,
        string? category,
        string? glAccount,
        DocumentAddress? address,
        string? description,
        bool emailOptOut)
    {
        Name = Guard.MaxLength(Guard.NotEmpty(name), CommerceLimits.NameMaxLength);
        OwnerUserId = Guard.NotDefault(ownerUserId);
        Phone = Clean(phone, CommerceLimits.VendorPhoneMaxLength);
        Email = Clean(email, CommerceLimits.VendorEmailMaxLength)?.ToLowerInvariant();
        Website = Clean(website, CommerceLimits.VendorWebsiteMaxLength);
        Category = Clean(category, CommerceLimits.VendorTextMaxLength);
        GlAccount = Clean(glAccount, CommerceLimits.VendorTextMaxLength);
        Description = Clean(description, CommerceLimits.VendorDescriptionMaxLength);
        EmailOptOut = emailOptOut;

        var a = DocumentAddress.Normalize(address);
        AddressStreet = a?.Street;
        AddressBuilding = a?.Building;
        AddressCity = a?.City;
        AddressState = a?.State;
        AddressPostalCode = a?.PostalCode;
        AddressCountry = a?.Country;
    }

    private static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : Guard.MaxLength(trimmed, maxLength);
    }
}
