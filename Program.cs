using System.Globalization;
using System.Text;
using ExpertPro.Receivables.Application;
using ExpertPro.Receivables.Infrastructure;
using ExpertPro.Receivables.Models;

Console.OutputEncoding = Encoding.UTF8;
var culture = CultureInfo.GetCultureInfo("ru-RU");
var store = new JsonDataStore(Path.Combine("data", "receivables.json"));
var service = await store.LoadAsync();

Console.WriteLine("Эксперт-Про — учет платежей и задолженности клиентов");
Console.WriteLine("Учебный прототип: УК, РСО и ТСЖ; данные хранятся локально и обезличенно.");

while (true)
{
    PrintMenu();
    Console.Write("Выберите действие: ");
    var choice = Console.ReadLine()?.Trim();
    try
    {
        switch (choice)
        {
            case "1": AddClient(service, culture); await store.SaveAsync(service); break;
            case "2": ChangeRate(service, culture); await store.SaveAsync(service); break;
            case "3": ClosePeriod(service, culture); await store.SaveAsync(service); break;
            case "4": RegisterPayment(service, culture); await store.SaveAsync(service); break;
            case "5": PrintDebtReport(service, culture); break;
            case "6": PrintInvoices(service, culture); break;
            case "7": SeedDemoData(service); await store.SaveAsync(service); Console.WriteLine("Демонстрационные данные добавлены."); break;
            case "0": await store.SaveAsync(service); Console.WriteLine("Данные сохранены. Работа завершена."); return;
            default: Console.WriteLine("Неизвестная команда."); break;
        }
    }
    catch (ArgumentException exception) { Console.WriteLine($"Ошибка: {exception.Message}"); }
    catch (IOException exception) { Console.WriteLine($"Не удалось сохранить данные: {exception.Message}"); }
    Console.WriteLine();
}

static void PrintMenu() => Console.WriteLine("""

1 — добавить организацию-клиента
2 — изменить тариф организации
3 — закрыть расчетный период и начислить плату
4 — зарегистрировать оплату
5 — отчет по задолженности и просрочкам
6 — список начислений
7 — добавить демонстрационные данные
0 — сохранить и выйти
""");

static void AddClient(ReceivablesService service, CultureInfo culture)
{
    Console.Write("Название организации: ");
    var name = Console.ReadLine() ?? string.Empty;
    Console.Write("ИНН организации: ");
    var inn = Console.ReadLine() ?? string.Empty;
    Console.Write("Тип (1 — УК, 2 — РСО, 3 — ТСЖ, 4 — прочее): ");
    var type = ReadClientType(Console.ReadLine());
    Console.Write("Тариф за один лицевой счет в месяц: ");
    var rate = ReadMoney(Console.ReadLine(), culture);
    var client = service.AddClient(name, type, rate, inn);
    Console.WriteLine($"Добавлена организация: {client.Name}, ИНН {client.Inn}.");
}

static void ChangeRate(ReceivablesService service, CultureInfo culture)
{
    var client = ChooseClient(service, culture);
    Console.WriteLine($"Текущий тариф: {client.MonthlyRatePerAccount.ToString("C", culture)} за лицевой счет.");
    Console.Write("Новый тариф: ");
    var updated = service.ChangeClientRate(client.Id, ReadMoney(Console.ReadLine(), culture));
    Console.WriteLine($"Тариф изменен на {updated.MonthlyRatePerAccount.ToString("C", culture)}. Уже закрытые периоды не изменяются.");
}

static void ClosePeriod(ReceivablesService service, CultureInfo culture)
{
    var client = ChooseClient(service, culture);
    Console.Write("Расчетный период (ГГГГ-ММ): ");
    var period = ReadPeriod(Console.ReadLine());
    Console.Write("Количество лицевых счетов на момент закрытия периода: ");
    if (!int.TryParse(Console.ReadLine(), out var accounts) || accounts < 0)
        throw new ArgumentException("Введите целое неотрицательное количество лицевых счетов.");

    var invoice = service.CloseBillingPeriod(client.Id, period, accounts);
    Console.WriteLine($"Начислено {invoice.Amount.ToString("C", culture)}. Срок оплаты: {invoice.DueDate:dd.MM.yyyy}.");
}

