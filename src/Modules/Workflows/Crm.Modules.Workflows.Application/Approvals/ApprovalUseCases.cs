using Crm.Modules.Workflows.Application.Executions;
using Crm.Modules.Workflows.Contracts;
using Crm.Modules.Workflows.Domain;
using Crm.Modules.Workflows.Domain.Approvals;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;
using FluentValidation;

namespace Crm.Modules.Workflows.Application.Approvals;

/// <summary>
/// Onay listesi. <c>Mine = true</c> (varsayılan) çağıranın onaylarıdır ve ek izin gerektirmez; <c>Mine = false</c> tüm organizasyonun
/// onaylarını verir ve <c>org.workflows.manage</c> ister. En yeni talep önce.
/// </summary>
public sealed record ListApprovalsQuery(PagedQuery Paging, ApprovalStatus? Status, bool Mine) : IQuery<PagedResult<ApprovalDto>>;

public sealed class ListApprovalsHandler(IWorkflowReadStore store, ICurrentUser user, IPermissionService permissions)
    : IQueryHandler<ListApprovalsQuery, PagedResult<ApprovalDto>>
{
    public async Task<Result<PagedResult<ApprovalDto>>> Handle(ListApprovalsQuery query, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        if (!query.Mine && !await permissions.HasAsync(userId, WorkflowsPermissions.Manage, cancellationToken).ConfigureAwait(false))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, ("permission", WorkflowsPermissions.Manage));
        }

        return await store.ListApprovalsAsync(query.Paging, new ApprovalFilter(query.Status, query.Mine ? userId : null), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Tek onay: çağıranın kendi onayı veya <c>org.workflows.manage</c>; aksi <c>forbidden</c>.</summary>
public sealed record GetApprovalQuery(Guid Id) : IQuery<ApprovalDto>;

public sealed class GetApprovalHandler(IWorkflowReadStore store, ICurrentUser user, IPermissionService permissions) : IQueryHandler<GetApprovalQuery, ApprovalDto>
{
    public async Task<Result<ApprovalDto>> Handle(GetApprovalQuery query, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        var approval = await store.GetApprovalAsync(query.Id, cancellationToken).ConfigureAwait(false);
        if (approval is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (approval.ApproverUserId != userId && !await permissions.HasAsync(userId, WorkflowsPermissions.Manage, cancellationToken).ConfigureAwait(false))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, ("permission", WorkflowsPermissions.Manage));
        }

        return approval;
    }
}

/// <summary>Çağıranın bekleyen onay sayısı (üst çubuk rozeti); her kimliği doğrulanmış üye için 200.</summary>
public sealed record GetApprovalSummaryQuery : IQuery<ApprovalSummaryDto>;

public sealed class GetApprovalSummaryHandler(IWorkflowReadStore store, ICurrentUser user) : IQueryHandler<GetApprovalSummaryQuery, ApprovalSummaryDto>
{
    public async Task<Result<ApprovalSummaryDto>> Handle(GetApprovalSummaryQuery query, CancellationToken cancellationToken) =>
        user.UserId is { } userId
            ? new ApprovalSummaryDto(await store.CountPendingAsync(userId, cancellationToken).ConfigureAwait(false))
            : Error.Unauthorized(ErrorCodes.Unauthenticated);
}

/// <summary>
/// Onay kararı. <c>crm.approvals.decide</c> gerekir ve onay çağırana ait olmalıdır (<c>forbidden</c>); zaten karar verilmiş/iptal
/// ise <c>approval.already_decided</c> (409); reddetmede <c>comment</c> zorunludur (<c>validation</c>). İlk karar sonucu belirler
/// (tek onay yeter): diğer bekleyen onaylar aynı transaction'da iptal edilir ve workflow'un HUMAN görevi tamamlanır.
/// </summary>
[RequiresPermission(WorkflowsPermissions.ApprovalsDecide)]
public sealed record DecideApprovalCommand(Guid Id, ApprovalDecision? Decision, string? Comment) : ICommand;

public sealed class DecideApprovalValidator : AbstractValidator<DecideApprovalCommand>
{
    public DecideApprovalValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Decision).NotNull();
        RuleFor(x => x.Comment).MaximumLength(WorkflowLimits.CommentMaxLength);
        RuleFor(x => x.Comment).NotEmpty().When(x => x.Decision == ApprovalDecision.Reject);
    }
}

public sealed class DecideApprovalHandler(
    IApprovalRepository approvals,
    IWorkflowExecutionRepository executions,
    IWorkflowEngine engine,
    ICurrentUser user,
    TimeProvider clock) : ICommandHandler<DecideApprovalCommand>
{
    public async Task<Result> Handle(DecideApprovalCommand command, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        var approval = await approvals.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (approval is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (approval.ApproverUserId != userId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var decision = command.Decision!.Value;
        var decided = approval.Decide(decision, command.Comment, now);
        if (decided.IsFailure)
        {
            return decided;
        }

        // İlk karar sonucu belirler: diğer bekleyen onaylar (aynı transaction'da) iptal edilir.
        await ApprovalCancellation.CancelPendingAsync(approvals, approval.ExecutionId, now, cancellationToken).ConfigureAwait(false);

        // Workflow'un HUMAN görevini tamamla. Motor reddederse hiçbir şey kaydedilmez (istek hata döner, tekrar denenebilir).
        var execution = await executions.GetByIdAsync(approval.ExecutionId, cancellationToken).ConfigureAwait(false);
        if (execution is { IsRunning: true, EngineWorkflowId: { } engineId })
        {
            try
            {
                await engine.CompleteWaitTaskAsync(
                    engineId,
                    WorkflowNames.WaitDecisionTaskRef,
                    new Dictionary<string, object?>
                    {
                        ["decision"] = decision == ApprovalDecision.Approve ? "approved" : "rejected",
                        ["comment"] = approval.Comment,
                        ["approverUserId"] = userId.ToString(),
                        ["approvalId"] = approval.Id.ToString(),
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Error.Failure(WorkflowsErrors.EngineUnavailable);
            }
        }

        return Result.Success();
    }
}
