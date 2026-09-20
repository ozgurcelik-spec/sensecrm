using System.Text.Json;
using System.Text.Json.Nodes;
using Sense.Crm.Modules.Integrations.Contracts;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Integrations.Application.OpenApi;

/// <summary>Kamuya açık API kataloğundaki bir kaynak: yol öneki (taban <c>/api/v1</c> sonrası), kapı modülü (çekirdek için null) ve okuma/yazma kapsamı.</summary>
public sealed record PublicApiEntry(string PathPrefix, string? Module, string ReadScope, string? WriteScope);

/// <summary>Kaynak ilke istisnası: bir işlemin gerçek gereksinimi birden çok izin olabilir (ilk kapsam <c>x-required-scope</c>, tümü <c>x-required-scopes</c>).</summary>
public sealed record PublicApiOperationOverride(string Method, string PathSuffix, IReadOnlyList<string> Scopes);

/// <summary>
/// Kamuya açık (API anahtarıyla çağrılabilen) uçlar (D13): <c>/accounts /contacts /leads /deals /pipelines /activities /products /quotes /orders /cases /campaigns /reports</c>. Yönetim düzlemi
/// (<c>/auth /me /organization /platform /workflows /approvals /integrations /service/sla-policies /audit /permissions /subscription /onboarding</c>) belgede <b>yoktur</b> (anahtarlara kapalıdır).
/// <c>x-required-scope</c> kuralı: <c>GET</c> → <c>.read</c>, diğerleri → <c>.write</c>, <c>/reports/*</c> → <c>crm.reports.read</c>. <c>/pipelines</c> yazmaları <c>org.settings.manage</c> gerektirir → belgelenmez.
/// </summary>
public static class PublicApiCatalog
{
    public const string BasePath = "/api/v1";
    public const string SecuritySchemeName = "bearerApiKey";

    public static IReadOnlyList<PublicApiEntry> Entries { get; } =
    [
        new("/accounts", null, "crm.accounts.read", "crm.accounts.write"),
        new("/contacts", null, "crm.contacts.read", "crm.contacts.write"),
        new("/leads", null, "crm.leads.read", "crm.leads.write"),
        new("/deals", null, "crm.deals.read", "crm.deals.write"),
        new("/pipelines", null, "crm.deals.read", null),
        new("/activities", null, "crm.activities.read", "crm.activities.write"),
        new("/products", GatedModules.Commerce, "crm.products.read", "crm.products.write"),
        new("/quotes", GatedModules.Commerce, "crm.quotes.read", "crm.quotes.write"),
        new("/orders", GatedModules.Commerce, "crm.orders.read", "crm.orders.write"),
        new("/cases", GatedModules.Service, "crm.cases.read", "crm.cases.write"),
        new("/campaigns", GatedModules.Marketing, "crm.campaigns.read", "crm.campaigns.write"),
        new("/reports/sales", null, "crm.reports.read", null),
        new("/reports/activities", null, "crm.reports.read", null),
        new("/reports/commerce", GatedModules.Commerce, "crm.reports.read", null),
        new("/reports/service", GatedModules.Service, "crm.reports.read", null),
        new("/reports/marketing", GatedModules.Marketing, "crm.reports.read", null),
    ];

    public static IReadOnlyList<PublicApiOperationOverride> Overrides { get; } =
    [
        new("get", "/accounts/{id}/contacts", ["crm.accounts.read", "crm.contacts.read"]),
        new("get", "/accounts/{id}/deals", ["crm.accounts.read", "crm.deals.read"]),
        new("post", "/leads/{id}/convert", ["crm.leads.write", "crm.accounts.write", "crm.contacts.write"]),
        new("post", "/quotes/{id}/convert", ["crm.quotes.read", "crm.orders.write"]),
    ];

