namespace Sense.Crm.Modules.Integrations.Domain;

/// <summary>Alan uzunlukları ve teknik sınırlar (kalıcılık ve doğrulama ortak kullanır).</summary>
public static class IntegrationsLimits
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;
    public const int UrlMaxLength = 2048;
    public const int HostMaxLength = 255;
    public const int EventTypeMaxLength = 48;
    public const int EventTypesMax = 20;
    public const int KindMaxLength = 12;
    public const int StatusMaxLength = 12;
    public const int FailureReasonMaxLength = 32;
    public const int DisabledReasonMaxLength = 16;
    public const int SecretKeyIdMaxLength = 16;
    public const int SecretHintLength = 4;
    public const int ErrorDetailMaxLength = 500;
    public const int ResponseSnippetMaxLength = 2048;
    public const int ApiKeyPrefixLength = 8;
    public const int ApiKeyHashLength = 32;
    public const int ApiKeyScopesMax = 40;
    public const int IpMaxLength = 64;
}

/// <summary>Modül hata kodları (<c>SharedResource{,.en}.resx</c> anahtarları; web ile sözleşme).</summary>
public static class IntegrationsErrors
{
    public const string WebhookUrlInvalid = "webhook.url_invalid";
    public const string WebhookNameTaken = "webhook.name_taken";
    public const string WebhookDeliveryUnavailable = "webhook.delivery_unavailable";
    public const string WebhookDisabled = "webhook.disabled";
    public const string DeliveryNotRedeliverable = "delivery.not_redeliverable";
    public const string ApiKeyRevoked = "api_key.revoked";

    /// <summary><c>webhook.url_invalid</c> için <c>args.reason</c> anahtarı.</summary>
    public const string ReasonArg = "reason";
    public const string ScopeArg = "scope";
}

/// <summary>Abonelik devre dışı bırakma nedenleri (tel değerleri).</summary>
public static class WebhookDisabledReasons
{
    public const string Manual = "manual";
    public const string Failing = "failing";
}

/// <summary>API anahtarı durumu (okuma anında saatten türetilir).</summary>
public static class ApiKeyStatuses
{
    public const string Active = "active";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
}
