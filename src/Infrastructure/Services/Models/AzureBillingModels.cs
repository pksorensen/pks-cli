namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// Reference to an Azure billing profile under a billing account.
/// </summary>
public class BillingProfileRef
{
    public string BillingAccountId { get; set; } = string.Empty;
    public string BillingAccountDisplayName { get; set; } = string.Empty;
    public string BillingProfileId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
}

/// <summary>
/// One credit lot on a billing profile (e.g. Azure Sponsorship credit).
/// </summary>
public class CreditLot
{
    public decimal OriginalAmount { get; set; }
    public decimal ClosedBalance { get; set; }
    public string CreditCurrency { get; set; } = string.Empty;
    public DateTime? StartDate { get; set; }
    public DateTime? ExpirationDate { get; set; }
    public string Source { get; set; } = string.Empty;
    public bool IsEstimatedBalance { get; set; }
}

/// <summary>
/// Cost Management query grouping dimension.
/// </summary>
public enum CostGrouping
{
    None,
    Meter,
    ServiceName,
}

/// <summary>
/// A single grouped cost row.
/// </summary>
public record CostRow(string Group, decimal Cost);

/// <summary>
/// Aggregated result of a Cost Management query.
/// </summary>
public class CostQueryResult
{
    public string Currency { get; set; } = string.Empty;
    public decimal TotalCost { get; set; }
    public List<CostRow> Rows { get; set; } = new();
}

/// <summary>
/// One day of cost.
/// </summary>
public record DailyCostPoint(DateTime Date, decimal Cost);

/// <summary>
/// Daily-granularity Cost Management result.
/// </summary>
public class DailyCostResult
{
    public string Currency { get; set; } = string.Empty;
    public List<DailyCostPoint> Points { get; set; } = new();
}
