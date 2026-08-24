using EtlTool.Application.Extraction;
using EtlTool.Domain.Enums;

namespace EtlTool.Infrastructure.Extraction;

public sealed class FileExtractorResolver : IFileExtractorResolver
{
    private readonly IReadOnlyDictionary<SourceType, IFileExtractor> _extractors;

    public FileExtractorResolver(IEnumerable<IFileExtractor> extractors)
    {
        ArgumentNullException.ThrowIfNull(extractors);

        var registered = new Dictionary<SourceType, IFileExtractor>();

        foreach (var extractor in extractors)
        {
            if (extractor is null)
            {
                throw new ArgumentException(
                    "The file extractor collection contains an invalid extractor.",
                    nameof(extractors));
            }

            if (extractor.SourceType is not SourceType.Csv and not SourceType.Xlsx)
            {
                throw new InvalidOperationException(
                    $"The file extractor source type '{extractor.SourceType}' is not supported.");
            }

            if (!registered.TryAdd(extractor.SourceType, extractor))
            {
                throw new InvalidOperationException(
                    $"More than one file extractor is registered for source type '{extractor.SourceType}'.");
            }
        }

        _extractors = registered;
    }

    public IFileExtractor Resolve(SourceType sourceType)
    {
        if (sourceType is not SourceType.Csv and not SourceType.Xlsx)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceType),
                sourceType,
                "The source type is not supported.");
        }

        if (_extractors.TryGetValue(sourceType, out var extractor))
        {
            return extractor;
        }

        throw new KeyNotFoundException(
            $"No file extractor is registered for source type '{sourceType}'.");
    }
}
