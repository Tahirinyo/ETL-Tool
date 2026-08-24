using EtlTool.Domain.Enums;

namespace EtlTool.Application.Validations;

public sealed class ValidationHandlerRegistry
{
    private readonly Dictionary<ValidationType, IValidationHandler> _handlers;

    public ValidationHandlerRegistry(IEnumerable<IValidationHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        _handlers = [];

        foreach (var handler in handlers)
        {
            if (handler is null)
            {
                throw new ArgumentException(
                    "The validation handler collection contains an invalid handler.",
                    nameof(handlers));
            }

            if (!_handlers.TryAdd(handler.Type, handler))
            {
                throw new InvalidOperationException(
                    $"More than one validation handler is registered for validation type '{handler.Type}'.");
            }
        }
    }

    public IValidationHandler Resolve(ValidationType type)
    {
        if (_handlers.TryGetValue(type, out var handler))
        {
            return handler;
        }

        throw new KeyNotFoundException(
            $"No validation handler is registered for validation type '{type}'.");
    }
}
