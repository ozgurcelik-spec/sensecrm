namespace Sense.Crm.Modules.Identity.Application;

/// <summary>
/// Kayıt (self sign-up) denetimi (appsettings "Registration"). <see cref="Mode"/> boşsa ortama göre çözülür:
/// Development/Testing = <c>open</c>, diğer tüm ortamlar (Production) = <c>disabled</c>. Kurum içi dağıtımda organizasyonlar
/// yalnız platform yöneticisi (<c>POST /platform/organizations</c>) tarafından açılır.
/// </summary>
public sealed class RegistrationOptions
{
    /// <summary><c>open</c> | <c>disabled</c> (büyük/küçük harf duyarsız); boş = ortam varsayılanı.</summary>
    public string? Mode { get; set; }
}

public static class RegistrationModes
{
    public const string Open = "open";
    public const string Disabled = "disabled";

    public static bool IsValid(string? mode) =>
        string.IsNullOrWhiteSpace(mode)
        || string.Equals(mode.Trim(), Open, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode.Trim(), Disabled, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Herkese açık kaydın açık olup olmadığı (<c>POST /auth/signup</c> ve <c>GET /auth/config</c>).</summary>
public interface IRegistrationPolicy
{
    bool SignupEnabled { get; }
}

public sealed class RegistrationPolicy(bool signupEnabled) : IRegistrationPolicy
{
    public bool SignupEnabled { get; } = signupEnabled;

    /// <param name="options">Yapılandırılan ayar.</param>
    /// <param name="isDevelopmentLike">Development veya Testing ortamı (varsayılan: açık).</param>
    public static RegistrationPolicy Resolve(RegistrationOptions options, bool isDevelopmentLike)
    {
        ArgumentNullException.ThrowIfNull(options);
        var mode = string.IsNullOrWhiteSpace(options.Mode)
            ? (isDevelopmentLike ? RegistrationModes.Open : RegistrationModes.Disabled)
            : options.Mode.Trim();
        return new RegistrationPolicy(string.Equals(mode, RegistrationModes.Open, StringComparison.OrdinalIgnoreCase));
    }
}