static void RegisterPayment(ReceivablesService service, CultureInfo culture)
{
    var client = ChooseClient(service, culture);
    var invoices = service.GetInvoiceStatuses(DateOnly.FromDateTime(DateTime.Today))
        .Where(x => x.Client.Id == client.Id && x.OutstandingAmount > 0)
        .OrderBy(x => x.Invoice.DueDate)
        .ToList();
    if (invoices.Count == 0)
    {
        Console.WriteLine("Неоплаченных начислений нет. Вся сумма будет учтена как аванс (переплата) организации.");
    }
    else
    {
        Console.WriteLine($"Неоплаченные начисления организации {client.Name}, ИНН {client.Inn}:");
        for (var i = 0; i < invoices.Count; i++)
            Console.WriteLine($"{i + 1}. {invoices[i].Invoice.BillingPeriod:MM.yyyy}: остаток {invoices[i].OutstandingAmount.ToString("C", culture)}");
        Console.WriteLine("Платеж будет распределен по периодам автоматически: от самого раннего к самому позднему.");
    }

    Console.Write("Сумма платежа: ");
    var amount = ReadMoney(Console.ReadLine(), culture);
    Console.Write("Номер платежного документа (необязательно): ");
    var reference = Console.ReadLine() ?? string.Empty;
    var result = service.RegisterPaymentByOldestDebt(client.Id, amount, DateOnly.FromDateTime(DateTime.Today), reference);
    foreach (var allocation in result.Allocations)
        Console.WriteLine($"В счет периода {allocation.BillingPeriod:MM.yyyy} учтено {allocation.Amount.ToString("C", culture)}.");
    if (result.AdvanceAmount > 0)
        Console.WriteLine($"Авансом учтено {result.AdvanceAmount.ToString("C", culture)}.");
    if (result.Allocations.Count == 0 && result.AdvanceAmount > 0)
        Console.WriteLine("Неоплаченных начислений не было: вся сумма записана как аванс.");
}

static void PrintDebtReport(ReceivablesService service, CultureInfo culture)
{
    var asOf = DateOnly.FromDateTime(DateTime.Today);
    var rows = service.GetInvoiceStatuses(asOf).Where(x => x.OutstandingAmount > 0).ToList();
    var clientGroups = rows.GroupBy(x => x.Client.Id).ToDictionary(group => group.Key, group => group.ToList());
    var clientsWithBalance = service.Clients
        .Where(client => clientGroups.ContainsKey(client.Id) || service.GetClientAdvance(client.Id) > 0)
        .OrderBy(client => client.Name)
        .ToList();
    if (clientsWithBalance.Count == 0) { Console.WriteLine("Задолженности и авансовых платежей нет."); return; }

    Console.WriteLine($"Задолженность на {asOf:dd.MM.yyyy}:");
    foreach (var client in clientsWithBalance)
    {
        var clientRows = clientGroups.GetValueOrDefault(client.Id, []);
        Console.WriteLine($"\n{client.Name} ({ClientTypeName(client.Type)}), ИНН {client.Inn}");
        foreach (var row in clientRows.OrderBy(x => x.Invoice.BillingPeriod))
        {
            var status = row.IsOverdue ? "ПРОСРОЧЕНО" : "к оплате";
            Console.WriteLine($"  {row.Invoice.BillingPeriod:MM.yyyy} | лицевых счетов: {row.Invoice.AccountCountAtClose} | " +
                $"долг: {row.OutstandingAmount.ToString("C", culture)} | {status}");
        }
        var advance = service.GetClientAdvance(client.Id);
        if (advance > 0)
            Console.WriteLine($"  Аванс (переплата): -{advance.ToString("C", culture)}");
        var balance = service.GetClientDebt(client.Id);
        var balanceLabel = balance < 0 ? "Переплата организации" : "Итого задолженность организации";
        Console.WriteLine($"  {balanceLabel}: {Math.Abs(balance).ToString("C", culture)}");
    }
    var total = service.Clients.Sum(client => service.GetClientDebt(client.Id));
    Console.WriteLine($"Итоговое сальдо по всем организациям: {total.ToString("C", culture)}");
}

