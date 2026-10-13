using System.Globalization;
using ClinicalSensorFusion.Core.Localization;

namespace ClinicalSensorFusion.App.Views;

/// <summary>
/// Scelta della lingua al primo avvio. La lingua scelta si applica subito (<see cref="Loc.Use"/>) e la pagina
/// si ritraduce mentre si sceglie; <see cref="Completed"/> riceve il codice da salvare (vuoto = «come il telefono»).
/// </summary>
public partial class SetupPage : ContentPage
{
    private readonly List<(string Code, Border Tile, Label Name, Label Detail)> _options = [];
    private string _selected = string.Empty;

    public SetupPage()
    {
        InitializeComponent();

        // Prima voce: la lingua del telefono, che è anche quella con cui la pagina si apre.
        AddOption(string.Empty);
        foreach (var (code, _) in Loc.Supported)
        {
            AddOption(code);
        }

        Select(string.Empty);
    }

    /// <summary>L'utente ha confermato: codice della lingua scelta, vuoto per seguire il telefono.</summary>
    public event EventHandler<string>? Completed;

    private void AddOption(string code)
    {
        var name = new Label { FontAttributes = FontAttributes.Bold, FontSize = 17 };
        var detail = new Label { FontSize = 13 };
        detail.SetAppThemeColor(Label.TextColorProperty, Color("MutedLight"), Color("MutedDark"));

        var tile = new Border
        {
            Style = (Style)Application.Current!.Resources["Tile"],
            Padding = new Thickness(16, 14),
            Content = new VerticalStackLayout { Spacing = 2, Children = { name, detail } },
        };
        tile.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Select(code)) });

        _options.Add((code, tile, name, detail));
        Options.Children.Add(tile);
    }

    private void Select(string code)
    {
        _selected = code;
        Loc.Use(Loc.Resolve(code, CultureInfo.CurrentUICulture));

        Heading.Text = Loc.T("Scegli la lingua");
        Explanation.Text = Loc.T("L'app userà questa lingua per le schermate, la guida vocale e i rapporti. Potrai cambiarla quando vuoi dalle Impostazioni.");
        Continue.Text = Loc.T("Continua");

        var phone = Loc.Supported.First(l => l.Code == Loc.LanguageFor(CultureInfo.CurrentUICulture)).Name;
        foreach (var option in _options)
        {
            var chosen = option.Code == code;
            option.Name.Text = option.Code.Length == 0 ? Loc.T("Come il telefono") : Loc.Supported.First(l => l.Code == option.Code).Name;
            option.Detail.Text = option.Code.Length == 0 ? phone : string.Empty;
            option.Detail.IsVisible = option.Code.Length == 0;
            option.Tile.StrokeThickness = chosen ? 2 : 1;
            if (chosen)
            {
                option.Tile.Stroke = Color("Accent");
                option.Tile.SetAppThemeColor(BackgroundColorProperty, Color("AccentSoftLight"), Color("AccentSoftDark"));
            }
            else
            {
                option.Tile.SetAppThemeColor(Border.StrokeProperty, Color("StrokeLight"), Color("StrokeDark"));
                option.Tile.SetAppThemeColor(BackgroundColorProperty, Color("CardLight"), Color("CardDark"));
            }

            SemanticProperties.SetDescription(option.Tile, option.Name.Text);
            SemanticProperties.SetHint(option.Tile, chosen ? Loc.T("Selezionata") : string.Empty);
        }
    }

    private void OnContinueClicked(object? sender, EventArgs e) => Completed?.Invoke(this, _selected);

    private static Color Color(string key) => (Color)Application.Current!.Resources[key];
}
