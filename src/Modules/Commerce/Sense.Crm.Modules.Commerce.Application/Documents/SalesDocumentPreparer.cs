using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Modules.Commerce.Domain.PriceBooks;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Commerce.Application;

/// <summary>Satış belgesi yazımı için doğrulanmış/çözülmüş girdiler: sahip, fiyatı çözülmüş kalemler ve (varsa) seçilen fiyat listesi.</summary>
public sealed record PreparedSalesDocument(Guid OwnerUserId, IReadOnlyList<LineInput> Lines, PriceBook? Book);

/// <summary>
/// Teklif, sipariş ve fatura yazımının ortak ön hazırlığı (aynı sıra, aynı hata kodları): sahip → bağlı kayıtlar (yalnız değiştiyse) → fiyat listesi
/// (izin, etkinlik, para birimi) → ürün doğrulaması ve kalem fiyat çözümü → toplam sınırı (yuvarlama dahil; numara ayrılmadan önce).
/// </summary>
public sealed class SalesDocumentPreparer(OwnerResolver owners, RelatedRecordVerifier related, PriceBookSelector priceBooks, DocumentLinePricer pricer)
{
    /// <summary><paramref name="existing"/> yoksa oluşturma; varsa güncelleme (mevcut sahip/bağlar/liste karşılaştırması).</summary>
    public async Task<Result<PreparedSalesDocument>> PrepareAsync(IDocumentFields fields, SalesDocument? existing, IReadOnlySet<Guid> existingProductIds, CancellationToken ct)
    {
        var owner = await owners.ResolveAsync(fields.OwnerUserId, existing?.OwnerUserId, ct).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        if (existing is null || fields.AccountId != existing.AccountId || fields.ContactId != existing.ContactId || fields.DealId != existing.DealId)
        {
            var relatedCheck = await related.VerifyAsync(fields.AccountId, fields.ContactId, fields.DealId, ct).ConfigureAwait(false);
            if (relatedCheck.IsFailure)
            {
                return relatedCheck.Error;
            }
        }

        var selection = await priceBooks.SelectAsync(fields.PriceBookId, existing?.PriceBookId, fields.Currency, existing?.Currency, ct).ConfigureAwait(false);
        if (selection.IsFailure)
        {
            return selection.Error;
        }

        var lines = await pricer.PriceSalesAsync(fields.Currency, fields.Lines, selection.Value.Book, existingProductIds, ct).ConfigureAwait(false);
        var fits = SalesDocument.EnsureTotalsFit(lines, fields.Adjustment).ThrowIfFieldError();
        if (fits.IsFailure)
        {
            return fits.Error;
        }

        return new PreparedSalesDocument(owner.Value, lines, selection.Value.Book);
    }

    public static DocumentHeader HeaderFor(IDocumentFields f, Guid ownerUserId) =>
        new(
            f.Subject,
            f.AccountId,
            f.ContactId,
            f.DealId,
            ownerUserId,
            f.Currency,
            f.Terms,
            f.Notes,
            f.Carrier,
            f.Adjustment,
            f.BillingAddress.ToDomain(),
            f.ShippingAddress.ToDomain(),
            f.PriceBookId);
}
