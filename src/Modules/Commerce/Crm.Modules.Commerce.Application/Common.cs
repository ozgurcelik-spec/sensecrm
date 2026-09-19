using Crm.Modules.Commerce.Domain;
using Crm.Modules.Commerce.Domain.Documents;
using Crm.Modules.Commerce.Domain.Numbering;
using Crm.Modules.Commerce.Domain.Products;
using Crm.Modules.Identity.Contracts;
using Crm.Modules.Sales.Contracts;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Kernel.Results;
using Crm.Shared.Kernel.ValueObjects;
using FluentValidation;
using FluentValidation.Results;

namespace Crm.Modules.Commerce.Application;

/// <summary>
/// Kayıt sahibi (owner) kuralı: verilmezse mevcut sahip (güncellemede) veya çağıran kullanıcı; verilen (ve mevcut sahipten farklı)
/// kullanıcı aktif organizasyonun aktif üyesi olmalıdır (<c>owner.not_member</c>). Mevcut sahip korunurken üyelik yeniden sorgulanmaz.
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
            return Error.Validation(CommerceErrors.OwnerNotMember);
        }

        return owner;
    }
}

/// <summary>
/// Bağlı kayıt doğrulaması (yalnız <c>Sales.Contracts</c> üzerinden): firma, kişi ve fırsat aktif organizasyonda var olmalı
/// (yok/silinmiş/başka kiracı → <c>commerce.related_not_found</c> 404; varlık sızdırılmaz); kişinin firması varsa belgenin firmasıyla,
/// fırsatın firması belgenin firmasıyla aynı olmalı (400).
/// </summary>
public sealed class RelatedRecordVerifier(IRecordLookup records, IRecordRelationLookup relations)
{
    public async Task<Result> VerifyAsync(Guid accountId, Guid? contactId, Guid? dealId, CancellationToken ct)
    {
        if (!await records.ExistsAsync(RecordType.Account, accountId, ct).ConfigureAwait(false))
        {
            return Error.NotFound(CommerceErrors.RelatedNotFound);
        }

        if (contactId is { } contactKey)
        {
            var contact = await relations.GetContactAsync(contactKey, ct).ConfigureAwait(false);
            if (contact is null)
            {
                return Error.NotFound(CommerceErrors.RelatedNotFound);
            }

            if (contact.AccountId is { } contactAccount && contactAccount != accountId)
            {
                return Error.Validation(CommerceErrors.ContactAccountMismatch);
            }
        }

        if (dealId is { } dealKey)
        {
            var deal = await relations.GetDealAsync(dealKey, ct).ConfigureAwait(false);
            if (deal is null)
            {
                return Error.NotFound(CommerceErrors.RelatedNotFound);
            }

            if (deal.AccountId != accountId)
            {
                return Error.Validation(CommerceErrors.DealAccountMismatch);
            }
        }

        return Result.Success();
    }
}

/// <summary>
/// Kalemlerin ürün bağı doğrulaması: <c>productId</c> kiracıda yok → <c>validation</c> (<c>errors["lines[i].productId"]</c>); pasif ürün
/// belgede <b>daha önce olmayan</b> <c>productId</c> için eklenemez (mevcut kalem korunur); ürün para birimi belge para birimiyle aynı olmalı
/// (kur çevrimi yok). Hatalar <see cref="ValidationException"/> ile (400 + <c>errors</c>) fırlatılır.
/// </summary>
public sealed class LineProductVerifier(IProductLookup products)
{
    public async Task VerifyAsync(string? documentCurrency, IReadOnlyList<LineInput> lines, IReadOnlySet<Guid> existingProductIds, CancellationToken ct)
    {
        var ids = lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var currency = string.IsNullOrWhiteSpace(documentCurrency) ? Product.DefaultCurrency : documentCurrency.Trim().ToUpperInvariant();
        var found = await products.GetAsync(ids, ct).ConfigureAwait(false);
        var failures = new List<ValidationFailure>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].ProductId is not { } productId)
            {
                continue;
            }

            var property = $"Lines[{i}].ProductId";
            if (!found.TryGetValue(productId, out var info))
            {
                failures.Add(new ValidationFailure(property, CommerceErrors.LineProductNotFound));
            }
            else if (!info.IsActive && !existingProductIds.Contains(productId))
            {
                failures.Add(new ValidationFailure(property, CommerceErrors.LineProductInactive));
            }
            else if (!string.Equals(info.Currency, currency, StringComparison.Ordinal))
            {
                failures.Add(new ValidationFailure(property, CommerceErrors.LineProductCurrencyMismatch));
            }
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }
    }
}

/// <summary>
/// Belge numarası üretimi: yıl, oluşturma anında <b>kiracının saat diliminde</b> hesaplanır (<c>TenantCalendarService</c>);
/// sıra <see cref="IDocumentNumberAllocator"/> ile belge INSERT'iyle aynı transaction'da atomik ayrılır.
/// </summary>
public sealed class DocumentNumbers(IDocumentNumberAllocator allocator, TenantCalendarService calendars)
{
    public async Task<string> NextAsync(string kind, CancellationToken ct)
    {
        var calendar = await calendars.GetCalendarAsync(ct).ConfigureAwait(false);
        var year = calendar.Today(calendars.UtcNow).Year;
        var sequence = await allocator.NextAsync(kind, year, ct).ConfigureAwait(false);
        return DocumentNumberFormat.Format(kind, year, sequence);
    }
}