static void PrintInvoices(ReceivablesService service, CultureInfo culture)
{
    var rows = service.GetInvoiceStatuses(DateOnly.FromDateTime(DateTime.Today));
    if (rows.Count == 0) { Console.WriteLine("Начислений пока нет."); return; }
    foreach (var row in rows)
        Console.WriteLine($"{row.Client.Name}, ИНН {row.Client.Inn} | {row.Invoice.BillingPeriod:MM.yyyy} | начислено {row.Invoice.Amount.ToString("C", culture)} | оплачено {row.PaidAmount.ToString("C", culture)} | остаток {row.OutstandingAmount.ToString("C", culture)}");
}

static ClientOrganization ChooseClient(ReceivablesService service, CultureInfo culture)
{
    if (service.Clients.Count == 0) throw new ArgumentException("Сначала добавьте организацию-клиента.");
    for (var i = 0; i < service.Clients.Count; i++)
        Console.WriteLine($"{i + 1}. {service.Clients[i].Name}, ИНН {service.Clients[i].Inn} — {service.Clients[i].MonthlyRatePerAccount.ToString("C", culture)} за лицевой счет");
    Console.Write("Номер организации или ИНН: ");
    var selection = Console.ReadLine()?.Trim() ?? string.Empty;
    if (int.TryParse(selection, out var number) && number >= 1 && number <= service.Clients.Count)
        return service.Clients[number - 1];

    var byInn = service.Clients.SingleOrDefault(x => x.Inn == selection);
    return byInn ?? throw new ArgumentException("Организация с таким номером или ИНН не найдена.");
}

static ClientType ReadClientType(string? raw) => raw?.Trim() switch
{
    "1" => ClientType.ManagementCompany,
    "2" => ClientType.ResourceSupplyOrganization,
    "3" => ClientType.HomeownersAssociation,
    "4" => ClientType.Other,
    _ => throw new ArgumentException("Укажите тип от 1 до 4."),
};

static string ClientTypeName(ClientType type) => type switch
{
    ClientType.ManagementCompany => "УК",
    ClientType.ResourceSupplyOrganization => "РСО",
    ClientType.HomeownersAssociation => "ТСЖ",
    _ => "прочее",
};

static DateOnly ReadPeriod(string? raw)
{
    if (!DateOnly.TryParseExact(raw, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        throw new ArgumentException("Введите период в формате ГГГГ-ММ, например 2026-11.");
    return new DateOnly(value.Year, value.Month, 1);
}

static decimal ReadMoney(string? raw, CultureInfo culture)
{
    if (!decimal.TryParse(raw, NumberStyles.Number, culture, out var value) || value <= 0)
        throw new ArgumentException("Введите сумму больше нуля, например 1500,50.");
    return decimal.Round(value, 2, MidpointRounding.ToEven);
}

static void SeedDemoData(ReceivablesService service)
{
    if (service.Clients.Count > 0)
        throw new ArgumentException("Демонстрационные данные можно добавить только в пустую базу. Используйте отдельный JSON-файл для нового сценария.");

    var uk = service.AddClient("УК «Северный дом»", ClientType.ManagementCompany, 12.50m, "1000000001");
    var rso = service.AddClient("ООО «ТеплоРесурс»", ClientType.ResourceSupplyOrganization, 9.80m, "1000000002");
    var tszh = service.AddClient("ТСЖ «Солнечный квартал»", ClientType.HomeownersAssociation, 15m, "1000000003");
    var advanceClient = service.AddClient("УК «Березовая роща»", ClientType.ManagementCompany, 14m, "1000000004");
    var october = new DateOnly(2026, 10, 1);
    var november = new DateOnly(2026, 11, 1);
    var ukOctober = service.CloseBillingPeriod(uk.Id, october, 1240);
    service.RegisterPayment(uk.Id, ukOctober.Id, 10000m, new DateOnly(2026, 11, 7), "ПП-1017");
    service.CloseBillingPeriod(uk.Id, november, 1250);
    service.CloseBillingPeriod(rso.Id, october, 850);
    service.CloseBillingPeriod(tszh.Id, november, 310);
    var advanceInvoice = service.CloseBillingPeriod(advanceClient.Id, november, 220);
    service.RegisterPayment(advanceClient.Id, advanceInvoice.Id, 3500m, new DateOnly(2026, 11, 20), "ПП-1120");
}
