using NSubstitute;
using Sense.Crm.Modules.Commerce.Application;
using Sense.Crm.Modules.Commerce.Application.Products;
using Sense.Crm.Modules.Identity.Application;
using Sense.Crm.Modules.Identity.Application.Members;
using Sense.Crm.Modules.Marketing.Application;
using Sense.Crm.Modules.Marketing.Application.Campaigns;
using Sense.Crm.Modules.Sales.Application.Accounts;
using Sense.Crm.Modules.Service.Application;
using Sense.Crm.Modules.Service.Application.Cases;
using Sense.Crm.Modules.Workflows.Application;
using Sense.Crm.Modules.Workflows.Application.Rules;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Infrastructure.Messaging.Behaviours;
using Sense.Crm.Shared.Kernel.Results;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Platform.Tests.Domain;

// Bu test assembly'sinin adı Sense.Crm.Modules.Platform.Tests olduğundan aşağıdaki sahte istekler "platform" modülünden sayılır (kapı modülü değil).

/// <summary>Sahte sorgu (kapı olmayan modül).</summary>
public sealed record AEntQuery : IQuery<int>;

/// <summary>Sahte komut (kapı olmayan modül).</summary>
public sealed record AEntCommand : ICommand;

[TenantStatusExempt("test: kullanıcı-düzeyi sorgu")]
public sealed record AEntExemptQuery : IQuery<int>;

[TenantStatusExempt("test: kullanıcı-düzeyi komut")]
public sealed record AEntExemptCommand : ICommand;

[PlatformAdminOnly]
public sealed record AEntPlatformCommand : ICommand;

[PlatformAdminOnly]
public sealed record AEntPlatformQuery : IQuery<int>;

[ConsumesLimit(LimitKeys.Records, 3)]
public sealed record AEntBulkCommand : ICommand;

