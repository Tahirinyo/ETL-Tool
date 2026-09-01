using EtlTool.Domain.Enums;

namespace EtlTool.Application.Loading;

public interface IDataLoaderResolver
{
    IDataLoader Resolve(DestinationType destinationType);
}
