using System;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace MouseIA.UI;

/// <summary>MouseIA's look: dark liquid glass with an "aurora" violet → cyan → pink accent.</summary>
internal static class Theme
{
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static readonly Color AccentA = C("#8B5CF6");
    public static readonly Color AccentB = C("#22D3EE");
    public static readonly Color AccentC = C("#F472B6");

    /// <summary>The pointer's light: a soft sky blue, like light through glass.</summary>
    public static readonly Color PointerGlow = C("#5AA9FF");
    public static readonly Brush PointerStroke = Vertical(C("#F2FFFFFF"), C("#CCBFE0FF"));
    public static readonly Brush SolidSurface = Vertical(C("#FA171B28"), C("#FA0E1119"));

    public static readonly Brush Text = B("#F5F7FC");
    public static readonly Brush Muted = B("#AAB3C5");
    public static readonly Brush Faint = B("#727B90");
    public static readonly Brush Danger = B("#FF8E8E");
    public static readonly Brush Success = B("#6EE7B7");
    public static readonly Brush Hover = B("#1CFFFFFF");
    public static readonly Brush Pressed = B("#30FFFFFF");
    public static readonly Brush Field = B("#12FFFFFF");
    public static readonly Brush FieldStroke = B("#24FFFFFF");
    public static readonly Brush Divider = B("#16FFFFFF");
    public static readonly Brush Selection = B("#668B5CF6");
    public static readonly Brush Transparent = Brushes.Transparent;

    /// <summary>Accent gradient used for primary actions and the selection frame.</summary>
    public static readonly Brush Accent = Gradient(45, AccentA, AccentB);

    public static readonly Brush AccentSoft = Gradient(45, Color.FromArgb(0x55, AccentA.R, AccentA.G, AccentA.B),
        Color.FromArgb(0x55, AccentB.R, AccentB.G, AccentB.B));

    /// <summary>Tint laid over the blurred backdrop.</summary>
    public static readonly Brush GlassTint = Vertical(C("#B3141826"), C("#CC0B0E17"));

    /// <summary>Edge light: brighter at the top, like light catching glass.</summary>
    public static readonly Brush GlassStroke = Vertical(C("#5CFFFFFF"), C("#12FFFFFF"));

    public static readonly Brush GlassSheen = new LinearGradientBrush(
        new GradientStopCollection
        {
            new GradientStop(C("#1AFFFFFF"), 0),
            new GradientStop(C("#05FFFFFF"), 0.35),
            new GradientStop(C("#00FFFFFF"), 0.6),
        }, new Point(0, 0), new Point(0, 1)).Frozen();

    /// <summary>Solid fallback when no blur is available (Windows 10 settings window).</summary>
    public static readonly Brush WindowFallback = Vertical(C("#F2181C2A"), C("#F20D1019"));

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static Brush B(string hex)
    {
        var brush = new SolidColorBrush(C(hex));
        brush.Freeze();
        return brush;
    }

