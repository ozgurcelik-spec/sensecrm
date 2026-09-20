using FluentValidation;
using FluentValidation.Results;
using Sense.Crm.Modules.Commerce.Contracts;
using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Modules.Commerce.Domain.Numbering;
using Sense.Crm.Modules.Commerce.Domain.PriceBooks;
using Sense.Crm.Modules.Commerce.Domain.Products;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;
using Sense.Crm.Shared.Kernel.ValueObjects;

namespace Sense.Crm.Modules.Commerce.Application;

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

    /// <summary>Satın alma emri bağlantısı: yalnız kişi (firma tutarlılığı aranmaz; kişi kiracıda var olmalı).</summary>
    public async Task<Result> VerifyContactAsync(Guid? contactId, CancellationToken ct)
    {
        if (contactId is { } contactKey && await relations.GetContactAsync(contactKey, ct).ConfigureAwait(false) is null)
        {
            return Error.NotFound(CommerceErrors.RelatedNotFound);
        }

        return Result.Success();
    }
}

/// <summary>
/// Kalemlerin ürün bağı doğrulaması: <c>productId</c> kiracıda yok → <c>validation</c> (<c>errors["lines[i].productId"]</c>); pasif ürün
/// belgede <b>daha önce olmayan</b> <c>productId</c> için eklenemez (mevcut kalem korunur); ürün para birimi belge para birimiyle aynı olmalı
/// (kur çevrimi yok). Hatalar <see cref="ValidationException"/> ile (400 + <c>errors</c>) fırlatılır. Bulunan ürünlerin özeti (fiyat çözümü için) döner.
/// </summary>
public sealed class LineProductVerifier(IProductLookup products)
{
    public async Task<IReadOnlyDictionary<Guid, ProductInfo>> VerifyAsync(
        string? documentCurrency,
        IReadOnlyList<LineInput> lines,
        IReadOnlySet<Guid> existingProductIds,
        CancellationToken ct)
    {
        var ids = lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, ProductInfo>();
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

        return found;
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

/// <summary>Kalem isteklerini (fiyat çözümü <b>öncesi</b>) yer tutucu domain girdisine çevirir; gerçek çözüm <see cref="DocumentLinePricer"/>'dadır.</summary>
public static class LineMapping
{
    /// <summary>Birim fiyatı henüz çözülmemiş kalemler için <c>0</c> yer tutucu (yalnız ürün/para birimi doğrulaması içindir).</summary>
    public static IReadOnlyList<LineInput> ToInputs(IReadOnlyList<LineRequest>? lines) =>
        (lines ?? []).Select(l => new LineInput(l.ProductId, (l.Description ?? string.Empty).Trim(), l.Quantity, l.UnitPrice ?? 0m, l.DiscountPercent, l.TaxRate)).ToList();
}

/// <summary>Adres isteği/yanıtı ↔ domain adresi.</summary>
public static class DocumentAddressMapping
{
    public static DocumentAddress? ToDomain(this DocumentAddressDto? dto) =>
        dto is null ? null : new DocumentAddress(dto.Street, dto.Building, dto.City, dto.State, dto.PostalCode, dto.Country);

    public static DocumentAddressDto? ToDto(this DocumentAddress? address) =>
        address is null ? null : new DocumentAddressDto(address.Street, address.Building, address.City, address.State, address.PostalCode, address.Country);
}

/// <summary>
/// Kalem fiyatı çözümü (m9c-envanter.md "Kalem fiyatı çözümü"): <c>unitPrice</c> verilmişse <b>override</b> (esastır, liste çözümü yapılmaz);
/// yoksa ürünlü satırda satış belgesi için fiyat listesi çözümü (liste yoksa/girdi yoksa katalog), satın alma emri için <c>purchasePrice</c>; ürünsüz satırda hata.
/// Çözüm yazma anında <b>bir kez</b> yapılır (kalemde anlık görüntü). Ürün bağı doğrulaması (<see cref="LineProductVerifier"/>) önce çalışır.
/// </summary>
public sealed class DocumentLinePricer(LineProductVerifier verifier, IPriceResolver resolver)
{
    /// <summary>Satış belgesi (teklif/sipariş/fatura): fiyat listesi (<paramref name="book"/>) veya katalog.</summary>
    public Task<IReadOnlyList<LineInput>> PriceSalesAsync(string? currency, IReadOnlyList<LineRequest>? requests, PriceBook? book, IReadOnlySet<Guid> existingProductIds, CancellationToken ct) =>
        PriceAsync(currency, requests, book, purchase: false, existingProductIds, ct);

    /// <summary>Satın alma emri: <c>product.purchasePrice</c> (tedarikçi tarafı).</summary>
    public Task<IReadOnlyList<LineInput>> PricePurchaseAsync(string? currency, IReadOnlyList<LineRequest>? requests, IReadOnlySet<Guid> existingProductIds, CancellationToken ct) =>
        PriceAsync(currency, requests, null, purchase: true, existingProductIds, ct);

    private async Task<IReadOnlyList<LineInput>> PriceAsync(
        string? currency,
        IReadOnlyList<LineRequest>? requests,
        PriceBook? book,
        bool purchase,
        IReadOnlySet<Guid> existingProductIds,
        CancellationToken ct)
    {
        var placeholders = LineMapping.ToInputs(requests);
        var products = await verifier.VerifyAsync(currency, placeholders, existingProductIds, ct).ConfigureAwait(false);

        var needsPrice = (requests ?? []).Select((r, i) => (Request: r, Index: i)).Where(x => x.Request.UnitPrice is null).ToList();
        var resolved = new Dictionary<Guid, ResolvedPrice>();
        if (!purchase)
        {
            var priced = needsPrice.Where(x => x.Request.ProductId is { } id && products.ContainsKey(id)).Select(x => x.Request.ProductId!.Value).Distinct().ToList();
            if (priced.Count > 0)
            {
                resolved = (await resolver.ResolveAsync(book, priced.ToDictionary(id => id, id => products[id]), ct).ConfigureAwait(false)).ToDictionary(p => p.Key, p => p.Value);
            }
        }

        var failures = new List<ValidationFailure>();
        var result = new List<LineInput>(placeholders.Count);
        for (var i = 0; i < placeholders.Count; i++)
        {
            var line = placeholders[i];
            if ((requests ?? [])[i].UnitPrice is { } explicitPrice)
            {
                result.Add(line with { UnitPrice = explicitPrice });
                continue;
            }

            var property = $"Lines[{i}].UnitPrice";
            if (line.ProductId is not { } productId)
            {
                failures.Add(new ValidationFailure(property, CommerceErrors.LineUnitPriceRequired));
                continue;
            }

            decimal? price = purchase
                ? products[productId].PurchasePrice
                : resolved.TryGetValue(productId, out var r) ? r.UnitPrice : null;
            if (price is not { } unitPrice || unitPrice > CommerceLimits.MaxUnitPrice)
            {
                failures.Add(new ValidationFailure(property, CommerceErrors.LinePriceUnresolvable));
                continue;
            }

            result.Add(line with { UnitPrice = unitPrice });
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }

        return result;
    }
}

/// <summary>Seçilen fiyat listesi (yalnız çözüm için) — <c>Book</c> yoksa katalog fiyatı kullanılır.</summary>
public sealed record PriceBookSelection(PriceBook? Book);

/// <summary>
/// Belgede fiyat listesi başvurusu (teklif/sipariş/fatura): <c>priceBookId</c> <b>verilen veya değiştirilen</b> istekte çağıranın ayrıca
/// <c>crm.pricebooks.read</c> izni olmalıdır (aksi <c>403 forbidden</c>; fiyat listesini okuyamayan kullanıcı kalem fiyatı çözümüyle listeyi sızdıramaz);
/// liste yok/silinmiş/başka kiracı → <c>commerce.related_not_found</c> 404; etkin değil → <c>errors.priceBookId</c> <c>validation.price_book_inactive</c>;
/// para birimi belgeyle farklı → <c>validation.price_book_currency</c>. Değişmeyen mevcut bağ geçerliliğini yitirse belge etkilenmez (yalnız para birimi
/// değiştiyse yeniden denetlenir).
/// </summary>
public sealed class PriceBookSelector(IPriceBookRepository books, IPermissionService permissions, ICurrentUser user, CommerceClock clock)
{
    public async Task<Result<PriceBookSelection>> SelectAsync(Guid? requestedId, Guid? currentId, string? documentCurrency, string? currentCurrency, CancellationToken ct)
    {
        if (requestedId is not { } id)
        {
            return new PriceBookSelection(null);
        }

        var currency = Normalize(documentCurrency);
        var changed = id != currentId;
        if (changed && !await CanReadAsync(ct).ConfigureAwait(false))
        {
            return Error.Forbidden(ErrorCodes.Forbidden);
        }

        var book = await books.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (book is null)
        {
            return changed ? Error.NotFound(CommerceErrors.RelatedNotFound) : new PriceBookSelection(null);
        }

        if (changed)
        {
            var today = await clock.TodayAsync(ct).ConfigureAwait(false);
            if (!book.IsEffective(today))
            {
                throw new ValidationException([new ValidationFailure("PriceBookId", CommerceErrors.PriceBookInactive)]);
            }
        }

        if ((changed || !string.Equals(currency, Normalize(currentCurrency), StringComparison.Ordinal)) && !string.Equals(book.Currency, currency, StringComparison.Ordinal))
        {
            throw new ValidationException([new ValidationFailure("PriceBookId", CommerceErrors.PriceBookCurrency)]);
        }

        return new PriceBookSelection(book);
    }

    private async Task<bool> CanReadAsync(CancellationToken ct) =>
        user.UserId is { } userId && await permissions.HasAsync(userId, CommercePermissions.PriceBooksRead, ct).ConfigureAwait(false);

    private static string Normalize(string? currency) => string.IsNullOrWhiteSpace(currency) ? Product.DefaultCurrency : currency.Trim().ToUpperInvariant();
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

    /// <summary>İsteğe bağlı tutar: doluysa 0…<paramref name="max"/> ve en çok <paramref name="scale"/> ondalık.</summary>
    public static IRuleBuilderOptions<T, decimal?> OptionalAmount<T>(this IRuleBuilder<T, decimal?> rule, decimal max, int scale) =>
        rule.InclusiveBetween(0m, max)
            .Must(value => value is null || decimal.Round(value.Value, scale) == value.Value)
            .WithMessage(CommerceErrors.Decimals);

    /// <summary>Web sitesi: boş olabilir; doluysa mutlak <c>http://</c> veya <c>https://</c> adresi (Sales ile aynı kural).</summary>
    public static IRuleBuilderOptions<T, string?> OptionalHttpUrl<T>(this IRuleBuilder<T, string?> rule, int maxLength) =>
        rule.MaximumLength(maxLength)
            .Must(url => string.IsNullOrWhiteSpace(url) || IsHttpUrl(url))
            .WithMessage(InvalidWebsite);

    public static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);

    public const string InvalidWebsite = "validation.website";
    public const string InvalidEmail = "validation.email";

    public static IRuleBuilderOptions<T, string?> OptionalEmail<T>(this IRuleBuilder<T, string?> rule, int maxLength) =>
        rule.MaximumLength(maxLength)
            .Must(email => string.IsNullOrWhiteSpace(email) || EmailAddress.IsValid(email))
            .WithMessage(InvalidEmail);
}

/// <summary>Kalem kuralları (<c>errors["lines[i].alan"]</c> anahtarları): adet, birim fiyat (isteğe bağlı), iskonto, KDV, açıklama, ürün kimliği.</summary>
public sealed class LineRequestValidator : AbstractValidator<LineRequest>
{
    public LineRequestValidator()
    {
        RuleFor(x => x.Description).NotEmpty().MaximumLength(CommerceLimits.LineDescriptionMaxLength);
        RuleFor(x => x.ProductId).NotEqual(Guid.Empty).When(x => x.ProductId is not null);
        RuleFor(x => x.Quantity).GreaterThan(0m).LessThanOrEqualTo(CommerceLimits.MaxQuantity).MaxDecimals(CommerceLimits.QuantityScale);
        RuleFor(x => x.UnitPrice).OptionalAmount(CommerceLimits.MaxUnitPrice, CommerceLimits.UnitPriceScale);
        RuleFor(x => x.DiscountPercent).Percent();
        RuleFor(x => x.TaxRate).Percent();
    }
}

/// <summary>Adres bloğu kuralları (<c>errors["billingAddress.city"]</c> …): <c>street</c> ≤ 200, diğerleri ≤ 100.</summary>
public sealed class DocumentAddressValidator : AbstractValidator<DocumentAddressDto>
{
    public DocumentAddressValidator()
    {
        RuleFor(x => x.Street).MaximumLength(CommerceLimits.StreetMaxLength);
        RuleFor(x => x.Building).MaximumLength(CommerceLimits.AddressPartMaxLength);
        RuleFor(x => x.City).MaximumLength(CommerceLimits.AddressPartMaxLength);
        RuleFor(x => x.State).MaximumLength(CommerceLimits.AddressPartMaxLength);
        RuleFor(x => x.PostalCode).MaximumLength(CommerceLimits.AddressPartMaxLength);
        RuleFor(x => x.Country).MaximumLength(CommerceLimits.AddressPartMaxLength);
    }
}

/// <summary>Tüm ticaret belgelerinin (teklif, sipariş, fatura, satın alma emri) ortak alanları.</summary>
public interface ICommonDocumentFields
{
    string Subject { get; }

