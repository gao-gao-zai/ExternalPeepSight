using Avalonia.Controls;

using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;

namespace ExternalPeepSight.UI;

/// <summary>
/// Hosts the FluentAvalonia settings experience.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        IInputElement? focusedElement = FocusManager?.GetFocusedElement();
        if (focusedElement is not Visual focusedVisual)
        {
            return;
        }

        FANumberBox? focusedNumberBox = focusedVisual
            .GetSelfAndVisualAncestors()
            .OfType<FANumberBox>()
            .FirstOrDefault();
        if (focusedNumberBox is null ||
            args.Source is not Visual sourceVisual ||
            sourceVisual.GetSelfAndVisualAncestors().Contains(focusedNumberBox))
        {
            return;
        }

        FocusManager?.Focus(this, NavigationMethod.Pointer);
    }
}
