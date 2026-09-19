namespace Crm.Modules.Marketing.Domain;

/// <summary>
/// Marketing hata kodları (= web istemcisiyle sözleşme + yerelleştirme anahtarları, docs/plan/m6c-pazarlama.md).
/// Ortak kodlar (validation, forbidden, not_found) <c>Crm.Shared.Kernel.Results.ErrorCodes</c>'tadır.
/// </summary>
public static class MarketingErrors
{
    /// <summary><c>endDate &lt; startDate</c> → 400 (<c>errors.endDate</c>).</summary>
    public const string InvalidDateRange = "campaign.invalid_date_range";

    /// <summary>Durum geçiş tablosunda olmayan değişiklik → 409 (<c>from</c>/<c>to</c> argümanları).</summary>
    public const string InvalidStatusTransition = "campaign.invalid_status_transition";

    /// <summary><c>completed</c>/<c>cancelled</c> kampanyaya üye ekleme → 409.</summary>
    public const string CampaignClosed = "campaign.closed";

    /// <summary>Sahip organizasyonun aktif üyesi değil → 400 (Sales/Activities ile aynı anahtar).</summary>
    public const string OwnerNotMember = "owner.not_member";

    /// <summary>Oluştururken durum yalnız <c>planned</c> veya <c>active</c> olabilir (doğrulama mesajı anahtarı).</summary>
    public const string InvalidCreateStatus = "validation.campaign_create_status";

    /// <summary><c>converted</c> üye durumu elle ayarlanamaz (doğrulama mesajı anahtarı).</summary>
    public const string ManualConvertedStatus = "validation.campaign_member_status";

    /// <summary>Kampanya türü bilinmiyor (doğrulama mesajı anahtarı).</summary>
    public const string InvalidType = "validation.campaign_type";

    /// <summary>Kampanya durumu bilinmiyor (doğrulama mesajı anahtarı).</summary>
    public const string InvalidStatus = "validation.campaign_status";

    /// <summary>Üye durumu bilinmiyor (doğrulama mesajı anahtarı).</summary>
    public const string InvalidMemberStatus = "validation.member_status";

    /// <summary><c>memberIds</c> 1–500 eleman olmalı (doğrulama mesajı anahtarı).</summary>
    public const string InvalidMemberIds = "validation.member_ids";

    /// <summary>Boş kimlik / bilinmeyen kimlik biçimi (doğrulama mesajı anahtarı).</summary>
    public const string EmptyMemberId = "validation.member_id";

    /// <summary>Para birimi 3 büyük harf değil (doğrulama mesajı anahtarı; Sales ile ortak).</summary>
    public const string InvalidCurrency = "validation.currency";

    /// <summary>Tutar negatif veya sınırın üstünde (doğrulama mesajı anahtarı).</summary>
    public const string InvalidAmount = "validation.campaign_amount";

    /// <summary>Tarih aralığı ters/aşırı uzun (rapor; <c>activities</c> ile aynı anahtar).</summary>
    public const string InvalidReportRange = "validation.date_range";

    /// <summary>Toplu ekleme sonucunda atlanan kayıt nedenleri (yanıt <c>skipped[].reason</c>).</summary>
    public static class SkipReasons
    {
        public const string NotFound = "not_found";
        public const string LeadConverted = "lead_converted";
    }
}

/// <summary>Domain sınırları (uzunluklar vb.). Ayar değil, veri modeli kısıtı.</summary>
public static class MarketingLimits
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 4000;
    public const int EnumColumnMaxLength = 20;
    public const int CurrencyLength = 3;
    public const string DefaultCurrency = "TRY";
    public const decimal MaxAmount = 999_999_999_999m;

    /// <summary>Toplu üye işlemlerinde tek çağrıdaki en çok kimlik.</summary>
    public const int MaxBatchSize = 500;

    /// <summary><c>by-member</c> yanıtındaki en çok satır.</summary>
    public const int MaxRecordCampaigns = 200;

    /// <summary>Rapordaki en çok kampanya (<c>topCampaigns</c>).</summary>
    public const int TopCampaignCount = 10;
}
