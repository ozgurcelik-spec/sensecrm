using System.Text.Json;

namespace Crm.Modules.Service.Domain.Cases;

/// <summary>Talep durumu. Sıralama (liste <c>sort=status</c>) metin değil enum sırasıdır.</summary>
public enum CaseStatus
{
    New,
    Open,
    Pending,
    Resolved,
    Closed,
}

/// <summary>Talep önceliği; sıralama low &lt; normal &lt; high &lt; urgent.</summary>
public enum CasePriority
{
    Low,
    Normal,
    High,
    Urgent,
}

/// <summary>Talebin geldiği kanal (yalnız kayıt bilgisi; e-postadan talep açma kapsam dışı).</summary>
public enum CaseChannel
{
    Email,
    Phone,
    Web,
    Other,
}

/// <summary>Zaman çizelgesi olay türü (yorumlar ayrı tablodadır).</summary>
public enum CaseEventType
{
    Created,
    StatusChanged,
    PriorityChanged,
    Assigned,
}

/// <summary>Yorum görünürlüğü: herkese açık yanıt veya dahili not.</summary>
public enum CommentVisibility
{
    Public,
    Internal,
}

/// <summary>Okuma anında hesaplanan SLA durumu (saklanmaz).</summary>
public enum SlaState
{
    Ok,
    AtRisk,
    Breached,
}

/// <summary>Enum → tel biçimi (camelCase metin): denetim/olay alanları ve <c>CaseEvent.fromValue/toValue</c> için.</summary>
public static class EnumText
{
    public static string Camel<T>(T value)
        where T : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    /// <summary>camelCase (veya büyük/küçük harf duyarsız) metni tanımlı bir enum değerine çevirir; sayısal metin reddedilir.</summary>
    public static bool TryParse<T>(string? text, out T value)
        where T : struct, Enum
    {
        value = default;
        var trimmed = text?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && !char.IsDigit(trimmed[0])
            && Enum.TryParse(trimmed, ignoreCase: true, out value)
            && Enum.IsDefined(value);
    }
}
