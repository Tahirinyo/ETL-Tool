using EtlTool.Domain.Enums;

namespace EtlTool.Application.Loading;

public sealed class DataLoaderResolver : IDataLoaderResolver
{
    private readonly IReadOnlyDictionary<DestinationType, IDataLoader> _loaders;

    public DataLoaderResolver(IEnumerable<IDataLoader> loaders)
    {
        ArgumentNullException.ThrowIfNull(loaders);

        var registered = new Dictionary<DestinationType, IDataLoader>();
        foreach (var loader in loaders)
        {
            if (loader is null)
            {
                throw new ArgumentException(
                    "The data-loader collection contains an invalid loader.",
                    nameof(loaders));
            }

            if (loader.DestinationType == DestinationType.Unspecified
                || !Enum.IsDefined(loader.DestinationType))
            {
                throw new InvalidOperationException(
                    $"The data-loader destination type '{loader.DestinationType}' is not supported.");
            }

            if (!registered.TryAdd(loader.DestinationType, loader))
            {
                throw new InvalidOperationException(
                    $"More than one data loader is registered for destination type '{loader.DestinationType}'.");
            }
        }

        _loaders = registered;
    }

    public IDataLoader Resolve(DestinationType destinationType)
    {
        if (destinationType == DestinationType.Unspecified
            || !Enum.IsDefined(destinationType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(destinationType),
                destinationType,
                "The destination type is not supported.");
        }

        if (_loaders.TryGetValue(destinationType, out var loader))
        {
            return loader;
        }

        throw new KeyNotFoundException(
            $"No data loader is registered for destination type '{destinationType}'.");
    }
}