    /// <summary><paramref name="fullPath"/> (<c>/api/v1/accounts/{id}</c>) kataloğa aitse girişi ve göreli yolu döner.</summary>
    public static (PublicApiEntry Entry, string Relative)? Match(string fullPath)
    {
        if (!fullPath.StartsWith(BasePath + "/", StringComparison.Ordinal))
        {
            return null;
        }

        var relative = fullPath[BasePath.Length..];
        foreach (var entry in Entries.OrderByDescending(e => e.PathPrefix.Length))
        {
            if (relative.Equals(entry.PathPrefix, StringComparison.Ordinal) || relative.StartsWith(entry.PathPrefix + "/", StringComparison.Ordinal))
            {
                return (entry, relative);
            }
        }

        return null;
    }
}

/// <summary>
/// Tam OpenAPI belgesinden kiracıya özel süzülmüş belge üretir (JSON düzeyinde derin kopya): yalnız katalog yolları ve <b>planda açık modülün</b> yolları; her işleme <c>x-required-scope</c> ve
/// <c>security: [{ bearerApiKey: [] }]</c>; yazma kapsamı olmayan kaynaklarda yalnız <c>GET</c>; askıda referans kalmayan <c>components/schemas</c> budanır.
/// </summary>
public static class OpenApiDocumentFilter
{
    private const string Description = """
        Sense CRM public API (v1). Authenticate with an API key: `Authorization: Bearer crmk_…` (keys are created by a tenant administrator; the raw key is shown once).
        Scopes are permission keys (`crm.*`); a request needs the operation's `x-required-scope` and never exceeds the key creator's permissions.
        Lists are paged: `page` (from 1), `pageSize` (default 25, max 100), response `{ items, page, pageSize, totalCount }`; `sort=field|-field`; `q` is a partial match
        (`%`, `_`, `\` escaped); dates are `YYYY-MM-DD`, timestamps are UTC ISO 8601, amounts are numbers with a `currency`. Errors are ProblemDetails with a stable `code`
        (clients should branch on `code`); rate limiting returns `429` with `Retry-After`; concurrent edits return `409 general.concurrency_conflict`.
        Versioning: the `/api/v1` contract only grows (new endpoints, optional fields, enum values — ignore unknown ones); breaking changes ship as `/api/v2` with
        `Deprecation`/`Sunset` headers and at least 12 months of parallel support (6 months for a single endpoint).
        """;

