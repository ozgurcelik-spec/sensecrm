using Microsoft.Extensions.Configuration;

namespace Sense.Crm.Shared.Infrastructure.DependencyInjection;

/// <summary>
/// Dosya tabanlı gizli değerler (Docker/Kubernetes secret) — Worker ve Migrator için (API zaten <c>AddKeyPerFile</c> kullanır; Worker SDK'sında bu paket yoktur):
/// <c>/run/secrets/&lt;Ad&gt;</c> dosyası yapılandırma anahtarı olur (<c>"__"</c> = <c>":"</c>), örn. <c>Integrations__Encryption__Keys__k1</c> → <c>Integrations:Encryption:Keys:k1</c>.
/// Dizin yoksa (geliştirme) sessizce yok sayılır. Değerin sonundaki satır sonları kırpılır; dosya içeriği günlüğe yazılmaz.
/// </summary>
public static class DockerSecretsConfiguration
{
    public const string DefaultDirectory = "/run/secrets";

    public static IConfigurationBuilder AddDockerSecrets(this IConfigurationBuilder builder, string directory = DefaultDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!Directory.Exists(directory))
        {
            return builder;
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var key = Path.GetFileName(file).Replace("__", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
            values[key] = File.ReadAllText(file).TrimEnd('\r', '\n');
        }

        return builder.AddInMemoryCollection(values);
    }
}