    public static Brush Vertical(Color top, Color bottom) =>
        new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0, 1)).Frozen();

    public static Brush Gradient(double angle, params Color[] colors)
    {
        var stops = new GradientStopCollection();
        for (int i = 0; i < colors.Length; i++)
            stops.Add(new GradientStop(colors[i], colors.Length == 1 ? 0 : (double)i / (colors.Length - 1)));
        return new LinearGradientBrush(stops, angle).Frozen();
    }

    /// <summary>A gradient that slowly rotates (for the selection frame and listening mic).</summary>
    public static LinearGradientBrush AnimatedAurora(double seconds = 6)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(AccentA, 0),
                new GradientStop(AccentB, 0.5),
                new GradientStop(AccentC, 1),
            },
        };
        var rotate = new RotateTransform(0, 0.5, 0.5);
        brush.RelativeTransform = rotate;
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever });
        return brush;
    }

    public static DropShadowEffect Glow(Color color, double blur, double opacity) =>
        new() { Color = color, BlurRadius = blur, ShadowDepth = 0, Opacity = opacity, RenderingBias = RenderingBias.Performance };

    public static DropShadowEffect Shadow() =>
        new() { Color = Colors.Black, BlurRadius = 36, ShadowDepth = 10, Direction = 270, Opacity = 0.45, RenderingBias = RenderingBias.Performance };

    private static Brush Frozen(this Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    // Segoe Fluent Icons / MDL2 glyphs.
    public static class Glyph
    {
        public const string Settings = "";
        public const string Close = "";
        public const string Add = "";
        public const string Copy = "";
        public const string Globe = "";
        public const string Mic = "";
        public const string Send = "";
        public const string Volume = "";
        public const string Stop = "";
        public const string Refresh = "";
        public const string Qr = "";
        public const string Document = "";
        public const string Folder = "";
        public const string Text = "";
        public const string Link = "";
        public const string ChevronDown = "";
        public const string Search = "";
        public const string Check = "";
        public const string Info = "";
        public const string Video = "";
        public const string Picture = "";
        public const string Robot = "";
        public const string Keyboard = "";
        public const string Mouse = "";
        public const string Heart = "";
        public const string Wifi = "";
        public const string Back = "";
        public const string Power = "";
        public const string Speech = "";
        public const string Cloud = "";
        public const string Pc = "";
        public const string Warning = "";
        public const string Play = "";
        public const string Clear = "";
    }

    /// <summary>Global styles that are much shorter in XAML: thin dark scrollbars, tooltips and menus.</summary>
    public static void LoadGlobalStyles(Application app)
    {
        const string xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='ThinThumb' TargetType='Thumb'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Thumb'>
          <Border CornerRadius='3' Background='#40FFFFFF'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='6'/>
    <Setter Property='MinWidth' Value='6'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.Thumb>
              <Thumb Style='{StaticResource ThinThumb}'/>
            </Track.Thumb>
          </Track>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property='Orientation' Value='Horizontal'>
        <Setter Property='Width' Value='Auto'/>
        <Setter Property='Height' Value='6'/>
        <Setter Property='MinHeight' Value='6'/>
        <Setter Property='Template'>
          <Setter.Value>
            <ControlTemplate TargetType='ScrollBar'>
              <Track x:Name='PART_Track' IsDirectionReversed='False'>
                <Track.Thumb>
                  <Thumb Style='{StaticResource ThinThumb}'/>
                </Track.Thumb>
              </Track>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Trigger>
    </Style.Triggers>
  </Style>
  <Style TargetType='ToolTip'>
    <Setter Property='Foreground' Value='#F5F7FC'/>
    <Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ToolTip'>
          <Border Background='#F2151925' BorderBrush='#30FFFFFF' BorderThickness='1' CornerRadius='8' Padding='9,5'>
            <ContentPresenter/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='ContextMenu'>
    <Setter Property='Foreground' Value='#F5F7FC'/>
    <Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ContextMenu'>
          <Border Background='#F7151925' BorderBrush='#30FFFFFF' BorderThickness='1' CornerRadius='10' Padding='4'>
            <StackPanel IsItemsHost='True'/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='MenuItem'>
    <Setter Property='Foreground' Value='#F5F7FC'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='MenuItem'>
          <Border x:Name='Bd' Background='Transparent' CornerRadius='6' Padding='12,7'>
            <ContentPresenter ContentSource='Header' RecognizesAccessKey='True'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsHighlighted' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='#24FFFFFF'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='Separator'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Separator'>
          <Border Height='1' Margin='8,4' Background='#20FFFFFF'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
        try
        {
            var dictionary = (ResourceDictionary)XamlReader.Parse(xaml);
            app.Resources.MergedDictionaries.Add(dictionary);
        }
        catch (Exception ex)
        {
            Native.Log.Write("Global styles failed: " + ex.Message);
        }
    }
}
