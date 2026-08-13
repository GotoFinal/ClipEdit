namespace ClipEdit.App.Platform;

internal interface IProjectFileAssociationService
{
    ProjectFileAssociationResult Register();
}

internal sealed record ProjectFileAssociationResult(bool IsSuccess, string Message);
