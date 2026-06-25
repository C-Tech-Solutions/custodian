namespace Custodian.Core.Model;

public sealed record CloudProviderMetadata(
    string ProviderId,
    string ProviderName,
    string AccountLabel,
    string RootPath);
