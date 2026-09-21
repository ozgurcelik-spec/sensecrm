using System.Text;
using System.Text.RegularExpressions;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Domain;
using Sense.Crm.Modules.Integrations.Domain.Webhooks;

namespace Sense.Crm.Modules.Integrations.Infrastructure.Delivery;

/// <summary>Geri çekilme titreşimi (testlerde 1.0): <c>±JitterPercent</c> çarpanı.</summary>
public interface IJitter
{
    /// <summary><paramref name="percent"/> = 20 → [0,8; 1,2] aralığında çarpan.</summary>
    double Factor(int percent);
}

public sealed class RandomJitter : IJitter
{
    public double Factor(int percent) => 1.0 + ((Random.Shared.NextDouble() * 2.0) - 1.0) * (percent / 100.0);
}

/// <summary>Deneme sonucunun sınıfı.</summary>
public enum OutcomeKind
{
    Success,
    Retry,
    Terminal,
    Exhausted,
}

/// <summary>Sınıflandırılmış sonuç: bir sonraki adım (başarı / yeniden dene / terminal / tükendi), neden ve gecikme.</summary>
public sealed record DeliveryOutcome(OutcomeKind Kind, string? Reason, int? HttpStatus, TimeSpan? RetryDelay, string? Detail);

/// <summary>
/// Sonuç sınıflaması (plan "Teslimat hattı"): <c>2xx</c> → başarı. <b>Yeniden denenir:</b> ağ/bağlantı hatası, zaman aşımı, <c>408</c>, <c>425</c>, <c>429</c>, <c>5xx</c> (<c>Retry-After</c> gecikmeyi uzatır, ≤ 3600 sn);
/// <c>dns_error</c> 3 denemeye kadar yeniden denenir. <b>Terminal:</b> <c>3xx</c> (izlenmez), diğer <c>4xx</c>, <c>blocked_destination</c>, <c>tls_error</c>. Sekizinci başarısızlıkta <c>retries_exhausted</c>. Geri çekilme:
/// 10 sn → 1 dk → 5 dk → 30 dk → 2 sa → 6 sa → 12 sa (± jitter).
/// </summary>
public static class DeliveryOutcomeClassifier
{
    public const int DnsMaxAttempts = 3;
    public const int RetryAfterCapSeconds = 3600;

    public static DeliveryOutcome Classify(TransportResult result, int attemptNo, WebhookOptions options, IJitter jitter)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Status)
        {
            case TransportStatus.Blocked:
                return new DeliveryOutcome(OutcomeKind.Terminal, DeliveryFailureReasons.BlockedDestination, null, null, result.Detail);
            case TransportStatus.TlsError:
                return new DeliveryOutcome(OutcomeKind.Terminal, DeliveryFailureReasons.TlsError, null, null, result.Detail);
            case TransportStatus.Timeout:
                return Retryable(DeliveryFailureReasons.Timeout, null, null, attemptNo, options, jitter, result.Detail);
            case TransportStatus.ConnectionError:
                return Retryable(DeliveryFailureReasons.ConnectionError, null, null, attemptNo, options, jitter, result.Detail);
            default:
                break;
        }

        var status = result.HttpStatus ?? 0;
        if (status is >= 200 and < 300)
        {
            return new DeliveryOutcome(OutcomeKind.Success, null, status, null, null);
        }

        if (status is >= 300 and < 400)
        {
            return new DeliveryOutcome(OutcomeKind.Terminal, DeliveryFailureReasons.Redirect, status, null, "redirect not followed");
        }

        if (status is 408 or 425 or 429 or >= 500)
        {
            return Retryable(DeliveryFailureReasons.HttpError, status, result.RetryAfter, attemptNo, options, jitter, null);
        }

        return new DeliveryOutcome(OutcomeKind.Terminal, DeliveryFailureReasons.HttpError, status, null, null);
    }

    /// <summary>Ad çözülemedi (boş çözümleme): 3 denemeye kadar yeniden denenir, sonra terminal.</summary>
    public static DeliveryOutcome ClassifyDnsFailure(int attemptNo, WebhookOptions options, IJitter jitter) =>
        attemptNo >= DnsMaxAttempts
            ? new DeliveryOutcome(OutcomeKind.Terminal, DeliveryFailureReasons.DnsError, null, null, "dns_error")
            : Retryable(DeliveryFailureReasons.DnsError, null, null, attemptNo, options, jitter, "dns_error");

    /// <summary>Hedef IP/URL engelli (SSRF): terminal.</summary>
    public static DeliveryOutcome Blocked(string detail) => new(OutcomeKind.Terminal, DeliveryFailureReasons.BlockedDestination, null, null, detail);

    private static DeliveryOutcome Retryable(string reason, int? httpStatus, TimeSpan? retryAfter, int attemptNo, WebhookOptions options, IJitter jitter, string? detail)
    {
        if (attemptNo >= options.MaxAttempts)
        {
            return new DeliveryOutcome(OutcomeKind.Exhausted, DeliveryFailureReasons.RetriesExhausted, httpStatus, null, detail ?? reason);
        }

        var schedule = options.EffectiveBackoff;
        var baseSeconds = schedule[Math.Min(attemptNo - 1, schedule.Count - 1)];
        var planned = TimeSpan.FromSeconds(baseSeconds * jitter.Factor(options.JitterPercent));
        var delay = planned;
        if (retryAfter is { } wait)
        {
            var capped = TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds, RetryAfterCapSeconds));
            delay = capped > planned ? capped : planned;
        }

        return new DeliveryOutcome(OutcomeKind.Retry, reason, httpStatus, delay, detail);
    }
}

/// <summary>
/// Yanıt özeti (günlük): ilk <c>ResponseSnippetBytes</c> bayt UTF-8 metin, denetim karakterleri temizlenmiş, ikili içerik <c>[binary]</c>; <c>authorization|token|secret|password|api[_-]?key</c>
/// desenleri ve bilinen sır biçimleri (<c>whsec_…</c>, <c>crmk_…</c>) maskelenir (alıcı yanıtı sır yansıtsa bile günlüğe girmez).
/// </summary>
public static partial class ResponseSnippet
{
    public const string Binary = "[binary]";
    public const string Mask = "***";

    [GeneratedRegex("""(?i)(authorization|token|secret|password|api[_-]?key)(["'\s:=]+)(bearer\s+)?[^\s"',;&}\]]+""")]
    private static partial Regex KeyValuePattern();

    [GeneratedRegex("(?i)(whsec_|crmk_)[A-Za-z0-9_-]+")]
    private static partial Regex SecretTokenPattern();

    public static string? Build(byte[] body, int maxBytes)
    {
        if (body.Length == 0)
        {
            return null;
        }

        var slice = body.AsSpan(0, Math.Min(body.Length, maxBytes));
        if (slice.Contains((byte)0))
        {
            return Binary;
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(slice);
        }
        catch (DecoderFallbackException)
        {
            // Kesilmiş çok baytlı karakter olabilir: son 3 baytı atıp bir kez daha dene; yine geçersizse ikili.
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(slice[..Math.Max(slice.Length - 3, 0)]);
            }
            catch (DecoderFallbackException)
            {
                return Binary;
            }
        }

        return Sanitize(text);
    }

    /// <summary>Denetim karakterlerini boşlukla değiştirir ve sır desenlerini maskeler.</summary>
    public static string Sanitize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(char.IsControl(c) && c is not ('\n' or '\t') ? ' ' : c);
        }

        var cleaned = SecretTokenPattern().Replace(builder.ToString(), "$1" + Mask);
        return KeyValuePattern().Replace(cleaned, "$1$2" + Mask);
    }
}
