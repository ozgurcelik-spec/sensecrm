using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Modules.Integrations.Domain.Webhooks;

/// <summary>Şifreli saklanan webhook sırrı (AES-256-GCM; anahtar kimliği + AAD = kiracı‖abonelik‖sürüm). Ham değer domain'e asla girmez.</summary>
public sealed record SealedSecret(byte[] Cipher, string KeyId, string Last4);

/// <summary>
/// Giden webhook aboneliği: HTTPS hedefi, olay türü süzgeci, şifreli imza sırrı (D7) ve sağlık sayaçları. <c>url</c>, <c>secret_enc</c>,
/// <c>previous_secret_enc</c> denetimde maskelenir (host görünür). Sayaçlar (<c>consecutive_failures</c>, <c>last_*</c>) teslimat hattında
/// <c>ExecuteUpdate</c> ile yazılır (her teslimat için denetim satırı üretmemek için); yalnız otomatik pasifleştirme izlenen bir güncellemedir.
/// </summary>
public sealed class WebhookSubscription : TenantAggregateRoot<Guid>, IAuditLogged
{
    private WebhookSubscription()
    {
    }

    private WebhookSubscription(Guid id, Guid tenantId) : base(id, tenantId)
    {
    }

    public static IReadOnlySet<string> SensitiveFields { get; } =
        new HashSet<string>([nameof(Url), nameof(SecretEnc), nameof(PreviousSecretEnc)], StringComparer.Ordinal);

    public string Name { get; private set; } = string.Empty;

    public string Url { get; private set; } = string.Empty;

    /// <summary>Türetilmiş ana bilgisayar (punycode, küçük harf); denetimde/günlükte görünür, URL görünmez.</summary>
    public string Host { get; private set; } = string.Empty;

    public List<string> EventTypes { get; private set; } = [];

    public bool Enabled { get; private set; }

    public string? DisabledReason { get; private set; }

    public DateTime? DisabledAt { get; private set; }

    public string? Description { get; private set; }

    public byte[] SecretEnc { get; private set; } = [];

    public string SecretKeyId { get; private set; } = string.Empty;

    public int SecretVersion { get; private set; }

    public string SecretLast4 { get; private set; } = string.Empty;

    public byte[]? PreviousSecretEnc { get; private set; }

    public string? PreviousSecretKeyId { get; private set; }

