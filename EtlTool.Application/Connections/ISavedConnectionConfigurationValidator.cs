using EtlTool.Domain.Enums;

namespace EtlTool.Application.Connections;

public interface ISavedConnectionConfigurationValidator
{
    void Validate(DatabaseProviderType providerType, string connectionConfiguration);
}
