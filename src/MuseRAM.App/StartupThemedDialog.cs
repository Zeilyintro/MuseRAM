using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Cursors = System.Windows.Input.Cursors;

namespace MuseRAM.App;

internal static class StartupThemedDialog
{
    public static void Show(string title, string message, string buttonText, bool light, bool error = false)
    {
        var windowBrush = Brush(light ? "#F4F6F8" : "#0A0C0F");
        var surfaceBrush = Brush(light ? "#FFFFFF" : "#101318");
        var borderBrush = Brush(light ? "#CCD3DC" : "#2A303A");
        var textBrush = Brush(light ? "#161A20" : "#F4F6FA");
        var mutedBrush = Brush(light ? "#596473" : "#9BA4B1");
        var accentBrush = Brush(light ? "#4263C7" : "#8DA8FF");
        var accentHoverBrush = Brush(light ? "#3152B8" : "#A5B9FF");
        var actionTextBrush = Brush(light ? "#FFFFFF" : "#0A0C0F");
        var errorBrush = Brush(light ? "#B4232F" : "#FF6B6B");

        var dialog = new Window
        {
            Title = title,
            Width = 540,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = false,
            ShowInTaskbar = true,
            Background = windowBrush,
            Foreground = textBrush,
            Topmost = true
        };
        var close = CreateButton("\uE711", surfaceBrush, borderBrush, mutedBrush, surfaceBrush, textBrush);
        close.Width = 32;
        close.Height = 32;
        close.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        close.FontSize = 12;
        close.Click += (_, _) => dialog.Close();
        var titleBar = new Grid();
        titleBar.ColumnDefinitions.Add(new ColumnDefinition());
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(close, 1);
        titleBar.Children.Add(close);
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (!close.IsMouseOver && e.ButtonState == MouseButtonState.Pressed) dialog.DragMove();
        };

        var body = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(new TextBlock
        {
            Text = error ? "\uE783" : "\uE946",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 24,
            Foreground = error ? errorBrush : accentBrush,
            VerticalAlignment = VerticalAlignment.Top
        });
        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Foreground = textBrush
        };
        Grid.SetColumn(messageText, 2);
        body.Children.Add(messageText);

        var confirm = CreateButton(buttonText, accentBrush, accentBrush, actionTextBrush, accentHoverBrush, actionTextBrush);
        confirm.MinWidth = 104;
        confirm.Height = 36;
        confirm.HorizontalAlignment = HorizontalAlignment.Right;
        confirm.Margin = new Thickness(0, 22, 0, 0);
        confirm.IsDefault = true;
        confirm.Click += (_, _) => dialog.Close();

        var content = new Grid { Margin = new Thickness(22, 18, 22, 20) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(titleBar);
        Grid.SetRow(body, 1);
        content.Children.Add(body);
        Grid.SetRow(confirm, 2);
        content.Children.Add(confirm);
        dialog.Content = new Border
        {
            Background = windowBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = content
        };
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) dialog.Close();
        };
        dialog.SourceInitialized += (_, _) => WindowThemeService.EnableNativeWindowAnimations(dialog);
        _ = dialog.ShowDialog();
    }

    private static Button CreateButton(
        string content,
        Brush background,
        Brush border,
        Brush foreground,
        Brush hoverBackground,
        Brush hoverForeground)
    {
        var root = new FrameworkElementFactory(typeof(Border));
        root.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        root.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Button.Background))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        root.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Button.BorderBrush))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        root.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding(nameof(Button.BorderThickness))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        root.AppendChild(presenter);
        var button = new Button
        {
            Content = content,
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Foreground = foreground,
            Padding = new Thickness(12, 5, 12, 5),
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = root }
        };
        button.MouseEnter += (_, _) =>
        {
            button.Background = hoverBackground;
            button.Foreground = hoverForeground;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = background;
            button.Foreground = foreground;
        };
        return button;
    }

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
