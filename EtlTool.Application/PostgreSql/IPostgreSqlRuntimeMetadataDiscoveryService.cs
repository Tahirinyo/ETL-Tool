namespace EtlTool.Application.PostgreSql;

public interface IPostgreSqlRuntimeMetadataDiscoveryService :
    IPostgreSqlMetadataDiscoveryService,
    IPostgreSqlDestinationAccessService
{
}
