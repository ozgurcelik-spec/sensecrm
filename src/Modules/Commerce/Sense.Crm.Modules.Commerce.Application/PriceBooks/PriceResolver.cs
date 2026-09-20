using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.PriceBooks;

namespace Sense.Crm.Modules.Commerce.Application;

/// <summary>
/// Fiyat çözücü (tek sınıf): belge yazma yolu ve <c>POST /pricebooks/{id}/resolve</c> aynı kodu kullanır (web önizlemesi ile sunucu sonucu aynıdır).
/// Liste yoksa (<c>null</c>) her ürün katalog fiyatıyla döner. Etkinlik denetimi çağıranındır (yazma yolu seçimde, resolve ucu <c>pricebook.not_effective</c> ile).
/// </summary>
public interface IPriceResolver
{
    Task<IReadOnlyDictionary<Guid, ResolvedPrice>> ResolveAsync(PriceBook? book, IReadOnlyDictionary<Guid, ProductInfo> products, CancellationToken ct);
}

public sealed class PriceResolver(IPriceBookRepository books) : IPriceResolver
{
    public async Task<IReadOnlyDictionary<Guid, ResolvedPrice>> ResolveAsync(PriceBook? book, IReadOnlyDictionary<Guid, ProductInfo> products, CancellationToken ct)
    {
        var entries = book is { PricingModel: PricingModel.PerProduct } && products.Count > 0
            ? await books.GetEntryPricesAsync(book.Id, products.Keys.ToList(), ct).ConfigureAwait(false)
            : new Dictionary<Guid, decimal>();

        var result = new Dictionary<Guid, ResolvedPrice>(products.Count);
        foreach (var (productId, info) in products)
        {
            result[productId] = book is null
                ? new ResolvedPrice(info.UnitPrice, PriceSource.Catalog)
                : book.Resolve(info.Currency, info.UnitPrice, entries.TryGetValue(productId, out var entry) ? entry : null);
        }

        return result;
    }
}
