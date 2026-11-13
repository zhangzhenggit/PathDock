using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PathDock;

internal static class Ui
{
    public static readonly Brush Ink = SystemColors.ControlTextBrush, Muted = Brush("#62666C"), Accent = Brush("#0067C0"), Line = Brush("#E2E5EA"), Canvas = Brush("#FAFAFA");
    static readonly Brush panelTint = Gradient("#FFFFFF", "#F1F7FF", 90);
    static readonly Brush headerTint = Gradient("#DFEDFF", "#DDF5F1", 0);
    static readonly Brush accentRibbon = Gradient("#4385EF", "#27B6AE", 0);
    static readonly Brush edgeTint = Gradient("#C5DDFF", "#BAEEE6", 60);
    static readonly Brush hoverTint = Gradient("#DEEDFF", "#E0F5EF", 0);
    static readonly Brush pressTint = Gradient("#C9E0FF", "#CDEDE4", 0);
    static readonly Brush motionGlow = CreateMotionGlow();
    static readonly Brush lightRight = CreateEdgeLight(0), lightLeft = CreateEdgeLight(180),
        lightBottom = CreateEdgeLight(90), lightTop = CreateEdgeLight(270);
    public static Brush PanelBackground => SystemParameters.HighContrast ? SystemColors.WindowBrush : panelTint;
    public static Brush HeaderBackground => SystemParameters.HighContrast ? SystemColors.ControlBrush : headerTint;
    public static Brush AccentRibbon => SystemParameters.HighContrast ? SystemColors.HighlightBrush : accentRibbon;
    public static Brush EdgeBackground => SystemParameters.HighContrast ? SystemColors.ControlBrush : edgeTint;
    public static Brush HoverBackground => SystemParameters.HighContrast ? SystemColors.HighlightBrush : hoverTint;
    public static Brush PressBackground => SystemParameters.HighContrast ? SystemColors.HighlightBrush : pressTint;
    public static Brush MotionGlow => motionGlow;
    public static Brush DockLight(string side) => side switch { "left" => lightLeft, "top" => lightTop, "bottom" => lightBottom, _ => lightRight };
    static Brush CreateEdgeLight(double angle)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5), RelativeTransform = new RotateTransform(angle, .5, .5) };
        brush.GradientStops.Add(new(Color.FromArgb(100, 37, 88, 150), 0));
        brush.GradientStops.Add(new(Color.FromArgb(210, 255, 255, 255), .18));
        brush.GradientStops.Add(new(Color.FromArgb(110, 101, 206, 225), .42));
        brush.GradientStops.Add(new(Colors.Transparent, 1));
        brush.Freeze(); return brush;
    }
    static Brush Gradient(string start, string end, double angle)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(start), (Color)ColorConverter.ConvertFromString(end), angle);
        brush.Freeze(); return brush;
    }
    static Brush CreateMotionGlow()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, .4) };
        brush.GradientStops.Add(new(Color.FromArgb(0, 255, 255, 255), 0));
        brush.GradientStops.Add(new(Color.FromArgb(210, 255, 255, 255), .48));
        brush.GradientStops.Add(new(Color.FromArgb(70, 103, 220, 220), 1));
        brush.Freeze(); return brush;
    }
    public static double ExplorerFontSize => SystemFonts.MessageFontSize;
    public static SolidColorBrush Brush(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    public static void CrispText(FrameworkElement element)
    {
        element.UseLayoutRounding = true; element.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(element, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(element, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(element, TextHintingMode.Fixed);
        RenderOptions.SetClearTypeHint(element, ClearTypeHint.Enabled);
    }
    public static TextBlock Text(string text, double size = 13, Brush? color = null, bool bold = false) => new() { Text = text, FontSize = size, Foreground = color ?? Ink, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    public static TextBlock Icon(string glyph, double size = 17, Brush? color = null) => new() { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = size, Foreground = color ?? Muted, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    public static Button Button(object content, bool primary = false)
    {
        var button = new Button { Content = content, Background = primary ? Accent : Brushes.Transparent, Foreground = primary ? Brushes.White : Ink, BorderThickness = new Thickness(0), Padding = new Thickness(8, 5, 8, 5), Cursor = System.Windows.Input.Cursors.Arrow, Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Center };
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border)); border.Name = "surface"; border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3)); border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty)); presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty)); presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(presenter); template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Border.BackgroundProperty, primary ? Brush("#005A9E") : HoverBackground, "surface")); template.Triggers.Add(hover);
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true }; pressed.Setters.Add(new Setter(Border.BackgroundProperty, primary ? Brush("#004A86") : PressBackground, "surface")); template.Triggers.Add(pressed);
        if (SystemParameters.HighContrast && !primary)
        {
            hover.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.HighlightTextBrush));
            pressed.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.HighlightTextBrush));
        }
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .4)); template.Triggers.Add(disabled);
        button.Template = template; return button;
    }
    public static Button IconButton(string glyph, string tip) { var b = Button(Icon(glyph, 14, Ink)); b.Width = 28; b.Height = 28; b.Padding = new Thickness(5); b.ToolTip = tip; System.Windows.Automation.AutomationProperties.SetName(b, tip); return b; }
    public static TextBox Input(string text = "") => new() { Text = text, FontSize = 13, Padding = new Thickness(10), Background = Brushes.White, Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center };
    public static void Message(Window owner, string text) => MessageBox.Show(owner, text, "PathDock", MessageBoxButton.OK, MessageBoxImage.Information);
}

internal sealed class DockDialog : Window
{
    public StackPanel Body { get; } = new();
    public DockDialog(Window owner, string title, string subtitle, double width = 460, double height = 400)
    {
        Owner = owner; Title = title + " - PathDock"; Width = width; Height = height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Background = SystemColors.ControlBrush; FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = Ui.ExplorerFontSize;
        Ui.CrispText(this);
        Icon = AppIcon.Image;
        var grid = new Grid { Margin = new Thickness(16), Background = SystemColors.ControlBrush }; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition());
        var head = new StackPanel(); if (!string.IsNullOrWhiteSpace(subtitle)) { var description = Ui.Text(subtitle, 12, Ui.Muted); description.TextWrapping = TextWrapping.Wrap; description.Margin = new Thickness(0, 0, 0, 12); head.Children.Add(description); }
        grid.Children.Add(head);
        var scroll = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 1); grid.Children.Add(scroll); Content = grid;
    }
}