    public static string Filter(string baseDocumentJson, Func<string?, bool> moduleEnabled)
    {
        ArgumentNullException.ThrowIfNull(moduleEnabled);
        var root = JsonNode.Parse(baseDocumentJson)?.AsObject() ?? throw new InvalidOperationException("OpenAPI base document is empty.");

        var paths = new JsonObject();
        if (root["paths"] is JsonObject source)
        {
            foreach (var (path, item) in source.ToList())
            {
                if (PublicApiCatalog.Match(path) is not { } match || !moduleEnabled(match.Entry.Module) || item is not JsonObject operations)
                {
                    continue;
                }

                var kept = new JsonObject();
                foreach (var (method, operation) in operations.ToList())
                {
                    if (operation is not JsonObject op || !IsHttpMethod(method))
                    {
                        continue;
                    }

                    var scopes = ScopesFor(match.Entry, match.Relative, method);
                    if (scopes is null)
                    {
                        continue;
                    }

                    var clone = (JsonObject)JsonNode.Parse(op.ToJsonString())!;
                    clone["x-required-scope"] = scopes[0];
                    if (scopes.Count > 1)
                    {
                        clone["x-required-scopes"] = new JsonArray([.. scopes.Select(s => (JsonNode?)JsonValue.Create(s))]);
                    }

                    clone["security"] = new JsonArray(new JsonObject { [PublicApiCatalog.SecuritySchemeName] = new JsonArray() });
                    kept[method] = clone;
                }

                if (kept.Count > 0)
                {
                    paths[path] = kept;
                }
            }
        }

        var info = root["info"]?.AsObject() ?? new JsonObject();
        info["title"] = "Sense CRM API";
        info["version"] = "v1";
        info["description"] = Description;

        var result = new JsonObject
        {
            ["openapi"] = root["openapi"]?.DeepClone() ?? "3.1.1",
            ["info"] = info.DeepClone(),
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "/" }),
            ["paths"] = paths,
        };

        var components = new JsonObject
        {
            ["securitySchemes"] = new JsonObject
            {
                [PublicApiCatalog.SecuritySchemeName] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer", ["bearerFormat"] = "crmk" },
            },
        };
        var allSchemas = root["components"]?["schemas"] as JsonObject;
        if (allSchemas is not null)
        {
            var reachable = ReachableSchemas(paths, allSchemas);
            var schemas = new JsonObject();
            foreach (var name in reachable.Order(StringComparer.Ordinal))
            {
                schemas[name] = allSchemas[name]!.DeepClone();
            }

            if (schemas.Count > 0)
            {
                components["schemas"] = schemas;
            }
        }

        result["components"] = components;
        result["security"] = new JsonArray(new JsonObject { [PublicApiCatalog.SecuritySchemeName] = new JsonArray() });
        return result.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>İşlemin gerektirdiği kapsamlar; belgelenmemesi gereken işlem (yazma kapsamı yok / rapor dışı yazma) için null.</summary>
    public static IReadOnlyList<string>? ScopesFor(PublicApiEntry entry, string relativePath, string method)
    {
        var lower = method.ToLowerInvariant();
        var over = PublicApiCatalog.Overrides.FirstOrDefault(o => o.Method == lower && string.Equals(o.PathSuffix, relativePath, StringComparison.Ordinal));
        if (over is not null)
        {
            return over.Scopes;
        }

        if (lower == "get")
        {
            return [entry.ReadScope];
        }

        return entry.WriteScope is null ? null : [entry.WriteScope];
    }

    private static bool IsHttpMethod(string name) => name is "get" or "post" or "put" or "patch" or "delete";

    private static HashSet<string> ReachableSchemas(JsonObject paths, JsonObject allSchemas)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<JsonNode>();
        queue.Enqueue(paths);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var reference in References(node))
            {
                if (found.Add(reference) && allSchemas[reference] is { } schema)
                {
                    queue.Enqueue(schema);
                }
            }
        }

        return found;
    }

    private static IEnumerable<string> References(JsonNode node)
    {
        const string Prefix = "#/components/schemas/";
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == "$ref" && value is JsonValue v && v.TryGetValue<string>(out var reference) && reference.StartsWith(Prefix, StringComparison.Ordinal))
                    {
                        yield return reference[Prefix.Length..];
                    }
                    else if (value is not null)
                    {
                        foreach (var inner in References(value))
                        {
                            yield return inner;
                        }
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        foreach (var inner in References(item))
                        {
                            yield return inner;
                        }
                    }
                }

                break;
            default:
                break;
        }
    }
}

/// <summary>Kiracıya (plan) göre süzülmüş belgeyi üretir (Api uygular; taban belge süreç başına bir kez, süzülmüş sonuç kiracı+plan bazında önbelleklenir).</summary>
public interface IOpenApiDocumentComposer
{
    Task<string> ComposeAsync(Guid tenantId, EntitlementSnapshot snapshot, CancellationToken ct);
}

/// <summary>
/// <c>GET /integrations/openapi.json</c>: yalnız <c>org.integrations.manage</c> sahibine; kiracı planına göre süzülmüş belge (kapalı modüllerin yolları yok). Production'da anonim belge yoktur
/// (<c>/openapi/v1.json</c> <c>Docs:Enabled</c> kuralında kalır).
/// </summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record GetOpenApiDocumentQuery : IQuery<OpenApiDocumentDto>;

/// <summary>Belge gövdesi (ham JSON metni; denetleyici <c>application/json</c> olarak yazar).</summary>
public sealed record OpenApiDocumentDto(string Json);

public sealed class GetOpenApiDocumentHandler(ITenantContext tenant, ITenantEntitlements entitlements, IOpenApiDocumentComposer composer)
    : IQueryHandler<GetOpenApiDocumentQuery, OpenApiDocumentDto>
{
    public async Task<Result<OpenApiDocumentDto>> Handle(GetOpenApiDocumentQuery query, CancellationToken cancellationToken)
    {
        var snapshot = await entitlements.GetAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        return new OpenApiDocumentDto(await composer.ComposeAsync(tenant.TenantId, snapshot, cancellationToken).ConfigureAwait(false));
    }
}