    public DateTime? PreviousSecretExpiresAt { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public DateTime? LastSuccessAt { get; private set; }

    public DateTime? LastFailureAt { get; private set; }

    public static WebhookSubscription Create(
        Guid id,
        Guid tenantId,
        string name,
        string url,
        string host,
        IEnumerable<string> eventTypes,
        string? description,
        bool enabled,
        SealedSecret secret)
    {
        var subscription = new WebhookSubscription(id, tenantId)
        {
            SecretEnc = secret.Cipher,
            SecretKeyId = Guard.MaxLength(secret.KeyId, IntegrationsLimits.SecretKeyIdMaxLength),
            SecretVersion = 1,
            SecretLast4 = secret.Last4,
        };
        subscription.Apply(name, url, host, eventTypes, description);
        subscription.Enabled = enabled;
        if (!enabled)
        {
            subscription.DisabledReason = WebhookDisabledReasons.Manual;
        }

        return subscription;
    }

    /// <summary>Tam değiştirme (PUT); <paramref name="enabled"/> true'ya dönüşte sayaç ve neden sıfırlanır.</summary>
    public void Update(string name, string url, string host, IEnumerable<string> eventTypes, string? description, bool enabled, DateTime now)
    {
        Apply(name, url, host, eventTypes, description);
        if (enabled)
        {
            Enable();
        }
        else if (Enabled)
        {
            Disable(now);
        }
    }

    public void Enable()
    {
        Enabled = true;
        DisabledReason = null;
        DisabledAt = null;
        ConsecutiveFailures = 0;
    }

    public void Disable(DateTime now)
    {
        if (!Enabled)
        {
            return;
        }

        Enabled = false;
        DisabledReason = WebhookDisabledReasons.Manual;
        DisabledAt = now;
    }

    /// <summary>Art arda hata eşiği aşıldı: abonelik pasifleşir (<c>failing</c>); bekleyen teslimatlar sürer, yalnız yeni fan-out durur.</summary>
    public void AutoDisable(DateTime now)
    {
        if (!Enabled)
        {
            return;
        }

        Enabled = false;
        DisabledReason = WebhookDisabledReasons.Failing;
        DisabledAt = now;
    }

    /// <summary>
    /// Sır döndürme: yeni sır <c>secret_version + 1</c> ile hemen etkin; mevcut sır <paramref name="graceHours"/> boyunca <c>previous</c> olarak da geçerlidir
    /// (0 → hemen silinir). Grace içinde tekrar döndürme <c>previous</c>'ı mevcut sırla <b>değiştirir</b> (en çok bir önceki sır). Yeni sırrın şifrelemesi
    /// bu yöntemden dönen <see cref="NextSecretVersion"/> AAD'ıyla yapılmış olmalıdır.
    /// </summary>
    public void RotateSecret(SealedSecret next, int graceHours, DateTime now)
    {
        if (graceHours > 0)
        {
            PreviousSecretEnc = SecretEnc;
            PreviousSecretKeyId = SecretKeyId;
            PreviousSecretExpiresAt = now.AddHours(graceHours);
        }
        else
        {
            ClearPreviousSecret();
        }

        SecretEnc = next.Cipher;
        SecretKeyId = Guard.MaxLength(next.KeyId, IntegrationsLimits.SecretKeyIdMaxLength);
        SecretLast4 = next.Last4;
        SecretVersion += 1;
    }

    /// <summary>Bir sonraki sırrın AAD sürümü (<c>secret_version + 1</c>).</summary>
    public int NextSecretVersion => SecretVersion + 1;

    /// <summary>Önceki sırrın AAD sürümü (her zaman geçerli sürümden bir eksik).</summary>
    public int PreviousSecretVersion => SecretVersion - 1;

    public bool HasActivePreviousSecret(DateTime now) => PreviousSecretEnc is not null && PreviousSecretExpiresAt is { } at && at > now;

    public void ClearPreviousSecret()
    {
        PreviousSecretEnc = null;
        PreviousSecretKeyId = null;
        PreviousSecretExpiresAt = null;
    }

    /// <summary>Anahtar döndürme (Migrator): aynı sürüm/AAD ile yeniden şifrelenmiş blob.</summary>
    public void ReplaceSealedSecrets(SealedSecret current, SealedSecret? previous)
    {
        SecretEnc = current.Cipher;
        SecretKeyId = Guard.MaxLength(current.KeyId, IntegrationsLimits.SecretKeyIdMaxLength);
        if (previous is not null)
        {
            PreviousSecretEnc = previous.Cipher;
            PreviousSecretKeyId = Guard.MaxLength(previous.KeyId, IntegrationsLimits.SecretKeyIdMaxLength);
        }
    }

    /// <summary>Okuma anında sağlık: <c>disabled | failing | degraded | healthy</c> (tel değerleri).</summary>
    public static string HealthOf(bool enabled, int consecutiveFailures, DateTime? lastFailureAt, DateTime? lastSuccessAt, DateTime now)
    {
        if (!enabled)
        {
            return "disabled";
        }

        if (consecutiveFailures >= 5)
        {
            return "failing";
        }

        if (lastFailureAt is { } failure && failure >= now.AddHours(-24) && (lastSuccessAt is null || failure > lastSuccessAt))
        {
            return "degraded";
        }

        return "healthy";
    }

    private void Apply(string name, string url, string host, IEnumerable<string> eventTypes, string? description)
    {
        Name = Guard.MaxLength(Guard.NotEmpty(name), IntegrationsLimits.NameMaxLength);
        Url = Guard.MaxLength(Guard.NotEmpty(url), IntegrationsLimits.UrlMaxLength);
        Host = Guard.MaxLength(Guard.NotEmpty(host), IntegrationsLimits.HostMaxLength);
        EventTypes = [.. eventTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Description = string.IsNullOrWhiteSpace(description) ? null : Guard.MaxLength(description.Trim(), IntegrationsLimits.DescriptionMaxLength);
    }
}
