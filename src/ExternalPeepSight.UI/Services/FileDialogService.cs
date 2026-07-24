using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ExternalPeepSight.UI.Services;

internal interface IFileDialogService
{
    public Task<string?> OpenImageAsync();

    public Task<string?> OpenPackageAsync();

    public Task<string?> SavePackageAsync();
}

internal sealed class FileDialogService(Window owner, LocalizationService localization) : IFileDialogService
{
    public async Task<string?> OpenImageAsync()
    {
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = localization["Dialog.SelectImage"],
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("PNG / SVG")
                    {
                        Patterns = ["*.png", "*.svg"],
                        MimeTypes = ["image/png", "image/svg+xml"],
                    },
                ],
            });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> OpenPackageAsync()
    {
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = localization["Dialog.OpenPackage"],
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("ExternalPeepSight package")
                    {
                        Patterns = ["*.epsx"],
                    },
                ],
            });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> SavePackageAsync()
    {
        IStorageFile? file = await owner.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = localization["Dialog.SavePackage"],
                DefaultExtension = "epsx",
                SuggestedFileName = "ExternalPeepSight.epsx",
                FileTypeChoices =
                [
                    new FilePickerFileType("ExternalPeepSight package")
                    {
                        Patterns = ["*.epsx"],
                    },
                ],
            });
        return file?.TryGetLocalPath();
    }
}
