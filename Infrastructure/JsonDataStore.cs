using System.Text.Json;
using ExpertPro.Receivables.Application;
using ExpertPro.Receivables.Models;

namespace ExpertPro.Receivables.Infrastructure;

public sealed class JsonDataStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public JsonDataStore(string path) => _path = path;

    public async Task<ReceivablesService> LoadAsync()
    {
        if (!File.Exists(_path))
            return new ReceivablesService();

        await using var stream = File.OpenRead(_path);
        var data = await JsonSerializer.DeserializeAsync<ReceivablesData>(stream, Options);
        return new ReceivablesService(data?.Clients, data?.Invoices, data?.Payments);
    }

    public async Task SaveAsync(ReceivablesService service)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = _path + ".tmp";
        var data = new ReceivablesData(service.Clients.ToList(), service.Invoices.ToList(), service.Payments.ToList());
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, data, Options);
        File.Move(temporaryPath, _path, overwrite: true);
    }
}

internal sealed record ReceivablesData(
    List<ClientOrganization> Clients,
    List<Invoice> Invoices,
    List<Payment> Payments);
