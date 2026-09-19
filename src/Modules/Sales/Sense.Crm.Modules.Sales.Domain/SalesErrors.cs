namespace Sense.Crm.Modules.Sales.Domain;

/// <summary>
/// Sales hata kodları (= web istemcisiyle sözleşme + yerelleştirme anahtarları, docs/plan/m2-api-kontrat.md).
/// Ortak kodlar (validation, forbidden, not_found) <c>Sense.Crm.Shared.Kernel.Results.ErrorCodes</c>'tadır.
/// </summary>
public static class SalesErrors
{
    public const string LeadAlreadyConverted = "lead.already_converted";
    public const string StageNotFound = "pipeline.stage_not_found";
    public const string StageInUse = "pipeline.stage_in_use";
    public const string DefaultPipelineRequired = "pipeline.default_required";
    public const string LostReasonRequired = "deal.lost_reason_required";
    public const string AccountHasDependents = "account.has_dependents";
    public const string OwnerNotMember = "owner.not_member";

    /// <summary>Aşama kümesi kuralı (tam bir won + bir lost + en az bir open) ihlali için doğrulama mesajı anahtarı.</summary>
    public const string InvalidStageSet = "validation.pipeline_stages";

    public const string LeadStatusNotSettable = "validation.lead_status";

    public static class Args
    {
        public const string StageName = "stageName";
    }
}

/// <summary>Domain sınırları (uzunluklar vb.). Ayar değil, veri modeli kısıtı.</summary>
public static class SalesLimits
{
    public const int NameMaxLength = 200;
    public const int PersonNameMaxLength = 100;
    public const int TitleMaxLength = 100;
    public const int IndustryMaxLength = 100;
    public const int WebsiteMaxLength = 300;
    public const int PhoneMaxLength = 50;
    public const int EmailMaxLength = 254;
    public const int DescriptionMaxLength = 2000;
    public const int StreetMaxLength = 300;
    public const int AddressPartMaxLength = 100;
    public const int LostReasonMaxLength = 500;
    public const int StageNameMaxLength = 100;
    public const int CurrencyLength = 3;
    public const int EnumColumnMaxLength = 20;
    public const int MaxStagesPerPipeline = 30;
    public const decimal MaxAmount = 999_999_999_999m;
    public const int MaxProbability = 100;
}

/// <summary>Kullanıcı girdisi metin temizliği: kırp, boşsa null.</summary>
internal static class Text
{
    public static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : Sense.Crm.Shared.Kernel.Guard.MaxLength(trimmed, maxLength);
    }
}