    Guid? ContactId { get; }

    Guid? OwnerUserId { get; }

    string? Currency { get; }

    string? Terms { get; }

    string? Notes { get; }

    string? Carrier { get; }

    decimal Adjustment { get; }

    DocumentAddressDto? BillingAddress { get; }

    DocumentAddressDto? ShippingAddress { get; }

    IReadOnlyList<LineRequest>? Lines { get; }
}

/// <summary>Satış belgelerinin (teklif, sipariş, fatura) ortak alanları (oluşturma ve güncelleme ortak doğrulaması).</summary>
public interface IDocumentFields : ICommonDocumentFields
{
    Guid AccountId { get; }

    Guid? DealId { get; }

    Guid? PriceBookId { get; }
}

public abstract class CommonDocumentFieldsValidator<T> : AbstractValidator<T>
    where T : ICommonDocumentFields
{
    protected CommonDocumentFieldsValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(CommerceLimits.SubjectMaxLength);
        RuleFor(x => x.ContactId).NotEqual(Guid.Empty).When(x => x.ContactId is not null);
        RuleFor(x => x.OwnerUserId).NotEqual(Guid.Empty).When(x => x.OwnerUserId is not null);
        RuleFor(x => x.Currency).OptionalCurrency();
        RuleFor(x => x.Terms).MaximumLength(CommerceLimits.TermsMaxLength);
        RuleFor(x => x.Notes).MaximumLength(CommerceLimits.NotesMaxLength);
        RuleFor(x => x.Carrier).MaximumLength(CommerceLimits.CarrierMaxLength);
        RuleFor(x => x.Adjustment).InclusiveBetween(-CommerceLimits.MaxAdjustment, CommerceLimits.MaxAdjustment).MaxDecimals(CommerceLimits.AmountScale);
        RuleFor(x => x.BillingAddress!).SetValidator(new DocumentAddressValidator()).When(x => x.BillingAddress is not null);
        RuleFor(x => x.ShippingAddress!).SetValidator(new DocumentAddressValidator()).When(x => x.ShippingAddress is not null);
        RuleFor(x => x.Lines)
            .Must(lines => lines is null || lines.Count <= CommerceLimits.MaxLines)
            .WithMessage(CommerceErrors.TooManyLines);
        RuleForEach(x => x.Lines).SetValidator(new LineRequestValidator());
    }
}

