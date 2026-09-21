using System.Text.Json;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Usage;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Platform.Tests.Domain;

/// <summary>Etkin hak birleştirme (plan + istisna), istisna JSON'u, plan yapılandırma doğrulaması, aşım hesabı, CSV biçimlendirme.</summary>
public sealed class EntitlementModelTests
{
    private static readonly PlanLimits Starter = new(5, new Dictionary<string, int?> { ["sales"] = 5000, ["activities"] = 10000 });
    private static readonly IReadOnlyDictionary<string, bool> StarterModules = new Dictionary<string, bool> { ["workflows"] = false, ["marketing"] = true };

    // ---- EffectiveEntitlements.Merge ------------------------------------------------------------------------------------

    [Fact]
    public void Merge_WithoutOverrides_IsThePlan_AndMissingModuleKeysAreOff()
    {
        var effective = EffectiveEntitlements.Merge(Starter, StarterModules, null, GatedModules.All);

        effective.MaxUsers.ShouldBe(5);
        effective.MaxRecords["sales"].ShouldBe(5000);
        effective.Modules.ShouldBe(new Dictionary<string, bool> { ["workflows"] = false, ["commerce"] = false, ["service"] = false, ["marketing"] = true, ["integrations"] = false });
    }

    [Fact]
    public void Merge_PartialOverrides_WinOverThePlan_OthersKeepThePlanValue()
    {
        var overrides = TenantOverrides.FromJson("""{"maxUsers":10,"maxRecords":{"sales":200000},"modules":{"workflows":true}}""");

        var effective = EffectiveEntitlements.Merge(Starter, StarterModules, overrides, GatedModules.All);

        effective.MaxUsers.ShouldBe(10);
        effective.MaxRecords["sales"].ShouldBe(200000);
        effective.MaxRecords["activities"].ShouldBe(10000);
        effective.Modules["workflows"].ShouldBeTrue();
        effective.Modules["marketing"].ShouldBeTrue();
    }

    [Fact]
    public void Merge_MaxUsersNullOverride_MeansExplicitlyUnlimited_WhileAbsentKeyKeepsThePlan()
    {
        var unlimited = EffectiveEntitlements.Merge(Starter, StarterModules, TenantOverrides.FromJson("""{"maxUsers":null}"""), GatedModules.All);
        var absent = EffectiveEntitlements.Merge(Starter, StarterModules, TenantOverrides.FromJson("""{"maxRecords":{"sales":1}}"""), GatedModules.All);

        unlimited.MaxUsers.ShouldBeNull();
        absent.MaxUsers.ShouldBe(5);
    }

    [Fact]
    public void Merge_RecordLimitNullOverride_MeansUnlimitedForThatModule_AndModuleOverrideCanTurnAModuleOff()
    {
        var overrides = TenantOverrides.FromJson("""{"maxRecords":{"sales":null},"modules":{"marketing":false}}""");

        var effective = EffectiveEntitlements.Merge(Starter, StarterModules, overrides, GatedModules.All);

        effective.MaxRecords["sales"].ShouldBeNull();
        effective.Modules["marketing"].ShouldBeFalse();
    }

    [Fact]
    public void Overrides_RoundTripThroughJson_KeepingOnlyTheGivenKeys()
    {
        var json = TenantOverrides.FromJson("""{"maxUsers":null,"maxRecords":{"sales":7},"modules":{"service":true}}""").ToJson();

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("maxUsers").ValueKind.ShouldBe(JsonValueKind.Null);
        document.RootElement.GetProperty("maxRecords").GetProperty("sales").GetInt32().ShouldBe(7);
        document.RootElement.GetProperty("modules").GetProperty("service").GetBoolean().ShouldBeTrue();
        TenantOverrides.FromJson(json).MaxUsersSet.ShouldBeTrue();
        TenantOverrides.None.IsEmpty.ShouldBeTrue();
        TenantOverrides.FromJson("{}").IsEmpty.ShouldBeTrue();
        TenantOverrides.FromJson(null).IsEmpty.ShouldBeTrue();
    }

    // ---- aşım hesabı ---------------------------------------------------------------------------------------------------

