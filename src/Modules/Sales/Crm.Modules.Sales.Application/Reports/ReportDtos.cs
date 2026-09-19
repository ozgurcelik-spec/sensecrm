using Crm.Modules.Sales.Domain.Leads;
using Crm.Modules.Sales.Domain.Pipelines;

namespace Crm.Modules.Sales.Application.Reports;

// HTTP sözleşmesi (docs/plan/m3-aktivite-rapor.md). Tutarlar fırsat para birimlerine bakmadan toplanır (M2 sınırlaması).

/// <summary>Huni raporundaki bir aşama: güncel durumda aşamadaki fırsat sayısı ve toplam tutarı.</summary>
public sealed record FunnelStageDto(Guid Id, string Name, StageKind Kind, int Order, int Probability, int Count, decimal TotalAmount);

public sealed record FunnelDto(Guid PipelineId, IReadOnlyList<FunnelStageDto> Stages);

/// <summary>Bir dönem ("2026-09" veya ISO hafta "2026-W38") için kazanılan/kaybedilen fırsatlar.</summary>
public sealed record WonLostPeriodDto(string Period, int WonCount, decimal WonAmount, int LostCount, decimal LostAmount);

public sealed record LeadSourceReportRow(LeadSource Source, int Count, int ConvertedCount);

public sealed record OwnerReportRow(
    Guid OwnerUserId,
    string? OwnerName,
    int OpenDealCount,
    decimal OpenDealAmount,
    int WonCount,
    decimal WonAmount,
    int LeadCount);

/// <summary>Kazanılan/kaybedilen raporu gruplaması.</summary>
public enum WonLostGroupBy
{
    Month,
    Week,
}

/// <summary>Kapanış tarihi (kiracı saat diliminde takvim günü) başına kazanılan/kaybedilen özet satırı (depo çıktısı).</summary>
public sealed record ClosedDealDayRow(DateOnly Day, StageKind Kind, int Count, decimal Amount);

/// <summary>Sahip başına ham toplamlar (depo çıktısı; ad çözümü handler'da).</summary>
public sealed record OwnerTotals(Guid OwnerUserId, int OpenDealCount, decimal OpenDealAmount, int WonCount, decimal WonAmount, int LeadCount);
