using System.Windows;
using System.Windows.Controls;

namespace MemGuardian.Next.Desktop;

public partial class SettingField : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SettingField), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(SettingField), new PropertyMetadata(string.Empty));

    public SettingField() => InitializeComponent();

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public string Value
    {
        get => ValueBox.Text;
        set => ValueBox.Text = value;
    }
}