    [Fact]
    public void OverLimits_ReportsOnlyFiniteExceededLimits_OfEnabledModules_UsersCountActivePlusPending()
    {
        var usage = new UsageCollection(3, 3, new Dictionary<string, long> { ["sales.records"] = 6000, ["activities.records"] = 5, ["workflows.records"] = 99, ["commerce.records"] = 500 });
        var records = new Dictionary<string, int?> { ["sales"] = 5000, ["activities"] = 10000, ["workflows"] = 10, ["commerce"] = null };
        var modules = new Dictionary<string, bool> { ["workflows"] = false, ["commerce"] = true, ["service"] = false, ["marketing"] = false };

        var over = EntitlementMath.OverLimits(5, records, modules, usage);

        over.Select(o => (o.Limit, o.Module, o.Max, o.Used)).ShouldBe(
        [
            ("users", (string?)null, 5, 6L),
            ("records", "sales", 5000, 6000L),
        ]);
    }

    [Fact]
    public void OverLimits_UsageExactlyAtTheLimit_IsNotOver() =>
        EntitlementMath.OverLimits(3, new Dictionary<string, int?>(), new Dictionary<string, bool>(), new UsageCollection(2, 1, new Dictionary<string, long>())).ShouldBeEmpty();

    // ---- plan yapılandırma doğrulaması ---------------------------------------------------------------------------------

    [Fact]
    public void PlanCatalog_TheShippedDefaults_AreValid()
    {
        var options = ShippedOptions();

        PlanCatalogValidator.Validate(options).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Bad-Code", "code must match")]
    [InlineData("x", "code must match")]
    [InlineData("1abc", "code must match")]
    public void PlanCatalog_RejectsBadCodes(string code, string expected)
    {
        var options = ShippedOptions();
        options.Plans.Add(new PlanDefinition { Code = code, Name = "X" });

        PlanCatalogValidator.Validate(options).ShouldContain(e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void PlanCatalog_RejectsUnknownModuleKeys_NegativeLimits_BadTrialDays_AndDuplicates()
    {
        var options = ShippedOptions();
        options.Plans.Add(new PlanDefinition
        {
            Code = "broken",
            Name = "Broken",
            TrialDays = 0,
            Limits = new PlanLimitsDefinition { MaxUsers = -1, MaxRecords = { ["nonsense"] = 5, ["sales"] = -2 } },
            Modules = { ["identity"] = true, ["workflows"] = true },
        });
        options.Plans.Add(new PlanDefinition { Code = "starter", Name = "Dup" });

        var errors = PlanCatalogValidator.Validate(options);

        errors.ShouldContain(e => e.Contains("trialDays", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("maxUsers", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("unknown module 'nonsense'", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("maxRecords.sales", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("'identity'", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanCatalog_SignupAndDefaultPlanMustBeInTheCatalog()
    {
        var options = ShippedOptions();
        options.Signup.PlanCode = "missing";
        options.Provisioning.DefaultPlanCode = "gone";

        var errors = PlanCatalogValidator.Validate(options);

        errors.ShouldContain(e => e.Contains("Platform:Signup:PlanCode", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("Platform:Provisioning:DefaultPlanCode", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformOptions_InconsistentRanges_AreRejected()
    {
        var options = ShippedOptions();
        options.Deletion.RetentionDays = 5;
        options.Deletion.MaxRetentionDays = 3;
        options.Usage.SnapshotPollMinutes = 0;

        PlanCatalogValidator.ValidateRanges(options).Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    // ---- CSV biçimlendirme -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Acme", "Acme")]
    [InlineData("A,B", "\"A,B\"")]
    [InlineData("Say \"hi\"", "\"Say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("=cmd|' /C calc'!A0", "'=cmd|' /C calc'!A0")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@x", "'@x")]
    [InlineData("\tTab", "'\tTab")]
    [InlineData("\rCR", "\"'\rCR\"")]
    [InlineData("", "")]
    public void Csv_EscapesRfc4180_AndPrefixesFormulaCells(string value, string expected) => CsvFormatter.Text(value).ShouldBe(expected);

    [Fact]
    public void Csv_LinesEndWithCrLf_AndTheBomEncodingEmitsAPreamble()
    {
        CsvFormatter.Line(["a", "b"]).ShouldBe("a,b\r\n");
        CsvFormatter.Utf8WithBom.GetPreamble().ShouldBe([0xEF, 0xBB, 0xBF]);
    }

    private static PlatformOptions ShippedOptions()
    {
        var options = new PlatformOptions();
        options.Plans.Add(new PlanDefinition { Code = "internal", Name = "İç kullanım", Modules = { ["workflows"] = true, ["commerce"] = true, ["service"] = true, ["marketing"] = true } });
        options.Plans.Add(new PlanDefinition
        {
            Code = "starter",
            Name = "Starter",
            SortOrder = 10,
            TrialDays = 14,
            Limits = new PlanLimitsDefinition { MaxUsers = 5, MaxRecords = { ["sales"] = 5000, ["activities"] = 10000 } },
        });
        return options;
    }
}
