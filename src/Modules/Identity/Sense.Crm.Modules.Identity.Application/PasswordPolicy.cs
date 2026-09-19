using FluentValidation;
using FluentValidation.Results;
using Sense.Crm.Modules.Identity.Domain;

namespace Sense.Crm.Modules.Identity.Application;

/// <summary>
/// Parola politikası (H4-e): en az <c>MinPasswordLength</c> (varsayılan 10), en çok 128 karakter; e-postanın kullanıcı adı kısmını
/// ve gömülü yaygın parola listesini (<see cref="CommonPasswords"/>) içermez. Kayıt, platform organizasyonu yöneticisi ve parola
/// değiştirme aynı kuralı kullanır. Mesajlar kaynak anahtarıdır (yanıtta yerelleştirilir).
/// </summary>
public static class PasswordPolicy
{
    /// <summary>Kullanıcı adı kısmının bu uzunluktan kısa olması hâlinde e-posta karşılaştırması yapılmaz (aşırı red önlenir).</summary>
    private const int MinLocalPartLength = 3;

    public sealed record Violation(string Code, string? Placeholder = null, object? Value = null);

    public static IReadOnlyList<Violation> Evaluate(string? password, string? email, int minLength)
    {
        var violations = new List<Violation>();
        if (string.IsNullOrEmpty(password))
        {
            violations.Add(new Violation(IdentityErrors.Required));
            return violations;
        }

        if (password.Length < minLength)
        {
            violations.Add(new Violation(IdentityErrors.PasswordTooShort, "MinLength", minLength));
        }

        if (password.Length > IdentityLimits.PasswordMaxLength)
        {
            violations.Add(new Violation(IdentityErrors.PasswordTooLong, "MaxLength", IdentityLimits.PasswordMaxLength));
            return violations; // uzun girdi üzerinde daha fazla iş yapma
        }

        if (ContainsEmailLocalPart(password, email))
        {
            violations.Add(new Violation(IdentityErrors.PasswordContainsEmail));
        }

        if (CommonPasswords.Contains(password))
        {
            violations.Add(new Violation(IdentityErrors.PasswordTooCommon));
        }

        return violations;
    }

