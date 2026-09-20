using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sense.Crm.Modules.Integrations.Infrastructure;

namespace Sense.Crm.Migrator;

/// <summary>
/// <c>reencrypt-integration-secrets</c> (M8B, D7): webhook sırlarını mevcut şifreleme anahtarıyla (<c>Integrations:Encryption:CurrentKeyId</c>) yeniden şifreler. Anahtar döndürme sırası: yeni anahtar +
/// <c>CurrentKeyId</c> yapılandırılır (eski anahtar da yapılandırmada kalır) → bu komut → eski anahtar kaldırılır. İdempotent: zaten mevcut anahtarla şifreli sırlara dokunmaz; ham sır günlüğe yazılmaz.
/// </summary>
internal static class IntegrationsSecretsCommand
{
    public const string Name = "reencrypt-integration-secrets";

    public static async Task<int> RunAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var reencryptor = scope.ServiceProvider.GetRequiredService<IntegrationsSecretReencryptor>();
        try
        {
            var result = await reencryptor.RunAsync(ct).ConfigureAwait(false);
            logger.LogInformation("reencrypt-integration-secrets: {Subscriptions} subscriptions scanned, {Rewritten} secret(s) re-encrypted to key '{KeyId}'", result.Scanned, result.Rewritten, result.KeyId);
            return 0;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            logger.LogError("reencrypt-integration-secrets failed: an existing secret cannot be decrypted (is the previous encryption key still configured?). Nothing was written for the failing subscription.");
            return 1;
        }
    }
}
