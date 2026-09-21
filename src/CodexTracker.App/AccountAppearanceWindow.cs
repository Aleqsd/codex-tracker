using Microsoft.Win32;
using System.Windows.Media.Imaging;

namespace CodexTracker.App;

internal sealed class AccountAppearanceWindow : ThemedWindow
{
    private readonly PreferencesStore _preferences;
    private readonly Guid _id;
    private readonly TextBox _name;
    private readonly Border _preview;
    private readonly StackPanel _form = new();
    private readonly TextBlock _privacyHint;
    private readonly Button _save;
    private BitmapSource? _image;
    private bool _imageChanged;

    public AccountAppearanceWindow(Window owner, PreferencesStore preferences, Guid id, ThemeManager theme)
        : base(owner, "Personnaliser le compte", theme, 450, 440)
    {
        _preferences = preferences; _id = id;
        var appearance = preferences.Current.Appearances.GetValueOrDefault(id) ?? new();
        _image = AvatarStore.Load(preferences.DataDirectory, appearance.AvatarFile);
        _privacyHint = Ui.Text("Désactivez le mode confidentialité pour personnaliser ce compte.", 12, "MutedBrush"); Body.Children.Add(_privacyHint);
        _preview = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(16), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        _form.Children.Add(_preview); UpdatePreview();
        var actions = new WrapPanel();
        var choose = new Button { Content = "Choisir une photo…", Margin = new Thickness(0, 0, 8, 0) };
        choose.Click += (_, _) => ChooseImage(); actions.Children.Add(choose);
        var remove = new Button { Content = "Utiliser les initiales", Style = (Style)FindResource("QuietButton") };
        remove.Click += (_, _) => { _image = null; _imageChanged = true; UpdatePreview(); }; actions.Children.Add(remove); _form.Children.Add(actions);
        var hint = Ui.Text("Image locale, recadrée au centre. PNG ou JPEG, 8 Mo maximum.", 11, "MutedBrush"); hint.Margin = new Thickness(0, 8, 0, 20); _form.Children.Add(hint);
        _form.Children.Add(Ui.Text("Nom affiché", 12));
        _name = new TextBox { Text = appearance.Name ?? "", MaxLength = 48, Margin = new Thickness(0, 8, 0, 6), Padding = new Thickness(9) };
        _name.SetResourceReference(BackgroundProperty, "PanelBrush"); _name.SetResourceReference(ForegroundProperty, "TextBrush"); _name.SetResourceReference(BorderBrushProperty, "LineBrush");
        _form.Children.Add(_name); _form.Children.Add(Ui.Text("Laissez vide pour afficher l’adresse. L’identité de connexion reste inchangée.", 11, "MutedBrush")); Body.Children.Add(_form);
        _save = new Button { Content = "Enregistrer", Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        _save.Click += (_, _) => Save(); Body.Children.Add(_save);
        preferences.Changed += Changed; Closed += (_, _) => preferences.Changed -= Changed; SyncPrivacy();
    }
    private void Changed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(SyncPrivacy);
    private void SyncPrivacy()
    {
        bool hidden = _preferences.Current.PrivacyMode;
        _form.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        _privacyHint.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed; _save.IsEnabled = !hidden;
    }
    private void UpdatePreview() => _preview.Background = _image is null ? ThemeManager.GetBrush("AvatarBrush") : new ImageBrush(_image) { Stretch = Stretch.UniformToFill };
    private void ChooseImage()
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg", Title = "Photo du compte" };
        if (dialog.ShowDialog(this) != true) return;
        try { _image = AvatarStore.ReadImage(dialog.FileName); _imageChanged = true; UpdatePreview(); }
        catch (Exception) { Error("Impossible de lire cette image. Choisissez un PNG ou JPEG local de moins de 8 Mo."); }
    }
    private void Save()
    {
        if (_preferences.Current.PrivacyMode) return;
        var old = _preferences.Current.Appearances.GetValueOrDefault(_id) ?? new(); string? created = null;
        try
        {
            var file = _imageChanged ? _image is null ? null : created = AvatarStore.Save(_preferences.DataDirectory, _image) : old.AvatarFile;
            var name = new string(_name.Text.Trim().Where(c => !char.IsControl(c)).ToArray());
            _preferences.Update(p => p with { Appearances = new(p.Appearances) { [_id] = new(name.Length == 0 ? null : name, file) } });
            if (_imageChanged && old.AvatarFile != file) AvatarStore.Remove(_preferences.DataDirectory, old.AvatarFile);
            Close();
        }
        catch (Exception) { if (created is not null) AvatarStore.Remove(_preferences.DataDirectory, created); Error("La personnalisation n’a pas pu être enregistrée."); }
    }
    private void Error(string text) => new TrackerDialog(this, "Personnalisation", text, "Fermer", null).ShowDialog();
}
