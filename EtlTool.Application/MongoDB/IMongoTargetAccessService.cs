namespace EtlTool.Application.MongoDB;

public interface IMongoTargetAccessService
{
    MongoTargetValidationResult Validate(MongoTarget target);

    Task EnsureAccessibleAsync(
        MongoTarget target,
        CancellationToken cancellationToken);
}
