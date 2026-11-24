using ExpertPro.Receivables.Models;

namespace ExpertPro.Receivables.Application;

public sealed class ReceivablesService
{
    private readonly List<ClientOrganization> _clients;
    private readonly List<Invoice> _invoices;
    private readonly List<Payment> _payments;

    public ReceivablesService(
        IEnumerable<ClientOrganization>? clients = null,
        IEnumerable<Invoice>? invoices = null,
        IEnumerable<Payment>? payments = null)
    {
        _clients = clients?.ToList() ?? [];
        _invoices = invoices?.ToList() ?? [];
        _payments = payments?.ToList() ?? [];
    }

    public IReadOnlyList<ClientOrganization> Clients => _clients.AsReadOnly();
    public IReadOnlyList<Invoice> Invoices => _invoices.AsReadOnly();
    public IReadOnlyList<Payment> Payments => _payments.AsReadOnly();

    public ClientOrganization AddClient(string name, ClientType type, decimal monthlyRatePerAccount, string inn)
    {
        var normalizedName = RequireText(name, "Название организации обязательно.");
        var normalizedInn = NormalizeInn(inn);
        if (monthlyRatePerAccount <= 0)
            throw new ArgumentException("Тариф за лицевой счет должен быть больше нуля.", nameof(monthlyRatePerAccount));
        if (_clients.Any(x => x.Inn == normalizedInn))
            throw new ArgumentException("Организация с таким ИНН уже существует.");

        var client = new ClientOrganization(
            Guid.NewGuid(),
            normalizedName,
            type,
            decimal.Round(monthlyRatePerAccount, 2, MidpointRounding.ToEven),
            normalizedInn);
        _clients.Add(client);
        return client;
    }

    public ClientOrganization ChangeClientRate(Guid clientId, decimal monthlyRatePerAccount)
    {
        if (monthlyRatePerAccount <= 0)
            throw new ArgumentException("Тариф за лицевой счет должен быть больше нуля.", nameof(monthlyRatePerAccount));

        var index = _clients.FindIndex(x => x.Id == clientId);
        if (index < 0)
            throw new ArgumentException("Организация не найдена.");

        var old = _clients[index];
        var updated = old with { MonthlyRatePerAccount = decimal.Round(monthlyRatePerAccount, 2, MidpointRounding.ToEven) };
        _clients[index] = updated;
        return updated;
    }

    public Invoice CloseBillingPeriod(Guid clientId, DateOnly billingPeriod, int accountCountAtClose)
    {
        var client = GetClient(clientId);
        if (!client.IsActive)
            throw new ArgumentException("Для неактивной организации нельзя закрыть период.");
        if (accountCountAtClose < 0)
            throw new ArgumentException("Количество лицевых счетов не может быть отрицательным.", nameof(accountCountAtClose));

        var period = new DateOnly(billingPeriod.Year, billingPeriod.Month, 1);
        if (_invoices.Any(x => x.ClientId == clientId && x.BillingPeriod == period))
            throw new ArgumentException("Начисление для этой организации и периода уже существует.");

        var amount = decimal.Round(accountCountAtClose * client.MonthlyRatePerAccount, 2, MidpointRounding.ToEven);
        var dueDate = period.AddMonths(1).AddDays(9);
        var invoice = new Invoice(Guid.NewGuid(), clientId, period, accountCountAtClose, client.MonthlyRatePerAccount, amount, dueDate);
        _invoices.Add(invoice);
        return invoice;
    }

