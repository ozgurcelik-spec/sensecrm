namespace Sense.Crm.Modules.Commerce.Domain.Documents;

/// <summary>
/// Belge adres bloğu (faturalama / teslimat; Zoho "Adres Bilgileri"). Sales <c>Address</c>'i ile alan adları uyumludur, ek olarak
/// <see cref="Building"/> (daire / ev no / bina / apartman adı) vardır. Belgelerde düz kolonlar olarak saklanır (denetim kaydı alan bazında fark
/// üretebilsin diye); koordinat alanı yoktur. Tüm alanlar boşsa blok <c>null</c> saklanır (<see cref="Normalize"/>).
/// </summary>
public sealed record DocumentAddress(string? Street, string? Building, string? City, string? State, string? PostalCode, string? Country)
{
    public bool IsEmpty => Street is null && Building is null && City is null && State is null && PostalCode is null && Country is null;

    /// <summary>Alanları kırpar (boş → null, uzunluk sınırı aşılırsa <see cref="ArgumentException"/>); tüm alanlar boşsa null döner.</summary>
    public static DocumentAddress? Normalize(DocumentAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        var normalized = new DocumentAddress(
            Clean(address.Street, CommerceLimits.StreetMaxLength),
            Clean(address.Building, CommerceLimits.AddressPartMaxLength),
            Clean(address.City, CommerceLimits.AddressPartMaxLength),
            Clean(address.State, CommerceLimits.AddressPartMaxLength),
            Clean(address.PostalCode, CommerceLimits.AddressPartMaxLength),
            Clean(address.Country, CommerceLimits.AddressPartMaxLength));
        return normalized.IsEmpty ? null : normalized;
    }

    internal static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : Sense.Crm.Shared.Kernel.Guard.MaxLength(trimmed, maxLength);
    }
}
