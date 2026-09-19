using Crm.Modules.Identity.Contracts;
using Crm.Modules.Sales.Domain;
using Crm.Modules.Sales.Domain.Pipelines;
using Crm.Shared.Contracts.Configuration;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Kernel.Results;
using Crm.Shared.Kernel.ValueObjects;
using FluentValidation;

namespace Crm.Modules.Sales.Application;

/// <summary>
/// Kayıt sahibi (owner) kuralı: verilmezse çağıran kullanıcı; verilen (ve mevcut sahipten farklı) kullanıcı aktif organizasyonun
/// aktif üyesi olmalıdır (<c>owner.not_member</c>). Mevcut sahip korunurken üyelik yeniden sorgulanmaz (eski sahibi pasifleşen
/// kayıtlar düzenlenebilir kalır).
/// </summary>
public sealed class OwnerResolver(IMemberLookup members, ICurrentUser user)
{
    public async Task<Result<Guid>> ResolveAsync(Guid? requested, Guid? current, CancellationToken ct)
    {
        var target = requested ?? current ?? user.UserId;
        if (target is not { } owner)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        if (requested is { } candidate && candidate != current && !await members.IsActiveMemberAsync(candidate, ct).ConfigureAwait(false))
        {
            return Error.Validation(SalesErrors.OwnerNotMember);
        }

        return owner;
    }
}

/// <summary>
/// Varsayılan huniyi çözer; organizasyonda hiç huni yoksa (kayıt olayı henüz işlenmemiş olabilir) tembel olarak tohumlar,
/// böylece kayıttan hemen sonraki ilk istek de çalışır.
/// </summary>
public sealed class DefaultPipelineResolver(IPipelineRepository pipelines, IDefaultPipelineSeeder seeder, ITenantDirectory directory, ITenantContext tenant)
{
    public async Task<Pipeline?> GetDefaultAsync(CancellationToken ct)
    {
        if (await pipelines.GetDefaultAsync(ct).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        await EnsureSeededAsync(ct).ConfigureAwait(false);
        return await pipelines.GetDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task EnsureSeededAsync(CancellationToken ct)
    {
        if (await pipelines.AnyAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var info = await directory.FindAsync(tenant.TenantId, ct).ConfigureAwait(false);
        await seeder.EnsureAsync(tenant.TenantId, info?.DefaultLocale ?? Cultures.TurkishLanguage, ct).ConfigureAwait(false);
    }
}

/// <summary>Adres girdisi ↔ domain adresi.</summary>
internal static class AddressMapping
{
    public static Address? ToDomain(this AddressDto? dto) => dto is null ? null : new Address(dto.Street, dto.City, dto.State, dto.PostalCode, dto.Country);
}

/// <summary>Doğrulayıcılarda tekrar eden kurallar. Mesajlar kaynak anahtarıdır (yanıtta yerelleştirilir).</summary>
internal static class ValidationRules
{
    public const string InvalidEmail = "validation.email";
    public const string InvalidCurrency = "validation.currency";

    public static IRuleBuilderOptions<T, string?> Required<T>(this IRuleBuilder<T, string?> rule, int maxLength) =>
        rule.NotEmpty().MaximumLength(maxLength);

    public static IRuleBuilderOptions<T, string?> Optional<T>(this IRuleBuilder<T, string?> rule, int maxLength) =>
        rule.MaximumLength(maxLength);

    /// <summary>
    /// Web sitesi (L7): boş olabilir; doluysa mutlak <c>http://</c> veya <c>https://</c> adresi olmalıdır (<c>javascript:</c>,
    /// <c>data:</c>, çıplak host vb. reddedilir; arayüz bağlantıyı doğrudan <c>href</c> yapar). Yalnız yazmada doğrulanır; eski
    /// kayıtlar okunurken hata vermez.
    /// </summary>
    public static IRuleBuilderOptions<T, string?> OptionalHttpUrl<T>(this IRuleBuilder<T, string?> rule, int maxLength) =>
        rule.MaximumLength(maxLength)
            .Must(url => string.IsNullOrWhiteSpace(url) || IsHttpUrl(url))
            .WithMessage(InvalidWebsite);

    public static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);

    public const string InvalidWebsite = "validation.website";

    public static IRuleBuilderOptions<T, string?> OptionalEmail<T>(this IRuleBuilder<T, string?> rule) =>
        rule.MaximumLength(SalesLimits.EmailMaxLength)
            .Must(email => string.IsNullOrWhiteSpace(email) || EmailAddress.IsValid(email))
            .WithMessage(InvalidEmail);

    public static IRuleBuilderOptions<T, string?> OptionalCurrency<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(code => string.IsNullOrWhiteSpace(code) || Currencies.IsKnown(code.Trim().ToUpperInvariant()))
            .WithMessage(InvalidCurrency);

    public static IRuleBuilderOptions<T, decimal?> OptionalAmount<T>(this IRuleBuilder<T, decimal?> rule) =>
        rule.GreaterThanOrEqualTo(0m).LessThanOrEqualTo(SalesLimits.MaxAmount);
}

public sealed class AddressValidator : AbstractValidator<AddressDto>
{
    public AddressValidator()
    {
        RuleFor(x => x.Street).Optional(SalesLimits.StreetMaxLength);
        RuleFor(x => x.City).Optional(SalesLimits.AddressPartMaxLength);
        RuleFor(x => x.State).Optional(SalesLimits.AddressPartMaxLength);
        RuleFor(x => x.PostalCode).Optional(SalesLimits.AddressPartMaxLength);
        RuleFor(x => x.Country).Optional(SalesLimits.AddressPartMaxLength);
    }
}