/// <summary>
/// <c>EntitlementBehaviour</c> zorlama matrisi (saf birim testi; veritabanı yok): {etkin/deneme, deneme bitmiş, askı readOnly, askı blocked, silme bekleyen} ×
/// {sorgu, komut, muaf sorgu/komut, kapı modülü açık/kapalı sorgu ve komut, platform yöneticisi isteği, sistem bağlamı, anonim} → beklenen hata kodu/türü.
/// Kapı modülü kararı isteğin assembly'sinden türer: bu yüzden kapı modülü satırları gerçek Application istekleriyle kurulur.
/// </summary>
public sealed class EntitlementBehaviourMatrixTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private const string Pass = "pass";

    private static string Suspended(string reason) => $"suspended:{reason}";

    private static string Module(string module) => $"module:{module}";

    public enum LifecycleState
    {
        Active,
        Trial,
        TrialExpired,
        SuspendedReadOnly,
        SuspendedBlocked,
        PendingDeletion,
    }

    public enum RequestKind
    {
        Query,
        Command,
        ExemptQuery,
        ExemptCommand,
        PlatformQuery,
        PlatformCommand,
        MarketingQuery,
        MarketingCommand,
    }

    private sealed record Deps(ITenantContext Tenant, ICurrentUser User, ITenantEntitlements Entitlements, ILimitGuard Limits, TestClock Clock);

    private static Deps NewDeps(EntitlementSnapshot snapshot, bool tenantResolved = true, Guid? userId = null, bool systemContext = false)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.IsResolved.Returns(tenantResolved);
        tenant.TenantId.Returns(snapshot.TenantId);

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(systemContext ? null : userId ?? Guid.NewGuid());

        var entitlements = Substitute.For<ITenantEntitlements>();
        entitlements.GetAsync(snapshot.TenantId, Arg.Any<CancellationToken>()).Returns(snapshot);

        var limits = Substitute.For<ILimitGuard>();
        limits.EnsureAsync(Arg.Any<LimitDemand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());

        return new Deps(tenant, user, entitlements, limits, new TestClock(Now));
    }

    private static EntitlementSnapshot Snapshot(LifecycleState state, IReadOnlyDictionary<string, bool>? modules = null)
    {
        var (raw, mode, trialEndsAt) = state switch
        {
            LifecycleState.Active => ("active", (string?)null, (DateTimeOffset?)null),
            LifecycleState.Trial => ("active", null, Now.AddDays(1)),
            LifecycleState.TrialExpired => ("active", null, Now.AddSeconds(-1)),
            LifecycleState.SuspendedReadOnly => ("suspended", SuspensionModes.ReadOnly, null),
            LifecycleState.SuspendedBlocked => ("suspended", SuspensionModes.Blocked, null),
            LifecycleState.PendingDeletion => ("pending_deletion", null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

        return new EntitlementSnapshot(
            Guid.NewGuid(),
            "test",
            "Test",
            raw,
            mode,
            trialEndsAt,
            trialEndsAt is null ? null : DateOnly.FromDateTime(trialEndsAt.Value.UtcDateTime),
            "UTC",
            modules ?? AllModules(true),
            MaxUsers: null,
            new Dictionary<string, int?>());
    }

    private static Dictionary<string, bool> AllModules(bool enabled) => GatedModules.All.ToDictionary(m => m, _ => enabled, StringComparer.Ordinal);

    private static Dictionary<string, bool> OnlyModule(string module) => GatedModules.All.ToDictionary(m => m, m => m == module, StringComparer.Ordinal);

    private static async Task<(Result Result, bool NextCalled)> RunAsync<TRequest, TResponse>(TRequest request, Deps deps, TResponse success)
        where TRequest : notnull
        where TResponse : Result
    {
        var nextCalled = false;
        var behaviour = new EntitlementBehaviour<TRequest, TResponse>(deps.Tenant, deps.User, deps.Entitlements, deps.Limits, deps.Clock);
        var result = await behaviour.Handle(
            request,
            () =>
            {
                nextCalled = true;
                return Task.FromResult(success);
            },
            TestContext.Current.CancellationToken);
        return (result, nextCalled);
    }

    private static Result<T> Ok<T>() => Result.Success<T>(default!);

    private static Task<(Result Result, bool NextCalled)> RunKindAsync(RequestKind kind, Deps deps) => kind switch
    {
        RequestKind.Query => RunAsync(new AEntQuery(), deps, Ok<int>()),
        RequestKind.Command => RunAsync(new AEntCommand(), deps, Result.Success()),
        RequestKind.ExemptQuery => RunAsync(new AEntExemptQuery(), deps, Ok<int>()),
        RequestKind.ExemptCommand => RunAsync(new AEntExemptCommand(), deps, Result.Success()),
        RequestKind.PlatformQuery => RunAsync(new AEntPlatformQuery(), deps, Ok<int>()),
        RequestKind.PlatformCommand => RunAsync(new AEntPlatformCommand(), deps, Result.Success()),
        RequestKind.MarketingQuery => RunAsync(new GetCampaignQuery(Guid.NewGuid()), deps, Ok<CampaignDto>()),
        RequestKind.MarketingCommand => RunAsync(new DeleteCampaignCommand(Guid.NewGuid()), deps, Result.Success()),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Sonucu tablo dilimine çevirir: <c>pass</c> | <c>suspended:{reason}</c> | <c>module:{module}</c> (hata türü ve "sonraki adım çağrılmadı" ayrıca doğrulanır).</summary>
    private static string Describe((Result Result, bool NextCalled) outcome)
    {
        if (outcome.Result.IsSuccess)
        {
            outcome.NextCalled.ShouldBeTrue();
            return Pass;
        }

        outcome.NextCalled.ShouldBeFalse("reddedilen istek işleyiciye ulaşmamalı");
        var error = outcome.Result.Error;
        switch (error.Code)
        {
            case EntitlementErrors.TenantSuspended:
                error.Type.ShouldBe(ErrorType.Forbidden);
                return Suspended((string)error.Args![EntitlementErrors.ReasonArg]!);
            case EntitlementErrors.ModuleDisabled:
                error.Type.ShouldBe(ErrorType.Forbidden);
                return Module((string)error.Args![EntitlementErrors.ModuleArg]!);
            default:
                return $"other:{error.Code}";
        }
    }

    // Durum sırası: Active, Trial, TrialExpired, SuspendedReadOnly, SuspendedBlocked, PendingDeletion.
    private static readonly string[] ReadRow = [Pass, Pass, Pass, Pass, "suspended:suspended", "suspended:pending_deletion"];

    private static readonly string[] WriteRow = [Pass, Pass, "suspended:trial_expired", "suspended:suspended", "suspended:suspended", "suspended:pending_deletion"];

    private static readonly string[] AllPassRow = [Pass, Pass, Pass, Pass, Pass, Pass];

    private static readonly string[] MarketingOffReadRow = [Module("marketing"), Module("marketing"), Module("marketing"), Module("marketing"), "suspended:suspended", "suspended:pending_deletion"];

    private static readonly string[] MarketingOffWriteRow = [Module("marketing"), Module("marketing"), "suspended:trial_expired", "suspended:suspended", "suspended:suspended", "suspended:pending_deletion"];

    private static readonly (RequestKind Kind, bool ModulesEnabled, string[] Expected)[] Table =
    [
        (RequestKind.Query, true, ReadRow),
        (RequestKind.Command, true, WriteRow),
        (RequestKind.ExemptQuery, true, AllPassRow),
        (RequestKind.ExemptCommand, true, AllPassRow),
        (RequestKind.PlatformQuery, true, AllPassRow),
        (RequestKind.PlatformCommand, true, AllPassRow),
        (RequestKind.PlatformCommand, false, AllPassRow),
        (RequestKind.MarketingQuery, true, ReadRow),
        (RequestKind.MarketingCommand, true, WriteRow),
        (RequestKind.MarketingQuery, false, MarketingOffReadRow),
        (RequestKind.MarketingCommand, false, MarketingOffWriteRow),
    ];

    public static TheoryData<RequestKind, bool, LifecycleState, string> Matrix()
    {
        var data = new TheoryData<RequestKind, bool, LifecycleState, string>();
        foreach (var (kind, modulesEnabled, expected) in Table)
        {
            foreach (var state in Enum.GetValues<LifecycleState>())
            {
                data.Add(kind, modulesEnabled, state, expected[(int)state]);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Request_IsAllowedOrRejected_AccordingToLifecycleStateAndPlanModules(RequestKind kind, bool modulesEnabled, LifecycleState state, string expected)
    {
        var deps = NewDeps(Snapshot(state, AllModules(modulesEnabled)));

        var outcome = await RunKindAsync(kind, deps);

        Describe(outcome).ShouldBe(expected);
    }

    [Theory]
    [InlineData(RequestKind.PlatformQuery)]
    [InlineData(RequestKind.PlatformCommand)]
    public async Task PlatformAdminOnlyRequest_SkipsTheEntitlementLookupEntirely(RequestKind kind)
    {
        var deps = NewDeps(Snapshot(LifecycleState.SuspendedBlocked, AllModules(false)));

        var outcome = await RunKindAsync(kind, deps);

        outcome.Result.IsSuccess.ShouldBeTrue();
        await deps.Entitlements.DidNotReceiveWithAnyArgs().GetAsync(Guid.Empty, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(LifecycleState.Active)]
    [InlineData(LifecycleState.TrialExpired)]
    [InlineData(LifecycleState.SuspendedBlocked)]
    [InlineData(LifecycleState.PendingDeletion)]
    public async Task SystemContext_UserIdNull_IsNeverGated_EvenForDisabledModuleWritesAndBlockedTenants(LifecycleState state)
    {
        var deps = NewDeps(Snapshot(state, AllModules(false)), systemContext: true);

        var write = await RunKindAsync(RequestKind.MarketingCommand, deps);
        var read = await RunKindAsync(RequestKind.Query, deps);

        write.Result.IsSuccess.ShouldBeTrue();
        read.Result.IsSuccess.ShouldBeTrue();
        await deps.Entitlements.DidNotReceiveWithAnyArgs().GetAsync(Guid.Empty, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(LifecycleState.Active)]
    [InlineData(LifecycleState.SuspendedBlocked)]
    [InlineData(LifecycleState.PendingDeletion)]
    public async Task UnresolvedTenant_Anonymous_IsNeverGated(LifecycleState state)
    {
        var deps = NewDeps(Snapshot(state, AllModules(false)), tenantResolved: false);

        var write = await RunKindAsync(RequestKind.Command, deps);
        var gated = await RunKindAsync(RequestKind.MarketingQuery, deps);

        write.Result.IsSuccess.ShouldBeTrue();
        gated.Result.IsSuccess.ShouldBeTrue();
        await deps.Entitlements.DidNotReceiveWithAnyArgs().GetAsync(Guid.Empty, TestContext.Current.CancellationToken);
    }

    // ---- Her kapı modülü (workflows | commerce | service | marketing): gerçek istekle, okuma ve yazma -------------------------

    private static Task<(Result Result, bool NextCalled)> GatedRead(string module, Deps deps) => module switch
    {
        GatedModules.Marketing => RunAsync(new GetCampaignQuery(Guid.NewGuid()), deps, Ok<CampaignDto>()),
        GatedModules.Commerce => RunAsync(new GetProductQuery(Guid.NewGuid()), deps, Ok<ProductDto>()),
        GatedModules.Service => RunAsync(new GetCaseSummaryQuery(), deps, Ok<CaseSummaryDto>()),
        GatedModules.Workflows => RunAsync(new GetRuleQuery(Guid.NewGuid()), deps, Ok<RuleDto>()),
        _ => throw new ArgumentOutOfRangeException(nameof(module)),
    };

    private static Task<(Result Result, bool NextCalled)> GatedWrite(string module, Deps deps) => module switch
    {
        GatedModules.Marketing => RunAsync(new DeleteCampaignCommand(Guid.NewGuid()), deps, Result.Success()),
        GatedModules.Commerce => RunAsync(new DeleteProductCommand(Guid.NewGuid()), deps, Result.Success()),
        GatedModules.Service => RunAsync(new DeleteCaseCommand(Guid.NewGuid()), deps, Result.Success()),
        GatedModules.Workflows => RunAsync(new DeleteRuleCommand(Guid.NewGuid()), deps, Result.Success()),
        _ => throw new ArgumentOutOfRangeException(nameof(module)),
    };

    [Theory]
    [InlineData(GatedModules.Workflows)]
    [InlineData(GatedModules.Commerce)]
    [InlineData(GatedModules.Service)]
    [InlineData(GatedModules.Marketing)]
    public async Task GatedModule_WhenItsPlanFlagIsOff_BlocksReadsAndWrites_EvenWithEveryOtherModuleOn(string module)
    {
        var flags = AllModules(true);
        flags[module] = false;
        var deps = NewDeps(Snapshot(LifecycleState.Active, flags));

        Describe(await GatedRead(module, deps)).ShouldBe(Module(module));
        Describe(await GatedWrite(module, deps)).ShouldBe(Module(module));
    }

    [Theory]
    [InlineData(GatedModules.Workflows)]
    [InlineData(GatedModules.Commerce)]
    [InlineData(GatedModules.Service)]
    [InlineData(GatedModules.Marketing)]
    public async Task GatedModule_WhenOnlyItsPlanFlagIsOn_PassesReadsAndWrites(string module)
    {
        var deps = NewDeps(Snapshot(LifecycleState.Active, OnlyModule(module)));

        Describe(await GatedRead(module, deps)).ShouldBe(Pass);
        Describe(await GatedWrite(module, deps)).ShouldBe(Pass);
    }

    [Fact]
    public async Task GatedModule_MissingFlagInTheSnapshot_CountsAsDisabled()
    {
        var deps = NewDeps(Snapshot(LifecycleState.Active, new Dictionary<string, bool>()));

        Describe(await GatedRead(GatedModules.Service, deps)).ShouldBe(Module("service"));
    }

    [Fact]
    public async Task NonGatedModule_IsNeverBlockedByModuleFlags()
    {
        var deps = NewDeps(Snapshot(LifecycleState.Active, AllModules(false)));

        var identity = await RunAsync(new ListMembersQuery(), deps, Ok<IReadOnlyList<MemberDto>>());
        var sales = await RunAsync(new CreateAccountCommand("Acme", null, null, null, null, null, null, null), deps, Ok<Guid>());

        Describe(identity).ShouldBe(Pass);
        Describe(sales).ShouldBe(Pass);
    }

    // ---- [ConsumesLimit] -------------------------------------------------------------------------------------------------------

    public static TheoryData<string> ConsumingCommands() => ["sales", "marketing", "commerce", "service", "workflows", "identity", "platform-bulk"];

    private static (Task<(Result Result, bool NextCalled)> Run, LimitDemand Expected) Consuming(string name, Deps deps) => name switch
    {
        "sales" => (RunAsync(new CreateAccountCommand("Acme", null, null, null, null, null, null, null), deps, Ok<Guid>()), new LimitDemand(LimitKeys.Records, "sales")),
        "marketing" => (RunAsync(new CreateCampaignCommand("Kampanya", null, null, null, null, null, null, null, null, null, null), deps, Ok<Guid>()), new LimitDemand(LimitKeys.Records, "marketing")),
        "commerce" => (RunAsync(new CreateProductCommand("Urun", null, null, 1m, null, 0m, null, null), deps, Ok<ProductDto>()), new LimitDemand(LimitKeys.Records, "commerce")),
        "service" => (RunAsync(new CreateCaseCommand("Konu", null, null, null, null, null, null), deps, Ok<Guid>()), new LimitDemand(LimitKeys.Records, "service")),
        "workflows" => (RunAsync(new CreateRuleCommand("Kural", null, null, null), deps, Ok<Guid>()), new LimitDemand(LimitKeys.Records, "workflows")),
        "identity" => (RunAsync(new AddMemberCommand("uye@example.com", "Uye", Guid.NewGuid()), deps, Ok<AddMemberResultDto>()), new LimitDemand(LimitKeys.Users, "identity")),
        "platform-bulk" => (RunAsync(new AEntBulkCommand(), deps, Result.Success()), new LimitDemand(LimitKeys.Records, "platform", 3)),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(ConsumingCommands))]
    public async Task ConsumesLimit_Command_AsksTheGuard_WithTheModuleOfTheRequestAssembly_AndTheDeclaredAmount(string name)
    {
        var deps = NewDeps(Snapshot(LifecycleState.Active));
        var (run, expected) = Consuming(name, deps);

        var outcome = await run;

        Describe(outcome).ShouldBe(Pass);
        await deps.Limits.Received(1).EnsureAsync(expected, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumesLimit_FailingGuard_IsReturnedAsIs_As402PaymentError_AndTheHandlerIsNotReached()
    {
        var deps = NewDeps(Snapshot(LifecycleState.Active));
        deps.Limits.EnsureAsync(Arg.Any<LimitDemand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(EntitlementErrors.Exceeded(LimitKeys.Records, "platform", max: 5, used: 5)));

        var (result, nextCalled) = await RunAsync(new AEntBulkCommand(), deps, Result.Success());

        nextCalled.ShouldBeFalse();
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(EntitlementErrors.LimitExceeded);
        result.Error.Type.ShouldBe(ErrorType.Payment);
        result.Error.Args![EntitlementErrors.LimitArg].ShouldBe(LimitKeys.Records);
        result.Error.Args[EntitlementErrors.ModuleArg].ShouldBe("platform");
        result.Error.Args[EntitlementErrors.MaxArg].ShouldBe(5L);
        result.Error.Args[EntitlementErrors.UsedArg].ShouldBe(5L);
    }

    [Fact]
    public async Task ConsumesLimit_IsNotChecked_WhenTheStatusAlreadyRejectsTheRequest()
    {
        var readOnly = NewDeps(Snapshot(LifecycleState.SuspendedReadOnly));
        var blocked = NewDeps(Snapshot(LifecycleState.PendingDeletion));

        var (a, _) = await RunAsync(new AEntBulkCommand(), readOnly, Result.Success());
        var (b, _) = await RunAsync(new AEntBulkCommand(), blocked, Result.Success());

        a.Error.Code.ShouldBe(EntitlementErrors.TenantSuspended);
        b.Error.Code.ShouldBe(EntitlementErrors.TenantSuspended);
        await readOnly.Limits.DidNotReceiveWithAnyArgs().EnsureAsync(default!, TestContext.Current.CancellationToken);
        await blocked.Limits.DidNotReceiveWithAnyArgs().EnsureAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConsumesLimit_IsNotChecked_WhenTheModuleIsDisabled()
    {
        var flags = AllModules(true);
        flags[GatedModules.Marketing] = false;
        var deps = NewDeps(Snapshot(LifecycleState.Active, flags));

        var outcome = await RunAsync(new CreateCampaignCommand("Kampanya", null, null, null, null, null, null, null, null, null, null), deps, Ok<Guid>());

        Describe(outcome).ShouldBe(Module("marketing"));
        await deps.Limits.DidNotReceiveWithAnyArgs().EnsureAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Requests_WithoutConsumesLimit_NeverCallTheGuard()
    {
        var deps = NewDeps(Snapshot(LifecycleState.Active));

        await RunKindAsync(RequestKind.Command, deps);
        await RunKindAsync(RequestKind.Query, deps);
        await RunKindAsync(RequestKind.MarketingCommand, deps);

        await deps.Limits.DidNotReceiveWithAnyArgs().EnsureAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TrialExpiry_IsEvaluatedAtEveryCall_AgainstTheCurrentClock_NotAtSnapshotTime()
    {
        var deps = NewDeps(Snapshot(LifecycleState.Trial));

        Describe(await RunKindAsync(RequestKind.Command, deps)).ShouldBe(Pass);

        deps.Clock.SetUtcNow(Now.AddDays(1)); // deneme bitişi tam bu an (now >= trialEndsAt)

        Describe(await RunKindAsync(RequestKind.Command, deps)).ShouldBe("suspended:trial_expired");
        Describe(await RunKindAsync(RequestKind.Query, deps)).ShouldBe(Pass);

        deps.Clock.SetUtcNow(Now.AddDays(1).AddSeconds(-1)); // bir saniye önce hâlâ deneme

        Describe(await RunKindAsync(RequestKind.Command, deps)).ShouldBe(Pass);
    }
}
