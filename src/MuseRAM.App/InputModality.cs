using System.Windows;
using System.Windows.Input;

namespace MuseRAM.App;

public static class InputModality
{
    public static readonly DependencyProperty IsKeyboardModeProperty = DependencyProperty.RegisterAttached(
        "IsKeyboardMode",
        typeof(bool),
        typeof(InputModality),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    private static readonly DependencyProperty IsAttachedProperty = DependencyProperty.RegisterAttached(
        "IsAttached",
        typeof(bool),
        typeof(InputModality),
        new PropertyMetadata(false));

    public static bool GetIsKeyboardMode(DependencyObject element) =>
        (bool)element.GetValue(IsKeyboardModeProperty);

    public static void SetIsKeyboardMode(DependencyObject element, bool value) =>
        element.SetValue(IsKeyboardModeProperty, value);

    public static void Attach(Window window)
    {
        if ((bool)window.GetValue(IsAttachedProperty)) return;
        window.SetValue(IsAttachedProperty, true);
        window.PreviewKeyDown += (_, _) => SetIsKeyboardMode(window, true);
        window.PreviewMouseDown += (_, _) => SetIsKeyboardMode(window, false);
        window.PreviewStylusDown += (_, _) => SetIsKeyboardMode(window, false);
        window.PreviewTouchDown += (_, _) => SetIsKeyboardMode(window, false);
    }
}
