using System.Globalization;
using System.Text;
using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Modules.Identity.Domain.Tenants;

/// <summary>
/// Kiracı = organizasyon (şirket, K1). Kullanıcılar organizasyona <see cref="Memberships.Membership"/> ile bağlanır;
/// bir kullanıcı birden çok organizasyonun üyesi olabilir. Kiracı verisi satır bazında <c>TenantId</c> ile ayrılır (K2).
/// </summary>
public sealed class Tenant : AggregateRoot<Guid>, IAuditLogged
{
    private Tenant()
    {
    }

    private Tenant(Guid id, string name, string slug, string defaultLocale, string timeZone) : base(id)
    {
        Name = name;
        Slug = slug;
        DefaultLocale = defaultLocale;
        TimeZone = timeZone;
        IsActive = true;
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>URL/görünen kısa ad; organizasyon adından türetilir, küresel olarak benzersizdir.</summary>
    public string Slug { get; private set; } = string.Empty;

    /// <summary>Organizasyonun varsayılan dili (tr | en, K8); yeni üyelerin dil tercihi buradan başlar.</summary>
    public string DefaultLocale { get; private set; } = string.Empty;

    /// <summary>IANA saat dilimi (varsayılan Europe/Istanbul).</summary>
    public string TimeZone { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public static Tenant Create(string name, string slug, string defaultLocale, string timeZone)
    {
        var tenant = new Tenant(
            Guid.CreateVersion7(),
            Guard.MaxLength(Guard.NotEmpty(name), IdentityLimits.OrganizationNameMaxLength),
            Guard.NotEmpty(slug),
            Guard.NotEmpty(defaultLocale),
            Guard.NotEmpty(timeZone));
        tenant.Raise(new TenantCreated(tenant.Id, tenant.Slug));
        return tenant;
    }

    public void Update(string name, string defaultLocale, string timeZone)
    {
        Name = Guard.MaxLength(Guard.NotEmpty(name), IdentityLimits.OrganizationNameMaxLength);
        DefaultLocale = Guard.NotEmpty(defaultLocale);
        TimeZone = Guard.NotEmpty(timeZone);
    }

    /// <summary>
    /// Organizasyon adından slug üretir: Türkçe karakterler ASCII'ye çevrilir, harf/rakam dışı karakterler '-' olur.
    /// Benzersizlik çağıranın sorumluluğundadır (çakışmada sonek eklenir).
    /// </summary>
    public static string SlugFrom(string name)
    {
        var builder = new StringBuilder(name.Length);
        var lastWasSeparator = true;
        foreach (var ch in Transliterate(name.Trim()).ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch))
            {
                builder.Append(ch);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                builder.Append(IdentityLimits.SlugSeparator);
                lastWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim(IdentityLimits.SlugSeparator);
        if (slug.Length > IdentityLimits.SlugMaxLength - 5)
        {
            slug = slug[..(IdentityLimits.SlugMaxLength - 5)].Trim(IdentityLimits.SlugSeparator);
        }

        return slug.Length < IdentityLimits.SlugMinLength ? string.Concat("org-", slug).Trim(IdentityLimits.SlugSeparator) : slug;
    }

    private static string Transliterate(string value)
    {
        var map = new Dictionary<char, string>
        {
            ['ç'] = "c",
            ['Ç'] = "c",
            ['ğ'] = "g",
            ['Ğ'] = "g",
            ['ı'] = "i",
            ['İ'] = "i",
            ['ö'] = "o",
            ['Ö'] = "o",
            ['ş'] = "s",
            ['Ş'] = "s",
            ['ü'] = "u",
            ['Ü'] = "u",
        };

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormC))
        {
            if (map.TryGetValue(ch, out var replacement))
            {
                builder.Append(replacement);
                continue;
            }

            var decomposed = ch.ToString().Normalize(NormalizationForm.FormD);
            foreach (var part in decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
            {
                builder.Append(part);
            }
        }

        return builder.ToString();
    }
}

public sealed record TenantCreated(Guid TenantId, string Slug) : DomainEvent;
