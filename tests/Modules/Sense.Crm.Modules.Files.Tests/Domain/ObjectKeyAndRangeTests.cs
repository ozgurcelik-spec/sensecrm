using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Application.Files;
using Sense.Crm.Modules.Files.Domain;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Files.Tests.Domain;

public sealed class ObjectKeyTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid File = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void For_BuildsTheDocumentedLayout()
    {
        var key = ObjectKey.For(Tenant, 2026, File);
        key.ToString().ShouldBe("11111111-2222-3333-4444-555555555555/2026/aaaaaaaabbbbccccddddeeeeeeeeeeee");
        ObjectKey.TenantPrefix(Tenant).ShouldBe("11111111-2222-3333-4444-555555555555/");
    }

    [Fact]
    public void Parse_RoundTripsAndKeepsTheFullTenantGuid()
    {
        var key = ObjectKey.For(Tenant, 2027, File);
        var parsed = ObjectKey.Parse(key.ToString());
        parsed.ShouldBe(key);
        parsed.TenantId.ShouldBe(Tenant);
        parsed.Year.ShouldBe(2027);
        parsed.FileId.ShouldBe(File);
    }

    [Theory]
    [InlineData("")]
    [InlineData("11111111-2222-3333-4444-555555555555/2026")]
    [InlineData("11111111-2222-3333-4444-555555555555/2026/aaaaaaaabbbbccccddddeeeeeeeeeeee/extra")]
    [InlineData("11111111-2222-3333-4444-555555555555/2026/../aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("11111111-2222-3333-4444-555555555555/../aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("11111111-2222-3333-4444-555555555555/2026/AAAAAAAABBBBCCCCDDDDEEEEEEEEEEEE")]
    [InlineData("11111111-2222-3333-4444-55555555555F/2026/aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("11111111222233334444555555555555/2026/aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("11111111-2222-3333-4444-555555555555/2026/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("11111111-2222-3333-4444-555555555555/26/aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("11111111-2222-3333-4444-555555555555/1999/aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("00000000-0000-0000-0000-000000000000/2026/aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("11111111-2222-3333-4444-555555555555/2026/00000000000000000000000000000000")]
    [InlineData("11111111-2222-3333-4444-555555555555/2026/aaaaaaaabbbbccccddddeeeeeeeeeeee\n")]
    [InlineData("/11111111-2222-3333-4444-555555555555/2026/aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    public void Parse_RejectsAnythingThatIsNotTheStrictLayout(string value)
    {
        ObjectKey.TryParse(value, out _).ShouldBeFalse();
        Should.Throw<FormatException>(() => ObjectKey.Parse(value));
    }

    [Fact]
    public void For_RejectsEmptyIdsAndAbsurdYears()
    {
        Should.Throw<ArgumentException>(() => ObjectKey.For(Guid.Empty, 2026, File));
        Should.Throw<ArgumentException>(() => ObjectKey.For(Tenant, 2026, Guid.Empty));
        Should.Throw<ArgumentOutOfRangeException>(() => ObjectKey.For(Tenant, 1500, File));
    }

    [Fact]
    public void Parse_Null_IsRejected() => ObjectKey.TryParse(null, out _).ShouldBeFalse();
}

public sealed class HttpRangeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-5")]
    [InlineData("bytes=0-1,5-6")]
    public void Parse_NoHeaderOtherUnitOrMultipleRanges_MeansFullBody(string? header) =>
        HttpRange.Parse(header, 100).Kind.ShouldBe(RangeKind.None);

    [Theory]
    [InlineData("bytes=0-0", 0, 0)]
    [InlineData("bytes=0-9", 0, 9)]
    [InlineData("bytes=10-", 10, 99)]
    [InlineData("bytes=99-99", 99, 99)]
    [InlineData("bytes=90-500", 90, 99)]
    [InlineData("bytes=-10", 90, 99)]
    [InlineData("bytes=-500", 0, 99)]
    [InlineData("BYTES=5-6", 5, 6)]
    public void Parse_SingleRange_IsSatisfiable(string header, long start, long end)
    {
        var range = HttpRange.Parse(header, 100);
        range.Kind.ShouldBe(RangeKind.Satisfiable);
        range.Start.ShouldBe(start);
        range.End.ShouldBe(end);
    }

    [Theory]
    [InlineData("bytes=100-")]
    [InlineData("bytes=100-200")]
    [InlineData("bytes=5-4")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=abc-")]
    [InlineData("bytes=1-x")]
    [InlineData("bytes=5")]
    [InlineData("bytes=-")]
    [InlineData("bytes=-5-")]
    public void Parse_InvalidOrOutOfObject_IsNotSatisfiable(string header) =>
        HttpRange.Parse(header, 100).Kind.ShouldBe(RangeKind.NotSatisfiable);

    [Theory]
    [InlineData("\"abc\"", "\"abc\"", true)]
    [InlineData("W/\"abc\"", "\"abc\"", true)]
    [InlineData("*", "\"abc\"", true)]
    [InlineData("\"x\", \"abc\"", "\"abc\"", true)]
    [InlineData("\"x\"", "\"abc\"", false)]
    [InlineData("", "\"abc\"", false)]
    public void MatchesETag_HandlesListsWeakPrefixAndWildcard(string header, string etag, bool expected) =>
        HttpRange.MatchesETag(header, etag).ShouldBe(expected);
}

public sealed class FilesOptionsValidatorTests
{
    [Fact]
    public void Defaults_AreValid_InDevelopment() => FilesOptionsValidator.Validate(new FilesOptions(), isProduction: false).ShouldBeEmpty();

    [Fact]
    public void Production_RequiresTheS3Provider()
    {
        var options = new FilesOptions { Storage = { Provider = "filesystem", Encryption = "required" } };
        FilesOptionsValidator.Validate(options, isProduction: true).ShouldContain(e => e.Contains("'s3' in Production", StringComparison.Ordinal));

        var memory = new FilesOptions { Storage = { Provider = "memory", Encryption = "required" } };
        FilesOptionsValidator.Validate(memory, isProduction: true).ShouldNotBeEmpty();
    }

    [Fact]
    public void Production_RejectsUnencryptedUnlessAcknowledged()
    {
        var options = new FilesOptions { Storage = { Provider = "s3", Endpoint = "http://minio:9000", Encryption = "none" } };
        FilesOptionsValidator.Validate(options, isProduction: true).ShouldContain(e => e.Contains("Encryption 'none'", StringComparison.Ordinal));

        options.Storage.AcknowledgeUnencrypted = true;
        FilesOptionsValidator.Validate(options, isProduction: true).ShouldBeEmpty();
        FilesOptionsValidator.Validate(new FilesOptions { Storage = { Provider = "s3", Endpoint = "http://minio:9000", Encryption = "required" } }, isProduction: true).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0, 10, 10, false)]
    [InlineData(201, 10, 10, false)]
    [InlineData(25, 10, 24, false)]
    [InlineData(25, 10, 251, false)]
    [InlineData(25, 10, 25, true)]
    [InlineData(25, 10, 250, true)]
    [InlineData(1, 1, 1, true)]
    [InlineData(200, 1, 200, true)]
    public void UploadRanges_AreCheckedAtStartup(int maxFileMb, int maxFiles, int maxRequestMb, bool valid)
    {
        var options = new FilesOptions { Upload = { MaxFileMb = maxFileMb, MaxFilesPerRequest = maxFiles, MaxRequestMb = maxRequestMb } };
        (FilesOptionsValidator.Validate(options, isProduction: false).Count == 0).ShouldBe(valid);
    }

    [Fact]
    public void AllowedExtensions_CanOnlyNarrowTheCatalog()
    {
        var narrowed = new FilesOptions { Upload = { AllowedExtensions = ["pdf", "PNG"] } };
        FilesOptionsValidator.Validate(narrowed, isProduction: false).ShouldBeEmpty();
        FilesOptionsValidator.EffectiveExtensions(narrowed.Upload).OrderBy(e => e).ShouldBe(["pdf", "png"]);

        var widened = new FilesOptions { Upload = { AllowedExtensions = ["pdf", "exe"] } };
        FilesOptionsValidator.Validate(widened, isProduction: false).ShouldContain(e => e.Contains("'exe'", StringComparison.Ordinal));

        FilesOptionsValidator.EffectiveExtensions(new UploadOptions()).ShouldBe(FileTypeCatalog.AllExtensions, ignoreOrder: true);
    }

    [Fact]
    public void ScannerProvider_MustBeNone_UntilAnAdapterExists() =>
        FilesOptionsValidator.Validate(new FilesOptions { Scanner = { Provider = "clamav" } }, isProduction: false).ShouldNotBeEmpty();
}
