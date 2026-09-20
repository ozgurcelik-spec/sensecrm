using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Sense.Crm.Modules.Integrations.Application;

namespace Sense.Crm.Modules.Integrations.Api;

/// <summary>
/// Mevcut <c>AddOpenApi("v1")</c> belgesini (süreç başına bir kez) JSON olarak üretir ve önbellekler. Belge <b>yalnız</b> kiracıya süzülüp <c>GET /integrations/openapi.json</c> ile sunulur; anonim yol
/// <c>/openapi/v1.json</c> Production'da <c>Docs:Enabled</c> kuralında kalır (kapalı).
/// </summary>
public sealed class OpenApiBaseDocumentSource(IServiceProvider services) : IOpenApiBaseDocumentSource, IDisposable
{
    private const string DocumentName = "v1";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _json;

    public void Dispose() => _gate.Dispose();

    public async Task<string> GetJsonAsync(CancellationToken ct)
    {
        if (_json is not null)
        {
            return _json;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_json is not null)
            {
                return _json;
            }

            var provider = services.GetRequiredKeyedService<IOpenApiDocumentProvider>(DocumentName);
            var document = await provider.GetOpenApiDocumentAsync(ct).ConfigureAwait(false);
            _json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1, ct).ConfigureAwait(false);
            return _json;
        }
        finally
        {
            _gate.Release();
        }
    }
}
