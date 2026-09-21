using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Contracts;
using Sense.Crm.Modules.Integrations.Domain.Webhooks;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Modules;
using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Modules.Integrations.Infrastructure.Security;

/// <summary>
/// Webhook sırrı şifreleme (D7): <b>AES-256-GCM</b>; blob = <c>nonce(12) ‖ tag(16) ‖ şifreli metin</c>; AAD = <c>kiracı(16) ‖ abonelik(16) ‖ sürüm(int32 BE)</c> (başka satıra kopyalanan blob çözülemez).
/// Anahtarlar <c>Integrations:Encryption:Keys:&lt;id&gt;</c> (base64, 32 bayt), etkin olan <c>CurrentKeyId</c>. Development/Testing'de anahtar yoksa süreç başına geçici anahtar üretilir
/// (Production'da yoksa açılış <c>ValidateOnStart</c> ile reddedilir). Ham sır bu sınıftan başka hiçbir yere/günlüğe/hata iletisine girmez.
/// </summary>
public sealed class WebhookSecretProtector(IOptions<IntegrationsOptions> options) : IWebhookSecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int SecretBytes = 32;
    private readonly Lazy<IReadOnlyDictionary<string, byte[]>> _keys = new(() => LoadKeys(options.Value));

    public string CurrentKeyId => string.IsNullOrWhiteSpace(options.Value.Encryption.CurrentKeyId) ? EncryptionOptions.DefaultKeyId : options.Value.Encryption.CurrentKeyId;

    public string GenerateSecret() =>
        WebhookSignature.SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public SealedSecret Seal(string secret, Guid tenantId, Guid subscriptionId, int version)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var keyId = CurrentKeyId;
        var key = KeyFor(keyId);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(secret);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag, Aad(tenantId, subscriptionId, version));
        }

        CryptographicOperations.ZeroMemory(plain);
        var blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipher.CopyTo(blob, NonceSize + TagSize);
        return new SealedSecret(blob, keyId, secret[^4..]);
    }

    public string Open(byte[] cipher, string keyId, Guid tenantId, Guid subscriptionId, int version)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        if (cipher.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Sealed secret is malformed.");
        }

        var key = KeyFor(keyId);
        var nonce = cipher.AsSpan(0, NonceSize);
        var tag = cipher.AsSpan(NonceSize, TagSize);
        var body = cipher.AsSpan(NonceSize + TagSize);
        var plain = new byte[body.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, body, tag, plain, Aad(tenantId, subscriptionId, version));
        var text = Encoding.UTF8.GetString(plain);
        CryptographicOperations.ZeroMemory(plain);
        return text;
    }

    private byte[] KeyFor(string keyId) =>
        _keys.Value.TryGetValue(keyId, out var key) ? key : throw new CryptographicException("Unknown webhook secret encryption key id '" + keyId + "'.");

    private static byte[] Aad(Guid tenantId, Guid subscriptionId, int version)
    {
        var aad = new byte[36];
        tenantId.TryWriteBytes(aad.AsSpan(0, 16), bigEndian: true, out _);
        subscriptionId.TryWriteBytes(aad.AsSpan(16, 16), bigEndian: true, out _);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(32, 4), version);
        return aad;
    }

    private static Dictionary<string, byte[]> LoadKeys(IntegrationsOptions options)
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (id, value) in options.Encryption.Keys)
        {
            if (!string.IsNullOrWhiteSpace(value) && IntegrationsEncryptionKeys.TryDecode(value, out var bytes))
            {
                keys[id] = bytes;
            }
        }

        var current = string.IsNullOrWhiteSpace(options.Encryption.CurrentKeyId) ? EncryptionOptions.DefaultKeyId : options.Encryption.CurrentKeyId;
        if (!keys.ContainsKey(current))
        {
            if (!options.DevelopmentLike)
            {
                throw new InvalidOperationException("Integrations:Encryption:Keys:" + current + " is required outside Development/Testing.");
            }

            keys[current] = RandomNumberGenerator.GetBytes(KeySize);
        }

        return keys;
    }
}

/// <summary>Şifreleme anahtarı biçimi (base64, tam 32 bayt).</summary>
public static class IntegrationsEncryptionKeys
{
    public static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];
        try
        {
            var decoded = Convert.FromBase64String(value.Trim());
            if (decoded.Length != 32)
            {
                return false;
            }

            bytes = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Kısıtlı eylem denetimi (bellek içi, kopya başına): kayan pencerede <c>limit</c> aşılırsa false.</summary>
public sealed class ActionThrottle(TimeProvider clock) : IActionThrottle
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.Ordinal);

    public bool TryAcquire(string key, int limit, TimeSpan window)
    {
        var now = clock.GetUtcNow();
        var queue = _hits.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= limit)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}

/// <summary>Anahtar kapsam kataloğu: kayıtlı modüllerin izin katalogundan (<c>IModule.Permissions</c>); <c>crm.*</c> eksi <c>crm.approvals.decide</c>, <c>org.*</c> hiçbir anahtarda olmaz.</summary>
public sealed class ApiKeyScopeCatalog : IApiKeyScopeCatalog
{
    private readonly Lazy<IReadOnlyList<Permission>> _all;
    private readonly Lazy<HashSet<string>> _known;
    private readonly Lazy<IReadOnlyList<string>> _allowed;

    public ApiKeyScopeCatalog(IReadOnlyList<IModule> modules)
    {
        _all = new Lazy<IReadOnlyList<Permission>>(() => [.. modules.SelectMany(m => m.Permissions)]);
        _known = new Lazy<HashSet<string>>(() => _all.Value.Select(p => p.Key).ToHashSet(StringComparer.Ordinal));
        _allowed = new Lazy<IReadOnlyList<string>>(() => [.. ApiKeyScopePolicy.Allowed(_all.Value).Where(Application.OpenApi.PublicApiCatalog.Scopes.Contains)]);
    }

    public IReadOnlySet<string> AllKnown => _known.Value;

    public IReadOnlyList<string> Allowed => _allowed.Value;
}

/// <summary>
/// <c>IPermissionService</c> dekoratörü (D10): API anahtarı isteklerinde <c>kapsam ∩ oluşturanın o anki izinleri</c> — rol düşürme/pasifleştirme anahtarı anında daraltır. JWT isteklerinde
/// dokunmaz. <c>PermissionHandler</c> (<c>[Authorize(policy)]</c>) ve <c>AuthorizationBehaviour</c> aynı servisi kullanır.
/// </summary>
public sealed class ApiKeyScopedPermissionService(IPermissionService inner, ICurrentUser user) : IPermissionService
{
    public async Task<bool> HasAsync(Guid userId, string permission, CancellationToken cancellationToken = default) =>
        (await GetPermissionsAsync(userId, cancellationToken).ConfigureAwait(false)).Contains(permission);

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var owned = await inner.GetPermissionsAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user.ApiKey is not { } key || user.UserId != userId)
        {
            return owned;
        }

        var scoped = new HashSet<string>(owned.Where(key.Scopes.Contains), StringComparer.Ordinal);

        // Savunma derinliği: anahtar hiçbir koşulda org.* / crm.approvals.decide taşıyamaz (oluşturmada da engellenir).
        scoped.RemoveWhere(p => !ApiKeyScopePolicy.IsAllowedKey(p));
        return scoped;
    }
}
