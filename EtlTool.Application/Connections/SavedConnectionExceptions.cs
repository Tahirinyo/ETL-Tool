namespace EtlTool.Application.Connections;

public sealed class SavedConnectionConfigurationException : Exception
{
    public SavedConnectionConfigurationException(string message)
        : base(message)
    {
    }
}

public sealed class SavedConnectionResolutionException : Exception
{
    public SavedConnectionResolutionException()
        : base("The saved database connection is unavailable.")
    {
    }

    public SavedConnectionResolutionException(Exception innerException)
        : base("The saved database connection is unavailable.", innerException)
    {
    }
}