/// <summary>Kiracı takvimine göre "bugün" ve UTC "şimdi" (aynı <see cref="TimeProvider"/>; testler saati sabitleyebilir).</summary>
public sealed class CommerceClock(TenantCalendarService calendars)
{
    public DateTime NowUtc => calendars.UtcNow.UtcDateTime;

    public async Task<DateOnly> TodayAsync(CancellationToken ct)
    {
        var calendar = await calendars.GetCalendarAsync(ct).ConfigureAwait(false);
        return calendar.Today(calendars.UtcNow);
    }
}

/// <summary>Kalem isteklerini domain girdisine çevirir.</summary>
public static class LineMapping
{
    public static IReadOnlyList<LineInput> ToInputs(IReadOnlyList<LineRequest>? lines) =>
        (lines ?? []).Select(l => new LineInput(l.ProductId, (l.Description ?? string.Empty).Trim(), l.Quantity, l.UnitPrice, l.DiscountPercent, l.TaxRate)).ToList();
}

/// <summary>Doğrulayıcılarda tekrar eden kurallar. Mesajlar kaynak anahtarıdır (yanıtta yerelleştirilir).</summary>
internal static class ValidationRules
{
    public static IRuleBuilderOptions<T, string?> OptionalCurrency<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(code => string.IsNullOrWhiteSpace(code) || Currencies.IsKnown(code.Trim().ToUpperInvariant()))
            .WithMessage(CommerceErrors.CurrencyInvalid);

    /// <summary>En çok <paramref name="scale"/> ondalık basamak.</summary>
    public static IRuleBuilderOptions<T, decimal> MaxDecimals<T>(this IRuleBuilder<T, decimal> rule, int scale) =>
        rule.Must(value => decimal.Round(value, scale) == value).WithMessage(CommerceErrors.Decimals);

    public static IRuleBuilderOptions<T, decimal> Percent<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.InclusiveBetween(0m, CommerceLimits.MaxPercent).MaxDecimals(CommerceLimits.PercentScale);

    public static IRuleBuilderOptions<T, decimal> Amount<T>(this IRuleBuilder<T, decimal> rule, decimal max, int scale) =>
        rule.InclusiveBetween(0m, max).MaxDecimals(scale);
}

/// <summary>Kalem kuralları (<c>errors["lines[i].alan"]</c> anahtarları): adet, birim fiyat, iskonto, KDV, açıklama, ürün kimliği.</summary>
public sealed class LineRequestValidator : AbstractValidator<LineRequest>
{
    public LineRequestValidator()
    {
        RuleFor(x => x.Description).NotEmpty().MaximumLength(CommerceLimits.LineDescriptionMaxLength);
        RuleFor(x => x.ProductId).NotEqual(Guid.Empty).When(x => x.ProductId is not null);
        RuleFor(x => x.Quantity).GreaterThan(0m).LessThanOrEqualTo(CommerceLimits.MaxQuantity).MaxDecimals(CommerceLimits.QuantityScale);
        RuleFor(x => x.UnitPrice).Amount(CommerceLimits.MaxUnitPrice, CommerceLimits.UnitPriceScale);
        RuleFor(x => x.DiscountPercent).Percent();
        RuleFor(x => x.TaxRate).Percent();
    }
}

/// <summary>Teklif ve siparişin ortak alanları (oluşturma ve güncelleme ortak doğrulaması).</summary>
public interface IDocumentFields
{
    string Subject { get; }

    Guid AccountId { get; }

    Guid? ContactId { get; }

    Guid? DealId { get; }

    Guid? OwnerUserId { get; }

    string? Currency { get; }

    string? Terms { get; }

    string? Notes { get; }

    IReadOnlyList<LineRequest>? Lines { get; }
}

public abstract class DocumentFieldsValidator<T> : AbstractValidator<T>
    where T : IDocumentFields
{
    protected DocumentFieldsValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(CommerceLimits.SubjectMaxLength);
        RuleFor(x => x.AccountId).NotEmpty();
        RuleFor(x => x.ContactId).NotEqual(Guid.Empty).When(x => x.ContactId is not null);
        RuleFor(x => x.DealId).NotEqual(Guid.Empty).When(x => x.DealId is not null);
        RuleFor(x => x.OwnerUserId).NotEqual(Guid.Empty).When(x => x.OwnerUserId is not null);
        RuleFor(x => x.Currency).OptionalCurrency();
        RuleFor(x => x.Terms).MaximumLength(CommerceLimits.TermsMaxLength);
        RuleFor(x => x.Notes).MaximumLength(CommerceLimits.NotesMaxLength);
        RuleFor(x => x.Lines)
            .Must(lines => lines is null || lines.Count <= CommerceLimits.MaxLines)
            .WithMessage(CommerceErrors.TooManyLines);
        RuleForEach(x => x.Lines).SetValidator(new LineRequestValidator());
    }
}

/// <summary>Geçerlilik tarihi hatasını <c>errors.validUntil</c> olarak sunmak için: domain <c>validation.valid_until_past</c> hatası → 400 + errors.</summary>
public static class DomainValidationBridge
{
    /// <summary>Sonuç <c>validation.valid_until_past</c> ise <c>errors["validUntil"]</c> ile <see cref="ValidationException"/> fırlatır.</summary>
    public static Result ThrowIfValidUntilPast(this Result result)
    {
        if (result.IsFailure && result.Error.Code == CommerceErrors.ValidUntilPast)
        {
            throw new ValidationException([new ValidationFailure("ValidUntil", CommerceErrors.ValidUntilPast)]);
        }

        return result;
    }
}
