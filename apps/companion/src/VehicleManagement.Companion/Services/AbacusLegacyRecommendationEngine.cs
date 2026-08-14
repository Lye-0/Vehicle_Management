using VehicleManagement.AbacusImport;

namespace VehicleManagement.Companion.Services;

/// <summary>
/// 登録前候補グラフの値をGate28のUI非依存エンジンへ変換します。
/// </summary>
public sealed class AbacusLegacyRecommendationEngine
{
    private readonly AbacusRecommendationEngine engine = new();

    public IReadOnlyList<AbacusRecommendationCandidate> Build(
        AbacusLegacyExportCandidateGraphResult graph,
        Func<string, string>? customerNameResolver = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        customerNameResolver ??= customerId => graph.Customers
            .FirstOrDefault(customer => string.Equals(customer.CustomerId, customerId, StringComparison.Ordinal))
            ?.CustomerName ?? "";

        var customers = graph.Customers
            .Select(customer => new AbacusRecommendationCustomer(
                customer.CustomerId,
                new AbacusRecommendationProfile(
                    CustomerName: customerNameResolver(customer.CustomerId),
                    NameKana: customer.NameKana,
                    PhoneNumber: customer.PhoneNumber,
                    PostalCode: customer.PostalCode,
                    Address: customer.Address)))
            .ToArray();

        var vehicles = graph.Customers
            .SelectMany(customer => customer.Vehicles)
            .Concat(graph.UnresolvedVehicleRows)
            .GroupBy(vehicle => vehicle.VehicleId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(vehicle => new AbacusRecommendationVehicle(
                vehicle.VehicleId,
                vehicle.CustomerId,
                new AbacusRecommendationProfile(
                    CustomerName: string.IsNullOrWhiteSpace(vehicle.CustomerId)
                        ? vehicle.CustomerName
                        : customerNameResolver(vehicle.CustomerId),
                    Maker: vehicle.Maker,
                    VehicleName: vehicle.VehicleName,
                    Model: vehicle.Model,
                    RegistrationNumber: vehicle.RegistrationNumber,
                    ChassisNumber: vehicle.ChassisNumber)))
            .ToArray();

        var documents = graph.AllDocuments
            .Where(document => !document.IsLinked)
            .GroupBy(GetDocumentKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(document => new AbacusRecommendationDocument(
                GetDocumentKey(document),
                document.RecommendationProfile ?? new AbacusRecommendationProfile(
                    CustomerName: document.CustomerName,
                    VehicleName: document.VehicleName,
                    RegistrationNumber: document.RegistrationNumber)))
            .ToArray();

        return engine.Build(new AbacusRecommendationInput(documents, vehicles, customers));
    }

    private static string GetDocumentKey(AbacusLegacyExportCandidateGraphDocument document) =>
        string.Join("|", document.Kind, document.SourceFileName, document.SourceRowNumber, document.DocumentNumber);
}
