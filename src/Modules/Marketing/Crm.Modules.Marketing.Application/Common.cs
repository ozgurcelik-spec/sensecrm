using System.Text.RegularExpressions;
using Crm.Modules.Identity.Contracts;
using Crm.Modules.Marketing.Domain;
using Crm.Modules.Marketing.Domain.Members;
using Crm.Modules.Sales.Contracts;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Kernel.Results;
using FluentValidation;

namespace Crm.Modules.Marketing.Application;

/// <summary>
/// Kampanya sahibi kuralı: verilmezse mevcut sahip (yoksa çağıran kullanıcı); verilen (ve mevcut sahipten farklı) kullanıcı aktif
/// organizasyonun aktif üyesi olmalıdır (<c>owner.not_member</c>). Mevcut sahip korunurken üyelik yeniden sorgulanmaz (sahibi
/// pasifleşen kampanya düzenlenebilir kalır).
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
            return Error.Validation(MarketingErrors.OwnerNotMember);
        }

        return owner;
    }
}

/// <summary>Marketing <see cref="CampaignMemberType"/> ↔ Sales.Contracts <see cref="RecordType"/> eşlemesi (lead/kişi).</summary>
public static class MemberTypeMapping
{
    public static RecordType ToRecordType(CampaignMemberType type) => type switch
    {
        CampaignMemberType.Lead => RecordType.Lead,
        CampaignMemberType.Contact => RecordType.Contact,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}

/// <summary>Virgülle ayrılmış çoklu enum sorgu parametreleri (<c>status=planned,active</c>): bilinmeyen değer doğrulama hatasıdır.</summary>
public static class EnumListParser
{
    /// <summary>Boş/null → boş liste (filtre yok). Bilinmeyen değer varsa false.</summary>
    public static bool TryParse<TEnum>(string? raw, out IReadOnlyList<TEnum> values)
        where TEnum : struct, Enum
    {
        var parsed = new List<TEnum>();
        values = parsed;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = Enum.GetNames<TEnum>().FirstOrDefault(n => string.Equals(n, token, StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                return false;
            }

            var value = Enum.Parse<TEnum>(name);
            if (!parsed.Contains(value))
            {
                parsed.Add(value);
            }
        }

        return true;
    }

    public static IReadOnlyList<TEnum> ParseOrEmpty<TEnum>(string? raw)
        where TEnum : struct, Enum =>
        TryParse<TEnum>(raw, out var values) ? values : [];
}

/// <summary>Doğrulayıcılarda tekrar eden kurallar. Mesajlar kaynak anahtarıdır (yanıtta yerelleştirilir).</summary>
internal static partial class ValidationRules
{
    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    public static IRuleBuilderOptions<T, string?> ValidCurrency<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(code => string.IsNullOrWhiteSpace(code) || CurrencyPattern().IsMatch(code.Trim().ToUpperInvariant()))
            .WithMessage(MarketingErrors.InvalidCurrency);

    public static IRuleBuilderOptions<T, decimal?> ValidAmount<T>(this IRuleBuilder<T, decimal?> rule) =>
        rule.Must(amount => amount is null or (>= 0 and <= MarketingLimits.MaxAmount))
            .WithMessage(MarketingErrors.InvalidAmount);

    /// <summary><c>memberIds</c>: 1–500 eleman ve boş kimlik yok.</summary>
    public static IRuleBuilderOptions<T, IReadOnlyList<Guid>?> ValidMemberIds<T>(this IRuleBuilder<T, IReadOnlyList<Guid>?> rule) =>
        rule.Must(ids => ids is { Count: >= 1 and <= MarketingLimits.MaxBatchSize })
            .WithMessage(MarketingErrors.InvalidMemberIds)
            .Must(ids => ids is null || ids.All(id => id != Guid.Empty))
            .WithMessage(MarketingErrors.EmptyMemberId);
}
