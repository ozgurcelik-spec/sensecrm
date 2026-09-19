namespace Sense.Crm.Modules.Sales.Domain;

/// <summary>Adres değer nesnesi. Varlıklarda düz kolonlar olarak saklanır (denetim kaydı alan bazında fark üretebilsin diye).</summary>
public sealed record Address(string? Street, string? City, string? State, string? PostalCode, string? Country)
{
    public bool IsEmpty => Street is null && City is null && State is null && PostalCode is null && Country is null;

    /// <summary>Boş alanları null'a çevirir; tüm alanlar boşsa null döner.</summary>
    public static Address? Normalize(Address? address)
    {
        if (address is null)
        {
            return null;
        }

        var normalized = new Address(
            Text.Clean(address.Street, SalesLimits.StreetMaxLength),
            Text.Clean(address.City, SalesLimits.AddressPartMaxLength),
            Text.Clean(address.State, SalesLimits.AddressPartMaxLength),
            Text.Clean(address.PostalCode, SalesLimits.AddressPartMaxLength),
            Text.Clean(address.Country, SalesLimits.AddressPartMaxLength));
        return normalized.IsEmpty ? null : normalized;
    }
}
