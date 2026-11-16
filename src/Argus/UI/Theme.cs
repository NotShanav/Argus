using System;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace PresenceGuard
{
    internal static class Theme
    {
        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI");

        public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        public static SolidColorBrush Br(string hex) => new SolidColorBrush(C(hex));

        public static string StateHex(GuardState s) => s switch
        {
            GuardState.Idle => "#34D399",
            GuardState.Checking => "#FBBF24",
            GuardState.Paused => "#8B93A1",
            GuardState.Locked => "#6B7280",
            GuardState.NeedsEnrollment => "#3B82F6",
            GuardState.CameraProblem => "#EF4444",
            _ => "#8B93A1"
        };

        /// <summary>Runs on the WPF dispatcher (inline if already there). Never throws.</summary>
        public static void UI(Action a)
        {
            try
            {
                var d = System.Windows.Application.Current?.Dispatcher;
                if (d == null) return;
                if (d.CheckAccess()) { try { a(); } catch { } }
                else d.BeginInvoke(new Action(() => { try { a(); } catch { } }));
            }
            catch { }
        }

        /// <summary>Argus logo (embedded PNG) for WPF; null if missing.</summary>
        public static System.Windows.Media.Imaging.BitmapSource? Logo()
        {
            try
            {
                using var st = typeof(Theme).Assembly.GetManifestResourceStream("Argus.tray.png");
                if (st == null) return null;
                var b = new System.Windows.Media.Imaging.BitmapImage();
                b.BeginInit();
                b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                b.StreamSource = st;
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch { return null; }
        }

        public static ResourceDictionary Styles() => (ResourceDictionary)XamlReader.Parse(Xaml);

        private const string Xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style x:Key="Toggle" TargetType="CheckBox">
    <Setter Property="Foreground" Value="#E6E8EB"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="CheckBox">
          <Grid Background="Transparent">
            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
            <ContentPresenter VerticalAlignment="Center" TextElement.Foreground="{TemplateBinding Foreground}"/>
            <Border x:Name="Track" Grid.Column="1" Width="38" Height="20" CornerRadius="10" Background="#2A2F38" VerticalAlignment="Center">
              <Ellipse x:Name="Thumb" Width="14" Height="14" Fill="#C9CED6" HorizontalAlignment="Left" Margin="3,0,0,0"/>
            </Border>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="Track" Property="Background" Value="#22D3EE"/>
              <Setter TargetName="Thumb" Property="HorizontalAlignment" Value="Right"/>
              <Setter TargetName="Thumb" Property="Margin" Value="0,0,3,0"/>
              <Setter TargetName="Thumb" Property="Fill" Value="#0F1115"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="Accent" TargetType="Button">
    <Setter Property="Foreground" Value="#0F1115"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="B" Background="#22D3EE" CornerRadius="8" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="B" Property="Opacity" Value="0.85"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="B" Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="Flat" TargetType="Button">
    <Setter Property="Foreground" Value="#E6E8EB"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="B" Background="#262B33" CornerRadius="8" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="B" Property="Background" Value="#303640"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="B" Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="Slim" TargetType="Slider">
    <Setter Property="Height" Value="20"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Slider">
          <Grid Background="Transparent" Height="20">
            <Border Height="4" CornerRadius="2" Background="#2A2F38" VerticalAlignment="Center"/>
            <Track x:Name="PART_Track">
              <Track.DecreaseRepeatButton>
                <RepeatButton Command="Slider.DecreaseLarge" Focusable="False">
                  <RepeatButton.Template><ControlTemplate TargetType="RepeatButton">
                    <Border Height="4" CornerRadius="2" Background="#22D3EE" VerticalAlignment="Center"/>
                  </ControlTemplate></RepeatButton.Template>
                </RepeatButton>
              </Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton>
                <RepeatButton Command="Slider.IncreaseLarge" Focusable="False">
                  <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template>
                </RepeatButton>
              </Track.IncreaseRepeatButton>
              <Track.Thumb>
                <Thumb Width="14" Height="14">
                  <Thumb.Template><ControlTemplate TargetType="Thumb"><Ellipse Fill="#E6E8EB"/></ControlTemplate></Thumb.Template>
                </Thumb>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="ScrollBar">
    <Setter Property="Width" Value="6"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ScrollBar">
          <Track x:Name="PART_Track" IsDirectionReversed="True">
            <Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageUpCommand" Opacity="0" Focusable="False"/></Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageDownCommand" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
            <Track.Thumb>
              <Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border CornerRadius="3" Background="#3A404B"/></ControlTemplate></Thumb.Template></Thumb>
            </Track.Thumb>
          </Track>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>
""";
    }
}
