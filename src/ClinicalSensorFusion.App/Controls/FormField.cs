namespace ClinicalSensorFusion.App.Controls;

/// <summary>
/// Campo di un modulo: etichetta sopra, controllo di input dentro un riquadro arrotondato.
/// Uniforma Entry, Editor e Picker, che su iOS hanno bordi nativi diversi.
/// </summary>
[ContentProperty(nameof(Field))]
public sealed class FormField : ContentView
{
    public static readonly BindableProperty LabelProperty = BindableProperty.Create(
        nameof(Label), typeof(string), typeof(FormField), string.Empty,
        propertyChanged: (b, _, v) =>
        {
            var field = (FormField)b;
            field._label.Text = (string)v;
            field._label.IsVisible = !string.IsNullOrEmpty((string)v);
        });

    public static readonly BindableProperty FieldProperty = BindableProperty.Create(
        nameof(Field), typeof(View), typeof(FormField),
        propertyChanged: (b, _, v) => ((FormField)b)._box.Content = (View?)v);

    private readonly Label _label = new() { IsVisible = false };
    private readonly Border _box = new();

    public FormField()
    {
        _label.SetDynamicResource(StyleProperty, "FieldLabel");
        _box.SetDynamicResource(StyleProperty, "FieldBox");
        Content = new VerticalStackLayout { Spacing = 6, Children = { _label, _box } };
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public View? Field
    {
        get => (View?)GetValue(FieldProperty);
        set => SetValue(FieldProperty, value);
    }
}
