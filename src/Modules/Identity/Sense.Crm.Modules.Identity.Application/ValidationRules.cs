using FluentValidation;
using Sense.Crm.Modules.Identity.Domain;
using Sense.Crm.Shared.Contracts.Configuration;

namespace Sense.Crm.Modules.Identity.Application;

/// <summary>Identity doğrulayıcılarında tekrar eden kurallar. Mesajlar kaynak anahtarıdır (yanıtta yerelleştirilir).</summary>
internal static class ValidationRules
{
    public static IRuleBuilderOptions<T, string?> SupportedLocale<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(Cultures.IsSupportedLanguage).WithMessage(IdentityErrors.InvalidLocale);

    public static IRuleBuilderOptions<T, string?> ValidTimeZone<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(tz => !string.IsNullOrWhiteSpace(tz) && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)).WithMessage(IdentityErrors.InvalidTimeZone);

    public static IRuleBuilderOptions<T, string?> Email<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().MaximumLength(IdentityLimits.EmailMaxLength).Must(Sense.Crm.Shared.Kernel.ValueObjects.EmailAddress.IsValid).WithMessage(InvalidEmail);

    public const string InvalidEmail = "validation.email";
}
