namespace ClipEdit.Application.Projects;

public interface IProjectStore
{
    Task<ProjectDocument> LoadAsync(
        string projectPath,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        string projectPath,
        ProjectDocument document,
        CancellationToken cancellationToken = default);
}

public enum ProjectStoreFailure
{
    NotFound,
    AccessDenied,
    InvalidDocument,
    UnsupportedVersion,
    TooLarge,
    IoFailure,
}

public sealed class ProjectStoreException : Exception
{
    public ProjectStoreException(
        ProjectStoreFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public ProjectStoreFailure Failure { get; }
}
