namespace Sense.Crm.Spikes.M9h.Infra;

/// <summary>Ölçüm/SQL kanıtlarını <c>spikes/m9h-record-scope/evidence/*.log</c> dosyasına yazar (test çıktısı yakalanmadığı için); findings belgesi buradan alıntılar.</summary>
public static class Evidence
{
    private static readonly object Gate = new();
    private static readonly string Dir = ResolveDir();

    public static void Log(string text, string file = "evidence.txt")
    {
        lock (Gate)
        {
            File.AppendAllText(Path.Combine(Dir, file), text + Environment.NewLine + Environment.NewLine);
        }
    }

    public static void Write(string file, string text)
    {
        lock (Gate)
        {
            File.WriteAllText(Path.Combine(Dir, file), text);
        }
    }

    private static string ResolveDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Sense.Crm.Spikes.M9hRecordScope.Tests.csproj")))
        {
            d = d.Parent;
        }

        var dir = Path.Combine(d?.FullName ?? AppContext.BaseDirectory, "evidence");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
