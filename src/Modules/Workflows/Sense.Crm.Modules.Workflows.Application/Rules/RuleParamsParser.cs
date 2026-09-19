using System.Globalization;
using System.Text.Json;
using Sense.Crm.Modules.Workflows.Domain;
using Sense.Crm.Modules.Workflows.Domain.Rules;

namespace Sense.Crm.Modules.Workflows.Application.Rules;

/// <summary>Kural parametrelerindeki tek bir alan hatası (<c>params.&lt;alan&gt;</c>) ve mesaj (kaynak anahtarı).</summary>
public sealed record ParamError(string Field, string Message);

/// <summary>
/// Kural parametrelerini türe göre doğrular ve tipli <see cref="RuleParams"/>'a çevirir (saf hesap, birim testli).
/// <c>leadAssignment</c>: <c>sources?</c> (bilinen kaynaklar; boş = hepsi), <c>assigneeRoleId</c> (zorunlu Guid),
/// <c>followUpHours?</c> (1–720, varsayılan 24). <c>dealApproval</c>: <c>minAmount</c> (&gt; 0), <c>approverRoleId</c> (zorunlu Guid).
/// Rolün kiracıda var olması ayrıca (handler'da) <c>workflow.role_not_found</c> ile denetlenir.
/// </summary>
public static class RuleParamsParser
{
    public const string Required = "validation.required";
    public const string InvalidSource = "validation.workflow_source";
    public const string InvalidFollowUpHours = "validation.follow_up_hours";
    public const string InvalidMinAmount = "validation.min_amount";
    public const string InvalidRoleId = "validation.workflow_role_id";

    /// <summary>Tutar üst sınırı (decimal(18,4) ile uyumlu, makul).</summary>
    public const decimal MaxAmount = 999_999_999_999m;

    public static (RuleParams? Params, IReadOnlyList<ParamError> Errors) Parse(WorkflowRuleKind kind, JsonElement? element)
    {
        var errors = new List<ParamError>();
        if (element is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            errors.Add(new ParamError(string.Empty, WorkflowsErrors.InvalidParams));
            return (null, errors);
        }

        var obj = element is { ValueKind: JsonValueKind.Object } o ? o : (JsonElement?)null;
        RuleParams? parsed = kind switch
        {
            WorkflowRuleKind.LeadAssignment => ParseLeadAssignment(obj, errors),
            WorkflowRuleKind.DealApproval => ParseDealApproval(obj, errors),
            _ => null,
        };

        return errors.Count > 0 ? (null, errors) : (parsed, errors);
    }

    private static LeadAssignmentParams? ParseLeadAssignment(JsonElement? obj, List<ParamError> errors)
    {
        var roleId = ReadGuid(obj, "assigneeRoleId", errors);

        var sources = new List<string>();
        if (Property(obj, "sources") is { } sourcesElement && sourcesElement.ValueKind != JsonValueKind.Null)
        {
            if (sourcesElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new ParamError("sources", InvalidSource));
            }
            else
            {
                foreach (var item in sourcesElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text && LeadSources.IsKnown(text))
                    {
                        if (!sources.Contains(text, StringComparer.Ordinal))
                        {
                            sources.Add(text);
                        }
                    }
                    else
                    {
                        errors.Add(new ParamError("sources", InvalidSource));
                        break;
                    }
                }
            }
        }

        var hours = WorkflowLimits.DefaultFollowUpHours;
        if (Property(obj, "followUpHours") is { } hoursElement && hoursElement.ValueKind != JsonValueKind.Null)
        {
            if (hoursElement.ValueKind != JsonValueKind.Number || !hoursElement.TryGetInt32(out hours)
                || hours < WorkflowLimits.MinFollowUpHours || hours > WorkflowLimits.MaxFollowUpHours)
            {
                errors.Add(new ParamError("followUpHours", InvalidFollowUpHours));
            }
        }

        return roleId is { } id && errors.Count == 0 ? new LeadAssignmentParams(sources, id, hours) : null;
    }

    private static DealApprovalParams? ParseDealApproval(JsonElement? obj, List<ParamError> errors)
    {
        var roleId = ReadGuid(obj, "approverRoleId", errors);

        decimal minAmount = 0;
        if (Property(obj, "minAmount") is { } amountElement && amountElement.ValueKind != JsonValueKind.Null)
        {
            if (amountElement.ValueKind != JsonValueKind.Number || !amountElement.TryGetDecimal(out minAmount) || minAmount <= 0 || minAmount > MaxAmount)
            {
                errors.Add(new ParamError("minAmount", InvalidMinAmount));
            }
        }
        else
        {
            errors.Add(new ParamError("minAmount", Required));
        }

        return roleId is { } id && errors.Count == 0 ? new DealApprovalParams(decimal.Round(minAmount, 4), id) : null;
    }

    private static Guid? ReadGuid(JsonElement? obj, string name, List<ParamError> errors)
    {
        var element = Property(obj, name);
        if (element is null || element.Value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new ParamError(name, Required));
            return null;
        }

        if (element.Value.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.Value.GetString(), CultureInfo.InvariantCulture, out var id)
            && id != Guid.Empty)
        {
            return id;
        }

        errors.Add(new ParamError(name, InvalidRoleId));
        return null;
    }

    private static JsonElement? Property(JsonElement? obj, string name)
    {
        if (obj is not { } element)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
