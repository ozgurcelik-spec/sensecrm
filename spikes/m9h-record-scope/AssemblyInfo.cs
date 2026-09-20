using Xunit;

// Paylaşılan makine + süreç geneli statik bağlam (RecordScopeContext.FailClosedWhenUnset) → koleksiyonlar sıralı koşar.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
