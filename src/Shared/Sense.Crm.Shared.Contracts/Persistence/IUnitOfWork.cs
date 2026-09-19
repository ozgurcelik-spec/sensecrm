namespace Sense.Crm.Shared.Contracts.Persistence;

/// <summary>Modül DbContext'inin command pipeline'ına açtığı yüz.</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default);
}

/// <summary>Her modül kendi UnitOfWork'ünü bu türetilmiş arayüzle kaydeder; pipeline modülün handler assembly'sine göre seçer.</summary>
public interface IModuleUnitOfWork : IUnitOfWork
{
    string ModuleName { get; }
}