    /// <summary>Doğrulayıcı olmayan yollar (handler içi) için: ihlal varsa alan adıyla <see cref="ValidationException"/> fırlatır (400 validation).</summary>
    public static void EnsureValid(string? password, string? email, int minLength, string propertyName)
    {
        var failures = Evaluate(password, email, minLength).Select(v => ToFailure(propertyName, v)).ToList();
        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }
    }

    /// <summary>FluentValidation kuralı: <paramref name="email"/> parolanın e-posta kısmı denetimi için verilir (boşsa denetlenmez).</summary>
    public static IRuleBuilderOptionsConditions<T, string?> MeetsPasswordPolicy<T>(this IRuleBuilder<T, string?> rule, int minLength, Func<T, string?> email) =>
        rule.Custom((password, context) =>
        {
            foreach (var violation in Evaluate(password, email(context.InstanceToValidate), minLength))
            {
                context.AddFailure(ToFailure(context.PropertyPath, violation));
            }
        });

    private static ValidationFailure ToFailure(string propertyName, Violation violation)
    {
        var failure = new ValidationFailure(propertyName, violation.Code);
        if (violation.Placeholder is not null)
        {
            failure.FormattedMessagePlaceholderValues = new Dictionary<string, object> { [violation.Placeholder] = violation.Value! };
        }

        return failure;
    }

    private static bool ContainsEmailLocalPart(string password, string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var at = email.IndexOf('@', StringComparison.Ordinal);
        var local = (at > 0 ? email[..at] : email).Trim();
        return local.Length >= MinLocalPartLength && password.Contains(local, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Gömülü yaygın parola listesi (en sık kullanılan ~200; Türkçe yaygınlar dahil). Karşılaştırma büyük/küçük harf duyarsızdır ve
/// sondaki rakam/noktalama dizisi atılarak da denenir ("Password2026!" → "password"). Ağ çağrısı yok; tamamı bellek içidir.
/// </summary>
public static class CommonPasswords
{
    private static readonly HashSet<string> Set = new(Entries, StringComparer.OrdinalIgnoreCase);

    private static string[] Entries =>
    [
        "password", "password1", "password12", "password123", "password1234", "passw0rd", "p@ssw0rd", "p@ssword", "pa$$word", "passcode",
        "123456", "1234567", "12345678", "123456789", "1234567890", "12345678910", "0123456789", "0987654321", "9876543210", "987654321",
        "111111", "11111111", "1111111111", "000000", "00000000", "0000000000", "121212", "123123", "123321", "112233", "112233445566",
        "654321", "666666", "696969", "777777", "888888", "999999", "159753", "147258369", "123654789", "1q2w3e4r", "1q2w3e4r5t", "1qaz2wsx",
        "1qazxsw2", "q1w2e3r4", "q1w2e3r4t5", "qwerty", "qwerty1", "qwerty12", "qwerty123", "qwerty1234", "qwertyuiop", "qwertyui", "qwertyuiop123",
        "asdfgh", "asdfghjk", "asdfghjkl", "asdfghjkl123", "zxcvbn", "zxcvbnm", "zxcvbnm123", "qazwsx", "qazwsxedc", "qweasdzxc", "azerty", "azertyuiop",
        "abc123", "abc12345", "abcd1234", "abcdef", "abcdefg", "abcdefgh", "abcdefghij", "abcd123456", "aaaaaa", "aaaaaaaa", "aaaaaaaaaa",
        "letmein", "letmein123", "welcome", "welcome1", "welcome123", "welcome1234", "admin", "admin123", "admin1234", "administrator", "admin12345",
        "root", "rootroot", "toor", "test", "test123", "test1234", "testtest", "testing123", "guest", "guest123", "user", "user123", "default",
        "changeme", "changeme123", "change_me", "secret", "secret123", "master", "master123", "login", "login123", "access", "access123",
        "iloveyou", "iloveyou1", "iloveyou123", "loveyou", "lovely", "sunshine", "princess", "princess1", "football", "football1", "baseball",
        "basketball", "soccer", "monkey", "monkey123", "dragon", "dragon123", "shadow", "shadow123", "superman", "batman", "spiderman", "starwars",
        "michael", "jordan23", "hunter", "hunter123", "buster", "ranger", "harley", "trustno1", "mustang", "ashley", "bailey", "charlie", "donald",
        "freedom", "whatever", "hello", "hello123", "hello1234", "helloworld", "hellohello", "cheese", "computer", "internet", "killer", "pepper",
        "summer", "summer2020", "summer2021", "summer2022", "summer2023", "summer2024", "summer2025", "summer2026", "winter2024", "winter2025", "winter2026",
        "spring2025", "spring2026", "autumn2025", "autumn2026", "january2026", "february2026", "march2026", "april2026", "may2026", "june2026", "july2026",
        "august2026", "september2026", "october2026", "november2026", "december2026", "2024password", "2025password", "2026password", "password2024",
        "password2025", "password2026", "company123", "company1234", "crm12345", "crm123456", "crm1234567", "algosense", "algosense123", "algosense1234",
        "sifre", "sifre123", "sifre1234", "sifre12345", "sifre123456", "parola", "parola123", "parola1234", "parola12345", "parolam", "parolam123",
        "sifrem", "sifrem123", "kullanici", "kullanici123", "yonetici", "yonetici123", "merhaba", "merhaba123", "merhaba1234", "turkiye", "turkiye123",
        "istanbul", "istanbul34", "istanbul1234", "ankara", "ankara06", "ankara1234", "izmir", "izmir35", "izmir1234", "bursa16", "adana01", "antalya07",
        "galatasaray", "galatasaray1", "galatasaray1905", "fenerbahce", "fenerbahce1907", "besiktas", "besiktas1903", "trabzonspor", "trabzon61",
        "1905", "1907", "1903", "19051905", "19071907", "19031903", "sevgilim", "askim", "askimsin", "seniseviyorum", "canimsin", "kralbenim",
        "qwerty123456", "123qwe", "123qweasd", "123qweasdzxc", "qwe123", "qwe12345", "asd123", "asd12345", "zxc123", "zaq12wsx", "zaq1zaq1", "1234qwer",
        "12341234", "123412341234", "1234abcd", "abcd12345", "a1b2c3d4", "a1b2c3d4e5", "aa123456", "aa12345678", "pass1234", "pass12345", "pass123456",
    ];

    public static bool Contains(string password)
    {
        if (Set.Contains(password))
        {
            return true;
        }

        // Sondaki rakam ve '!' dizisini at ve tekrar dene ("Password2026!" → "password"). Sezgiseldir: yalnız en yaygın kalıbı yakalar.
        var core = password.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '!');
        return core.Length >= 4 && core.Length < password.Length && Set.Contains(core);
    }
}
