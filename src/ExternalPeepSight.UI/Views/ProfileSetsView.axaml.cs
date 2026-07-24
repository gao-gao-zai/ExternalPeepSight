using Avalonia.Controls;
using Avalonia.Input;
using ExternalPeepSight.Core;
using ExternalPeepSight.UI.ViewModels;

namespace ExternalPeepSight.UI.Views;

internal sealed partial class ProfileSetsView : UserControl
{
    public ProfileSetsView()
    {
        InitializeComponent();
    }

    private async void OnProfilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: Profile profile })
        {
            return;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(profile.Id.ToString("D")));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
    }

    private void OnProfileSetDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = Guid.TryParse(e.DataTransfer.TryGetText(), out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnProfileSetDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control { DataContext: ProfileSet profileSet } ||
            DataContext is not ProfileSetsViewModel viewModel ||
            !Guid.TryParse(e.DataTransfer.TryGetText(), out Guid profileId))
        {
            return;
        }

        viewModel.AssignProfile(profileId, profileSet.Id);
        e.Handled = true;
    }
}