    public PaymentRegistrationResult RegisterPayment(Guid clientId, Guid? invoiceId, decimal amount, DateOnly paymentDate, string reference)
    {
        GetClient(clientId);
        if (amount <= 0)
            throw new ArgumentException("Сумма платежа должна быть больше нуля.", nameof(amount));

        var normalizedAmount = decimal.Round(amount, 2, MidpointRounding.ToEven);
        var normalizedReference = reference?.Trim() ?? string.Empty;
        if (invoiceId is not null)
        {
            var invoice = GetInvoice(invoiceId.Value);
            if (invoice.ClientId != clientId)
                throw new ArgumentException("Начисление не относится к выбранной организации.");
            var remaining = invoice.Amount - GetPaidAmount(invoice.Id);
            var applied = Math.Min(normalizedAmount, remaining);
            var advance = normalizedAmount - applied;
            if (applied > 0)
                _payments.Add(new Payment(Guid.NewGuid(), invoiceId, clientId, paymentDate, applied, normalizedReference));
            if (advance > 0)
                _payments.Add(new Payment(Guid.NewGuid(), null, clientId, paymentDate, advance, normalizedReference));
            return new PaymentRegistrationResult(applied, advance);
        }

        _payments.Add(new Payment(Guid.NewGuid(), null, clientId, paymentDate, normalizedAmount, normalizedReference));
        return new PaymentRegistrationResult(0, normalizedAmount);
    }

    public PaymentDistributionResult RegisterPaymentByOldestDebt(Guid clientId, decimal amount, DateOnly paymentDate, string reference)
    {
        GetClient(clientId);
        if (amount <= 0)
            throw new ArgumentException("Сумма платежа должна быть больше нуля.", nameof(amount));

        var remainingPayment = decimal.Round(amount, 2, MidpointRounding.ToEven);
        var normalizedReference = reference?.Trim() ?? string.Empty;
        var allocations = new List<PaymentAllocation>();
        foreach (var invoice in _invoices.Where(x => x.ClientId == clientId).OrderBy(x => x.DueDate).ThenBy(x => x.BillingPeriod))
        {
            if (remainingPayment <= 0) break;
            var outstanding = invoice.Amount - GetPaidAmount(invoice.Id);
            if (outstanding <= 0) continue;
            var applied = Math.Min(remainingPayment, outstanding);
            _payments.Add(new Payment(Guid.NewGuid(), invoice.Id, clientId, paymentDate, applied, normalizedReference));
            allocations.Add(new PaymentAllocation(invoice.BillingPeriod, applied));
            remainingPayment -= applied;
        }
        if (remainingPayment > 0)
            _payments.Add(new Payment(Guid.NewGuid(), null, clientId, paymentDate, remainingPayment, normalizedReference));
        return new PaymentDistributionResult(allocations, remainingPayment);
    }

    public IReadOnlyList<InvoiceStatus> GetInvoiceStatuses(DateOnly asOfDate) =>
        _invoices
            .Select(invoice =>
            {
                var paid = GetPaidAmount(invoice.Id);
                var client = GetClient(invoice.ClientId);
                var outstanding = invoice.Amount - paid;
                return new InvoiceStatus(invoice, client, paid, outstanding, outstanding > 0 && invoice.DueDate < asOfDate);
            })
            .OrderBy(x => x.Client.Name)
            .ThenBy(x => x.Invoice.BillingPeriod)
            .ToList();

    public decimal GetClientDebt(Guid clientId) =>
        _invoices.Where(x => x.ClientId == clientId).Sum(invoice => invoice.Amount - GetPaidAmount(invoice.Id)) - GetClientAdvance(clientId);

    public decimal GetClientAdvance(Guid clientId) =>
        _payments.Where(x => x.ClientId == clientId && x.InvoiceId is null).Sum(x => x.Amount);

    private ClientOrganization GetClient(Guid id) =>
        _clients.SingleOrDefault(x => x.Id == id) ?? throw new ArgumentException("Организация не найдена.");

    private Invoice GetInvoice(Guid id) =>
        _invoices.SingleOrDefault(x => x.Id == id) ?? throw new ArgumentException("Начисление не найдено.");

    private decimal GetPaidAmount(Guid invoiceId) =>
        _payments.Where(x => x.InvoiceId == invoiceId).Sum(x => x.Amount);

    private static string RequireText(string? value, string message) =>
        string.IsNullOrWhiteSpace(value?.Trim()) ? throw new ArgumentException(message) : value.Trim();

    private static string NormalizeInn(string? inn)
    {
        var value = inn?.Trim() ?? string.Empty;
        if ((value.Length != 10 && value.Length != 12) || !value.All(char.IsAsciiDigit))
            throw new ArgumentException("ИНН должен состоять из 10 или 12 цифр.");
        return value;
    }
}
