namespace ExpertPro.Receivables.Models;

public enum ClientType
{
    ManagementCompany,
    ResourceSupplyOrganization,
    HomeownersAssociation,
    Other,
}

public sealed record ClientOrganization(
    Guid Id,
    string Name,
    ClientType Type,
    decimal MonthlyRatePerAccount,
    string Inn = "",
    bool IsActive = true);

public sealed record Invoice(
    Guid Id,
    Guid ClientId,
    DateOnly BillingPeriod,
    int AccountCountAtClose,
    decimal RatePerAccount,
    decimal Amount,
    DateOnly DueDate);

public sealed record Payment(
    Guid Id,
    Guid? InvoiceId,
    Guid ClientId,
    DateOnly Date,
    decimal Amount,
    string Reference);

public sealed record InvoiceStatus(
    Invoice Invoice,
    ClientOrganization Client,
    decimal PaidAmount,
    decimal OutstandingAmount,
    bool IsOverdue);

public sealed record PaymentRegistrationResult(decimal AppliedToInvoice, decimal AdvanceAmount);

public sealed record PaymentAllocation(DateOnly BillingPeriod, decimal Amount);

public sealed record PaymentDistributionResult(
    IReadOnlyList<PaymentAllocation> Allocations,
    decimal AdvanceAmount);
