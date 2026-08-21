using EtlTool.Domain.Enums;

namespace EtlTool.Application.Transformations;

public sealed class TransformationHandlerRegistry
{
    private readonly Dictionary<TransformationType, ITransformationHandler> _handlers;

    public TransformationHandlerRegistry(IEnumerable<ITransformationHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        _handlers = [];

        foreach (var handler in handlers)
        {
            if (handler is null)
            {
                throw new ArgumentException(
                    "The transformation handler collection contains an invalid handler.",
                    nameof(handlers));
            }

            if (!_handlers.TryAdd(handler.Type, handler))
            {
                throw new InvalidOperationException(
                    $"More than one transformation handler is registered for transformation type '{handler.Type}'.");
            }
        }
    }

    public ITransformationHandler Resolve(TransformationType type)
    {
        if (_handlers.TryGetValue(type, out var handler))
        {
            return handler;
        }

        throw new KeyNotFoundException(
            $"No transformation handler is registered for transformation type '{type}'.");
    }
}
