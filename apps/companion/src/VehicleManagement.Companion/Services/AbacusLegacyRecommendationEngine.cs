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

        return BuildInternal(graph, customerNameResolver);
    }

    /// <summary>
    /// 顧客統合・統合解除後に影響する顧客を中心に候補を再評価します。
    /// 全候補の判定に必要な顧客・車両情報は参照しますが、返す候補は影響範囲へ限定します。
    /// 既存候補と結合することで、未影響の候補を画面から不用意に消しません。
    /// </summary>
    public IReadOnlyList<AbacusRecommendationCandidate> BuildForCustomers(
        AbacusLegacyExportCandidateGraphResult graph,
        IReadOnlySet<string> affectedCustomerIds,
        Func<string, string>? customerNameResolver = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(affectedCustomerIds);
        if (affectedCustomerIds.Count == 0)
        {
            return [];
        }

        var allCandidates = BuildInternal(graph, customerNameResolver);
        var affectedVehicleIds = graph.Customers
            .SelectMany(customer => customer.Vehicles)
            .Where(vehicle => affectedCustomerIds.Contains(vehicle.CustomerId))
            .Select(vehicle => vehicle.VehicleId)
            .ToHashSet(StringComparer.Ordinal);
        var affectedDocumentKeys = graph.Customers
            .Where(customer => affectedCustomerIds.Contains(customer.CustomerId))
            .SelectMany(customer => customer.Vehicles.SelectMany(vehicle => vehicle.Documents)
                .Concat(customer.UnresolvedDocuments))
            .Select(GetDocumentKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return allCandidates
            .Where(candidate =>
                candidate.TargetCustomerId is not null && affectedCustomerIds.Contains(candidate.TargetCustomerId) ||
                candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
                affectedCustomerIds.Contains(candidate.SubjectId) ||
                candidate.TargetKind == AbacusRecommendationEntityKinds.Customer &&
                affectedCustomerIds.Contains(candidate.TargetId) ||
                candidate.SubjectKind == AbacusRecommendationEntityKinds.Vehicle &&
                affectedVehicleIds.Contains(candidate.SubjectId) ||
                candidate.SubjectKind == AbacusRecommendationEntityKinds.Document &&
                affectedDocumentKeys.Contains(candidate.SubjectId))
            .ToArray();
    }

    private IReadOnlyList<AbacusRecommendationCandidate> BuildInternal(
        AbacusLegacyExportCandidateGraphResult graph,
        Func<string, string>? customerNameResolver)
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
