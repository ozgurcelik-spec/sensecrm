using System.Globalization;
using System.Text.RegularExpressions;

namespace Sense.Crm.Modules.Files.Domain;

/// <summary>
/// Nesne deposu anahtarı (<c>{tenantId:D}/{yyyy}/{fileId:N}</c>). <b>Tek oluşturma yolu</b>: <see cref="For"/> (sunucu üretimi kimlikler) ve katı
/// <see cref="Parse"/>/<see cref="TryParse"/> (veritabanından okunan anahtarın yeniden doğrulanması). Kullanıcıdan gelen hiçbir parça (dosya adı, tür,
/// kayıt kimliği) anahtara girmez; küçük harf onaltılık dışında hiçbir karakter, <c>..</c> veya ek parça kabul edilmez.
/// </summary>
public readonly partial record struct ObjectKey
{
    private const int MinYear = 2000;
    private const int MaxYear = 2999;

    private ObjectKey(Guid tenantId, int year, Guid fileId)
    {
        TenantId = tenantId;
        Year = year;
        FileId = fileId;
    }

    public Guid TenantId { get; }

    public int Year { get; }

    public Guid FileId { get; }

    /// <summary>Kiracının tüm nesnelerinin öneki: <c>{tenantId:D}/</c> (imha ve listeleme).</summary>
    public static string TenantPrefix(Guid tenantId) => tenantId.ToString("D", CultureInfo.InvariantCulture) + "/";

    public static ObjectKey For(Guid tenantId, int year, Guid fileId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        if (fileId == Guid.Empty)
        {
            throw new ArgumentException("File id must not be empty.", nameof(fileId));
        }

        if (year is < MinYear or > MaxYear)
        {
            throw new ArgumentOutOfRangeException(nameof(year));
        }

        return new ObjectKey(tenantId, year, fileId);
    }

    public static ObjectKey Parse(string? value) =>
        TryParse(value, out var key) ? key : throw new FormatException("Invalid object key.");

    public static bool TryParse(string? value, out ObjectKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(value) || value.Length != KeyLength)
        {
            return false;
        }

        var match = KeyPattern().Match(value);
        if (!match.Success)
        {
            return false;
        }

        if (!Guid.TryParseExact(match.Groups["t"].Value, "D", out var tenantId)
            || !Guid.TryParseExact(match.Groups["f"].Value, "N", out var fileId)
            || !int.TryParse(match.Groups["y"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || tenantId == Guid.Empty
            || fileId == Guid.Empty
            || year is < MinYear or > MaxYear)
        {
            return false;
        }

        key = new ObjectKey(tenantId, year, fileId);
        return true;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{TenantId:D}/{Year:D4}/{FileId:N}");

    private const int KeyLength = 36 + 1 + 4 + 1 + 32;

    [GeneratedRegex("^(?<t>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/(?<y>[0-9]{4})/(?<f>[0-9a-f]{32})$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
