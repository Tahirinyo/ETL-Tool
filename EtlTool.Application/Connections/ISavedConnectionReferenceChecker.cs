namespace EtlTool.Application.Connections;

public interface ISavedConnectionReferenceChecker
{
    Task<bool> IsReferencedAsync(Guid connectionId, CancellationToken cancellationToken);
}
