namespace Sense.Crm.Modules.Workflows.Domain;

/// <summary>
/// Workflows hata kodları (= web istemcisiyle sözleşme + yerelleştirme anahtarları, docs/plan/m4-workflow.md).
/// Ortak kodlar (validation, forbidden, not_found) <c>Sense.Crm.Shared.Kernel.Results.ErrorCodes</c>'tadır.
/// </summary>
public static class WorkflowsErrors
{
    /// <summary>Kuraldaki rol (atanan/onaylayıcı) aktif organizasyonda yok → 400.</summary>
    public const string RoleNotFound = "workflow.role_not_found";

    /// <summary>Yürütme çalışmıyor (terminate) → 409.</summary>
    public const string NotRunning = "workflow.not_running";

    /// <summary>Yürütme başarısız değil (retry) → 409.</summary>
    public const string NotFailed = "workflow.not_failed";

    /// <summary>Workflow motoru (Conductor) yanıt vermiyor/reddetti → 500.</summary>
    public const string EngineUnavailable = "workflow.engine_unavailable";

    /// <summary>Onay zaten karara bağlanmış veya iptal edilmiş → 409.</summary>
    public const string ApprovalAlreadyDecided = "approval.already_decided";

    /// <summary>Reddetmede yorum zorunlu → 400 (alan hatası <c>comment</c>).</summary>
    public const string CommentRequired = "approval.comment_required";

    /// <summary>Kural parametreleri şemaya uymuyor (doğrulama mesajı anahtarı).</summary>
    public const string InvalidParams = "validation.workflow_params";

    // Yürütme hata kodları (workflow_executions.error; istemci çevirir).

    /// <summary>Atanacak rolde aktif üye yok; lead sahibi değişmez.</summary>
    public const string NoAssignee = "no_assignee";

    /// <summary>
    /// Onaylayıcı yok: rolde aktif üye yok ya da tek üye fırsatın sahibi (sahip kendi fırsatını onaylayamaz, L3).
    /// </summary>
    public const string NoApprover = "no_approver";

    /// <summary>
    /// Görev girdisi güvenilmez (H1): <c>(tenantId, executionId, motor workflow kimliği)</c> üçlüsü çalışan bir yürütmeyle eşleşmiyor
    /// (sahte/başka kiracı/başka örnek/bitmiş yürütme). Yan etkisiz, terminal hata.
    /// </summary>
    public const string UntrustedTask = "untrusted_task";

    /// <summary>Yürütme motor kimliğini henüz kaydetmedi (başlatma ile kayıt arasındaki kısa yarış): geçici hata, motor yeniden dener.</summary>
    public const string EngineIdPending = "engine_id_pending";

    /// <summary>Yürütmenin kuralı silinmiş: rol/parametreler okunamaz, terminal hata.</summary>
    public const string RuleNotFound = "rule_not_found";

    /// <summary>Karar veritabanındaki onay kayıtlarından türetilir; henüz karar yok (commit gecikmesi olabilir: geçici hata).</summary>
    public const string DecisionNotFound = "decision_not_found";
}

/// <summary>Domain sınırları (uzunluklar vb.). Ayar değil, veri modeli kısıtı.</summary>
public static class WorkflowLimits
{
    public const int NameMaxLength = 200;
    public const int EnumColumnMaxLength = 30;
    public const int ErrorMaxLength = 1000;
    public const int SubjectNameMaxLength = 300;
    public const int CommentMaxLength = 2000;
    public const int TitleMaxLength = 300;
    public const int EngineIdMaxLength = 100;
    public const int CurrencyMaxLength = 10;

    public const int MinFollowUpHours = 1;
    public const int MaxFollowUpHours = 720;
    public const int DefaultFollowUpHours = 24;
}
