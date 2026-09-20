namespace Sense.Crm.Shared.Contracts.Files;

/// <summary>Dosya eki alabilen kayıt türlerinin tel değerleri (M8C; <c>?recordType=</c> ve <c>files.attachments.record_type</c>).</summary>
public static class AttachmentRecordTypes
{
    public const string Account = "account";
    public const string Contact = "contact";
    public const string Lead = "lead";
    public const string Deal = "deal";
    public const string Activity = "activity";
    public const string Case = "case";
    public const string Quote = "quote";
    public const string Order = "order";
    public const string Campaign = "campaign";

    public static IReadOnlyList<string> All { get; } = [Account, Contact, Lead, Deal, Activity, Case, Quote, Order, Campaign];

    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>
/// Bir kayıt türünün dosya eki bağı (M8C, D2). <b>Sahip modül</b> kendi türlerini uygular ve <c>Add&lt;Modül&gt;ContractServices()</c> ile kaydeder;
/// <c>Files</c> modülü <c>IEnumerable&lt;IAttachmentTarget&gt;</c>'ı <see cref="RecordType"/> ile çözer (yeni tür = yeni bir sınıf, Files'a dokunulmaz).
/// İzin anahtarları sahip modülün kendi <c>*Permissions</c> sabitlerinden gelir (dizge kopyalanmaz).
/// </summary>
public interface IAttachmentTarget
{
    /// <summary>Tel değeri (<see cref="AttachmentRecordTypes"/>).</summary>
    string RecordType { get; }

    /// <summary>Sahip modülün <c>IModule.Name</c>'i (kapı modülü kararı için <c>GatedModules</c>).</summary>
    string Module { get; }

    string ReadPermission { get; }

    string WritePermission { get; }

    /// <summary>
    /// Verilen kimliklerden aktif kiracıda <b>var</b> ve silinmemiş olanlar (kiracı + yumuşak silme filtresi altında; başka kiracı asla dönmez).
    /// </summary>
    Task<IReadOnlySet<Guid>> GetExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}