public abstract class DocumentFieldsValidator<T> : CommonDocumentFieldsValidator<T>
    where T : IDocumentFields
{
    protected DocumentFieldsValidator()
    {
        RuleFor(x => x.AccountId).NotEmpty();
        RuleFor(x => x.DealId).NotEqual(Guid.Empty).When(x => x.DealId is not null);
        RuleFor(x => x.PriceBookId).NotEqual(Guid.Empty).When(x => x.PriceBookId is not null);
    }
}

/// <summary>
/// Alan kuralı olan domain hatalarını <c>errors.alan</c> olarak sunmak için: <c>validation.valid_until_past</c> → <c>errors.validUntil</c>,
/// <c>validation.adjustment_*</c> → <c>errors.adjustment</c>, <c>validation.due_before_*</c> → <c>errors.dueDate</c>,
/// <c>validation.paid_*</c> → <c>errors.paidOn</c> (400 + <c>errors</c>). Diğer hatalar olduğu gibi döner.
/// </summary>
public static class DomainValidationBridge
{
    private static readonly IReadOnlyDictionary<string, string> Properties = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [CommerceErrors.ValidUntilPast] = "ValidUntil",
        [CommerceErrors.AdjustmentNegativeTotal] = "Adjustment",
        [CommerceErrors.AdjustmentRequiresLines] = "Adjustment",
        [CommerceErrors.DueBeforeOrder] = "DueDate",
        [CommerceErrors.DueBeforeInvoice] = "DueDate",
        [CommerceErrors.DueBeforePo] = "DueDate",
        [CommerceErrors.PaidInFuture] = "PaidOn",
        [CommerceErrors.PaidBeforeInvoice] = "PaidOn",
    };

    /// <summary>Sonuç alan kuralı hatasıysa <see cref="ValidationException"/> fırlatır; aksi sonucu aynen döner.</summary>
    public static TResult ThrowIfFieldError<TResult>(this TResult result)
        where TResult : Result
    {
        if (result.IsFailure && Properties.TryGetValue(result.Error.Code, out var property))
        {
            throw new ValidationException([new ValidationFailure(property, result.Error.Code)]);
        }

        return result;
    }

    /// <summary>Geriye uyumlu ad (M6A): <see cref="ThrowIfFieldError{TResult}"/> ile aynı.</summary>
    public static Result ThrowIfValidUntilPast(this Result result) => result.ThrowIfFieldError();
}
