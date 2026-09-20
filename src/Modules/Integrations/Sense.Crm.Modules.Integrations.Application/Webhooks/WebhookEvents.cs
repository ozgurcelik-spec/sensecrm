using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sense.Crm.Modules.Commerce.Contracts;
using Sense.Crm.Modules.Integrations.Contracts;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Modules.Service.Contracts;
using Sense.Crm.Shared.Contracts.Events;

namespace Sense.Crm.Modules.Integrations.Application.Webhooks;

/// <summary>
/// Webhook zarfı (D3): <c>{ id, type, version, occurredAt, tenantId, actorUserId?, data }</c>. Serileştirme <b>belirlidir</b> (alan sırası sabit, <c>JsonObject</c> ekleme sırası) ve
/// imzalanan tam bayt dizisi olarak <c>payload text</c> saklanır (<c>jsonb</c> anahtar sırasını bozup imzayı geçersiz kılardı).
/// </summary>
public static class WebhookEnvelope
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public const int CurrentVersion = 1;

    public static string Serialize(Guid id, string type, int version, DateTime occurredAt, Guid tenantId, Guid? actorUserId, JsonObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var root = new JsonObject
        {
            ["id"] = id,
            ["type"] = type,
            ["version"] = version,
            ["occurredAt"] = FormatTime(occurredAt),
        };
        root["tenantId"] = tenantId;
        if (actorUserId is { } actor)
        {
            root["actorUserId"] = actor;
        }

        root["data"] = data;
        return root.ToJsonString(Options);
    }

    /// <summary>UTC ISO 8601: <c>2026-09-20T09:00:00Z</c> (kesirli saniye varsa milisaniyeye kadar, sondaki sıfırlar atılır).</summary>
    public static string FormatTime(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFF'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>deal.won/lost</c> kimliği: kaynaktan deterministik (<c>SHA-256(eventId ‖ type)</c> ilk 16 bayt → UUID) — <c>deal.stage_changed</c> ile çakışmaz ve yeniden işlemede aynı kalır.
    /// </summary>
    public static Guid DeriveId(Guid eventId, string type)
    {
        var input = new byte[16 + Encoding.UTF8.GetByteCount(type)];
        eventId.TryWriteBytes(input, bigEndian: true, out _);
        Encoding.UTF8.GetBytes(type, input.AsSpan(16));
        var hash = SHA256.HashData(input);
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}

/// <summary>
/// Olay verisi eşleyicileri: <b>elle yazılmış açık izin listesi</b> (yansıma yok). Yalnız kimlik/numara/durum/tutar alanları yazılır; e-posta, telefon, adres, kişi adı ve serbest metin
/// <b>yoktur</b> (KVKK veri minimizasyonu). <c>null</c> alanlar yazılmaz. Yeni tür eklerken bu kural mimari testle (yasaklı alan adı listesi) denetlenir.
/// </summary>
public static class WebhookDataMappers
{
    public static JsonObject Map(LeadCreated e) => new() { ["leadId"] = e.LeadId, ["source"] = e.Source, ["ownerUserId"] = e.OwnerUserId };

    public static JsonObject Map(LeadConverted e) => Obj(("leadId", e.LeadId), ("accountId", e.AccountId), ("contactId", e.ContactId), ("dealId", e.DealId));

    public static JsonObject Map(AccountCreated e) => Obj(("accountId", e.AccountId), ("ownerUserId", e.OwnerUserId));

    public static JsonObject Map(ContactCreated e) => Obj(("contactId", e.ContactId), ("accountId", e.AccountId), ("ownerUserId", e.OwnerUserId));

    public static JsonObject MapStageChanged(DealStageChangedIntegration e) =>
        Obj(("dealId", e.DealId), ("pipelineId", e.PipelineId), ("fromStageId", e.FromStageId), ("toStageId", e.ToStageId), ("toStageKind", e.ToStageKind), ("amount", e.Amount), ("currency", e.Currency));

    /// <summary><c>deal.won</c> ve <c>deal.lost</c> ortak verisi.</summary>
    public static JsonObject MapDealClosed(DealStageChangedIntegration e) =>
        Obj(("dealId", e.DealId), ("pipelineId", e.PipelineId), ("toStageId", e.ToStageId), ("amount", e.Amount), ("currency", e.Currency));

    public static JsonObject Map(QuoteAccepted e) =>
        Obj(("quoteId", e.QuoteId), ("number", e.Number), ("accountId", e.AccountId), ("dealId", e.DealId), ("grandTotal", e.GrandTotal), ("currency", e.Currency));

    public static JsonObject Map(SalesOrderCreated e) =>
        Obj(("orderId", e.OrderId), ("number", e.Number), ("accountId", e.AccountId), ("dealId", e.DealId), ("quoteId", e.QuoteId), ("grandTotal", e.GrandTotal), ("currency", e.Currency), ("source", e.Source));

    public static JsonObject Map(CaseCreated e) =>
        Obj(("caseId", e.CaseId), ("number", e.CaseNumber), ("accountId", e.AccountId), ("contactId", e.ContactId), ("priority", e.Priority), ("channel", e.Channel), ("assignedUserId", e.AssignedUserId));

    public static JsonObject Map(CaseResolved e) =>
        Obj(
            ("caseId", e.CaseId), ("number", e.CaseNumber), ("accountId", e.AccountId), ("contactId", e.ContactId), ("priority", e.Priority), ("assignedUserId", e.AssignedUserId),
            ("resolvedAt", WebhookEnvelope.FormatTime(e.ResolvedAt)), ("resolutionMinutes", e.ResolutionMinutes), ("slaBreached", e.SlaBreached));

    /// <summary>Test ping'i verisi (<c>ping</c>).</summary>
    public static JsonObject Ping(Guid subscriptionId) => new() { ["subscriptionId"] = subscriptionId, ["message"] = "ping" };

    /// <summary>Tutarlar JSON sayısıdır (API ile aynı); sondaki sıfırlar atılır (<c>1500.5000</c> → <c>1500.5</c>).</summary>
    public static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;

    private static JsonObject Obj(params (string Name, object? Value)[] fields)
    {
        var result = new JsonObject();
        foreach (var (name, value) in fields)
        {
            switch (value)
            {
                case null:
                    break;
                case Guid guid:
                    result[name] = guid;
                    break;
                case string text:
                    result[name] = text;
                    break;
                case decimal number:
                    result[name] = Normalize(number);
                    break;
                case int integer:
                    result[name] = integer;
                    break;
                case bool flag:
                    result[name] = flag;
                    break;
                default:
                    throw new InvalidOperationException("Unsupported webhook data type: " + value.GetType().Name);
            }
        }

        return result;
    }
}

/// <summary>Olay kataloğu satırı (kod = tek kaynak): tür, sürüm, grup/modül, açıklama ve örnek veri üreticisi.</summary>
public sealed record WebhookEventDefinition(string Type, int Version, string Group, string Module, string Description, Func<JsonObject> SampleData, bool Deprecated = false, DateOnly? SunsetOn = null);

/// <summary>
/// v1 olay kataloğu (D2/D3). <c>GET /integrations/webhook-events</c>, geliştirici rehberi ve altın dosya (golden JSON) testi bu listeden üretilir; alan ekleme/silme testi kırar
/// (bilinçli değişiklik). Sürüm politikası: bir türün <c>data</c> alanları silinmez/yeniden adlandırılmaz; yeni isteğe bağlı alan eklenebilir; kırıcı değişiklik yeni tür adıyla gelir.
/// </summary>
public static class WebhookEventCatalog
{
    public static readonly Guid SampleTenantId = new("0192f0a1-0000-7000-8000-0000000000aa");

    public static readonly Guid SampleEventId = new("0192f0a1-0000-7000-8000-000000000001");

    public static readonly DateTime SampleOccurredAt = new(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc);

    private static readonly Guid A = new("0192f0a1-0000-7000-8000-0000000000bb");
    private static readonly Guid B = new("0192f0a1-0000-7000-8000-0000000000cc");
    private static readonly Guid C = new("0192f0a1-0000-7000-8000-0000000000dd");
    private static readonly Guid D = new("0192f0a1-0000-7000-8000-0000000000ee");
    private static readonly Guid E = new("0192f0a1-0000-7000-8000-0000000000ff");

    public static IReadOnlyList<WebhookEventDefinition> All { get; } =
    [
        new(WebhookEventTypes.LeadCreated, 1, "sales", "sales", "A lead was created.", () => WebhookDataMappers.Map(new LeadCreated(SampleTenantId, A, "-", "-", "web", B))),
        new(WebhookEventTypes.LeadConverted, 1, "sales", "sales", "A lead was converted into an account and contact (and optionally a deal).", () => WebhookDataMappers.Map(new LeadConverted(SampleTenantId, A, B, C, D))),
        new(WebhookEventTypes.AccountCreated, 1, "sales", "sales", "An account was created.", () => WebhookDataMappers.Map(new AccountCreated(SampleTenantId, A, B))),
        new(WebhookEventTypes.ContactCreated, 1, "sales", "sales", "A contact was created.", () => WebhookDataMappers.Map(new ContactCreated(SampleTenantId, A, B, C))),
        new(WebhookEventTypes.DealStageChanged, 1, "sales", "sales", "A deal moved to another pipeline stage.", () => WebhookDataMappers.MapStageChanged(SampleDeal("open"))),
        new(WebhookEventTypes.DealWon, 1, "sales", "sales", "A deal moved to a stage of kind won.", () => WebhookDataMappers.MapDealClosed(SampleDeal("won"))),
        new(WebhookEventTypes.DealLost, 1, "sales", "sales", "A deal moved to a stage of kind lost.", () => WebhookDataMappers.MapDealClosed(SampleDeal("lost"))),
        new(WebhookEventTypes.QuoteAccepted, 1, "commerce", "commerce", "A quote was accepted.", () => WebhookDataMappers.Map(new QuoteAccepted(SampleTenantId, A, "Q-2026-0001", B, C, 1500.5000m, "TRY"))),
        new(WebhookEventTypes.OrderCreated, 1, "commerce", "commerce", "A sales order was created.", () => WebhookDataMappers.Map(new SalesOrderCreated(SampleTenantId, A, "O-2026-0001", B, C, D, 1500.5000m, "TRY", "quote"))),
        new(WebhookEventTypes.CaseCreated, 1, "service", "service", "A support case was created.", () => WebhookDataMappers.Map(new CaseCreated(SampleTenantId, A, "C-2026-0001", B, C, "normal", "web", D))),
        new(WebhookEventTypes.CaseResolved, 1, "service", "service", "A support case was resolved.", () => WebhookDataMappers.Map(new CaseResolved(SampleTenantId, A, "C-2026-0001", B, C, "normal", D, SampleOccurredAt, 95, false))),
    ];

    public static WebhookEventDefinition? Find(string type) => All.FirstOrDefault(d => string.Equals(d.Type, type, StringComparison.Ordinal));

    /// <summary>Örnek zarf (sabit kimlikler/zaman): rehber ve altın dosya testi için; gerçek zarfla aynı serileştirici.</summary>
    public static string SampleEnvelope(WebhookEventDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return WebhookEnvelope.Serialize(SampleEventId, definition.Type, definition.Version, SampleOccurredAt, SampleTenantId, actorUserId: E, definition.SampleData());
    }

    private static DealStageChangedIntegration SampleDeal(string kind) =>
        new(SampleTenantId, A, B, C, D, kind, 1500.5000m, "TRY");
}

/// <summary>Olaydan olay örneği (occurrence) kurucuları.</summary>
public static class WebhookOccurrences
{
    public static WebhookOccurrence Of(IIntegrationEvent source, string type, JsonObject data) =>
        new(source.EventId, type, source.TenantId, source.ActorUserId, source.OccurredAt.UtcDateTime, data);

    /// <summary><c>deal.won</c>/<c>deal.lost</c>: kaynak olaydan deterministik türetilmiş kimlik.</summary>
    public static WebhookOccurrence Derived(IIntegrationEvent source, string type, JsonObject data) =>
        new(WebhookEnvelope.DeriveId(source.EventId, type), type, source.TenantId, source.ActorUserId, source.OccurredAt.UtcDateTime, data);
}

/// <summary>
/// Fan-out olay işleyicileri (D2): her kaynak event için bir <c>{Olay}WebhookHandler</c>. <c>[EntitlementExempt]</c> DEĞİLDİR → askıdaki/<c>integrations</c> kapalı kiracıda
/// <c>InProcessEventBus</c> işleyiciyi atlar (yeni olay kuyruğa girmez; M7 davranışı). Lead dönüşümü hesap/kişi yaratır ama <c>account.created</c>/<c>contact.created</c>
/// <b>üretmez</b> (tüketici <c>lead.converted</c> kullanır).
/// </summary>
public sealed class LeadCreatedWebhookHandler(IWebhookFanOut fanOut) : IIntegrationEventHandler<LeadCreated>
{
    public Task Handle(LeadCreated integrationEvent, CancellationToken cancellationToken) =>
        fanOut.PublishAsync(WebhookOccurrences.Of(integrationEvent, WebhookEventTypes.LeadCreated, WebhookDataMappers.Map(integrationEvent)), cancellationToken);
}

public sealed class LeadConvertedWebhookHandler(IWebhookFanOut fanOut) : IIntegrationEventHandler<LeadConverted>
{
    public Task Handle(LeadConverted integrationEvent, CancellationToken cancellationToken) =>
        fanOut.PublishAsync(WebhookOccurrences.Of(integrationEvent, WebhookEventTypes.LeadConverted, WebhookDataMappers.Map(integrationEvent)), cancellationToken);
}

public sealed class AccountCreatedWebhookHandler(IWebhookFanOut fanOut) : IIntegrationEventHandler<AccountCreated>
{
    public Task Handle(AccountCreated integrationEvent, CancellationToken cancellationToken) =>
        fanOut.PublishAsync(WebhookOccurrences.Of(integrationEvent, WebhookEventTypes.AccountCreated, WebhookDataMappers.Map(integrationEvent)), cancellationToken);
}

public sealed class ContactCreatedWebhookHandler(IWebhookFanOut fanOut) : IIntegrationEventHandler<ContactCreated>
{
    public Task Handle(ContactCreated integrationEvent, CancellationToken cancellationToken) =>
        fanOut.PublishAsync(WebhookOccurrences.Of(integrationEvent, WebhookEventTypes.ContactCreated, WebhookDataMappers.Map(integrationEvent)), cancellationToken);
}

/// <summary>Aşama değişimi: <c>deal.stage_changed</c> her zaman; <c>toStageKind = won|lost</c> ise ek olarak <c>deal.won</c>/<c>deal.lost</c> (türetilmiş kimlikle).</summary>
public sealed class DealStageChangedWebhookHandler(IWebhookFanOut fanOut) : IIntegrationEventHandler<DealStageChangedIntegration>
{
    public async Task Handle(DealStageChangedIntegration integrationEvent, CancellationToken cancellationToken)
    {
        await fanOut.PublishAsync(WebhookOccurrences.Of(integrationEvent, WebhookEventTypes.DealStageChanged, WebhookDataMappers.MapStageChanged(integrationEvent)), cancellationToken).ConfigureAwait(false);

        var derived = integrationEvent.ToStageKind switch
        {
            DealStageKinds.Won => WebhookEventTypes.DealWon,
            DealStageKinds.Lost => WebhookEventTypes.DealLost,
            _ => null,
        };
        if (derived is not null)
        {
            await fanOut.PublishAsync(WebhookOccurrences.Derived(integrationEvent, derived, WebhookDataMappers.MapDealClosed(integrationEvent)), cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class QuoteAcceptedWebhookHandler(IWebhookFanOut fanOut) : IIntegrationEventHandler<QuoteAccepted>
{
    public Task Handle(QuoteAccepted integrationEvent, CancellationToken cancellationToken) =>
        fanOut.PublishAsync(WebhookOccurrences.Of(integrationEvent, WebhookEventTypes.QuoteAccepted, WebhookDataMappers.Map(integrationEvent)), cancellationToken);
}

public sealed class SalesOrderCreatedWebhookHandler(IWebhookFanOut fanOut) : IIntegrationEventHandler<SalesOrderCreated>
{
    public Task Handle(SalesOrderCreated integrationEvent, CancellationToken cancellationToken) =>
        fanOut.PublishAsync(WebhookOccurrences.Of(integrationEvent, WebhookEventTypes.OrderCreated, WebhookDataMappers.Map(integrationEvent)), cancellationToken);
}

public sealed class CaseCreatedWebhookHandler(IWebhookFanOut fanOut) : IIntegrationEventHandler<CaseCreated>
{
    public Task Handle(CaseCreated integrationEvent, CancellationToken cancellationToken) =>
        fanOut.PublishAsync(WebhookOccurrences.Of(integrationEvent, WebhookEventTypes.CaseCreated, WebhookDataMappers.Map(integrationEvent)), cancellationToken);
}

public sealed class CaseResolvedWebhookHandler(IWebhookFanOut fanOut) : IIntegrationEventHandler<CaseResolved>
{
    public Task Handle(CaseResolved integrationEvent, CancellationToken cancellationToken) =>
        fanOut.PublishAsync(WebhookOccurrences.Of(integrationEvent, WebhookEventTypes.CaseResolved, WebhookDataMappers.Map(integrationEvent)), cancellationToken);
}
