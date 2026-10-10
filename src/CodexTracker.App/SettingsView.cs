using System.Diagnostics;
using CodexTracker.App.Updates;
using AppThemeMode = CodexTracker.App.ThemeMode;

namespace CodexTracker.App;

internal sealed record ThemeChoice(ThemeMode Value, string Label) { public override string ToString() => Label; }
internal sealed record NumberChoice(int Value, string Label) { public override string ToString() => Label; }
internal sealed record LanguageChoice(AppLanguage Value, string Label) { public override string ToString() => Label; }
internal sealed class SettingsView : UserControl, IDisposable
{
    private readonly MainWindow _owner;
    private readonly PreferencesStore _preferences;
    private readonly ApplicationCommands _commands;
    private readonly UpdateService _updates;
    private readonly AutomaticUpdater? _automaticUpdates;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<(CheckBox Box, Func<TrackerPreferences, bool> Read)> _toggles = new();
    private readonly ComboBox _themeSelector;
    private readonly ComboBox _refreshSelector;
    private readonly ComboBox _languageSelector;
    private readonly TextBlock _languageHint;
    private readonly Button _restart;
    private readonly TextBlock _updateStatus;
    private readonly TextBlock _preparationStatus;
    private readonly TextBlock _automaticStatus;
    private readonly Button _check, _install;
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? _nextCheckAt;
    private bool _updateBusy;
    private UpdateRelease? _release;
    private bool _syncing, _closed;
    private bool _includePrereleases;
    private readonly StackPanel _health = new();
    private readonly TextBlock _announcementsStatus = Ui.Text("", 11, "MutedBrush");
    private readonly Button _checkAnnouncements = new() { Content = Loc.T("Vérifier les annonces"),HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 9, 0, 0) };
    private string[] _healthMessages = [];
    private bool _recoveryRequired;
    private readonly bool _demo;
    private readonly TextBlock _claudeStatus = new() { FontSize = 12, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _navigation = new() { Margin = new Thickness(0, 10, 18, 0) };
    private readonly ScrollViewer _pageScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Dictionary<string, (StackPanel Content, Button Navigation)> _pages = new();
    private StackPanel _page = new();
    private StackPanel? _group;
    private readonly SelectionPill _navigationPill;
    internal string CurrentPage { get; private set; } = "Général";

    public SettingsView(MainWindow owner, PreferencesStore preferences, UpdateService updates, bool demo, AutomaticUpdater? automaticUpdates = null)
    {
        _owner = owner; _preferences = preferences; _updates = updates; _demo = demo; _automaticUpdates = automaticUpdates;
        _includePrereleases = preferences.Current.IncludePrereleaseUpdates;
        _commands = new(preferences, owner.Reminders.Secrets);
        Focusable = false;
        var layout = new Grid { Margin = new Thickness(0, 4, 0, 0) }; layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
        var navigationPill = new Border { CornerRadius = new CornerRadius(8) }; navigationPill.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        var navigationHost = new Grid(); navigationHost.Children.Add(navigationPill); navigationHost.Children.Add(_navigation);
        _navigationPill = new SelectionPill(navigationHost, navigationPill, () => _pages.TryGetValue(CurrentPage, out var page) ? page.Navigation : null);
        UiMotion.SetRise(_pageScroll, 8);
        var navigationScroll = new ScrollViewer { Content = navigationHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        layout.Children.Add(navigationScroll);
        Grid.SetColumn(_pageScroll, 1); layout.Children.Add(_pageScroll);
        Content = layout;
        System.Windows.Input.KeyboardNavigation.SetTabNavigation(_navigation, System.Windows.Input.KeyboardNavigationMode.Continue);
        Page("Général", Loc.T("Général"), Loc.T("Adaptez le suivi à votre façon de travailler."));
        Section(Loc.T("Apparence"));
        _themeSelector = new ComboBox { Tag = FindResource("DropdownThemeIcon"), ItemsSource = new[] { new ThemeChoice(AppThemeMode.System, Loc.T("Comme Windows")), new ThemeChoice(AppThemeMode.Light, Loc.T("Clair")), new ThemeChoice(AppThemeMode.Dark, Loc.T("Sombre")) }, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
        Row(Labelled(Loc.T("Thème"), _themeSelector));
        _themeSelector.SelectionChanged += (_, _) => { if (!_syncing && _themeSelector.SelectedValue is ThemeMode mode) Save(p => p with { ThemeMode = mode }); };
        _languageSelector = new ComboBox { Tag = FindResource("SettingsGeneralIcon"), ItemsSource = new[] { new LanguageChoice(AppLanguage.System, Loc.T("Comme Windows")), new LanguageChoice(AppLanguage.French, "Français"), new LanguageChoice(AppLanguage.English, "English") }, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
        var language = new StackPanel(); language.Children.Add(Labelled(Loc.T("Langue"), _languageSelector));
        _languageHint = Ui.Text(Loc.T("Appliquée au prochain démarrage du tracker."), 11, "MutedBrush"); _languageHint.Margin = new Thickness(0, 6, 0, 0); _languageHint.Visibility = Visibility.Collapsed; language.Children.Add(_languageHint);
        _restart = new Button { Content = Loc.T("Redémarrer maintenant"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, IsEnabled = !demo };
        _restart.Click += async (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--relaunch-after {Environment.ProcessId}") { UseShellExecute = false }); }
            catch (Exception error) { ShowError(error.Message); return; }
            await ((App)System.Windows.Application.Current).ExitAsync();
        };
        language.Children.Add(_restart); Row(language);
        _languageSelector.SelectionChanged += (_, _) =>
        {
            if (_syncing || _languageSelector.SelectedValue is not AppLanguage chosen) return;
            Save(p => p with { Language = chosen });
            var pending = Loc.Resolve(chosen) == AppLanguage.English != Loc.IsEnglish;
            _languageHint.Visibility = _restart.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        };
        Toggle(Loc.T("Aperçu au survol de l’icône"), Loc.T("Le quota et le prochain reset, sans ouvrir le panneau."), p => p.HoverPreview, (p, value) => p with { HoverPreview = value });
        Section(Loc.T("Actualisation"));
        _refreshSelector = Choice(Loc.T("Compte actif"), "DropdownRefreshIcon", [new(1, Loc.T("Chaque minute")), new(2, Loc.T("Toutes les 2 min")), new(5, Loc.T("Toutes les 5 min"))], v => Save(p => p with { RefreshMinutes = v }));
        Toggle(Loc.T("Adapter à mon activité"), Loc.T("Passe à 10 min après 5 min sans clavier ni souris. Reprend la fréquence choisie à votre retour. La détection des comptes reste immédiate."), p => p.AdaptiveRefresh, (p, v) => p with { AdaptiveRefresh = v });
        Section("Claude Code");
        var claude = Block();
        claude.Children.Add(Ui.Text(Loc.T("L’application Claude fournit automatiquement ses derniers quotas locaux, sans configuration. Les comptes personnels et d’entreprise sont séparés, même sur la même adresse. Pour les relevés du terminal, ajoutez le réglage ci-dessous puis ouvrez une nouvelle session."), 12, "MutedBrush"));
        var copyClaude = new Button { Content = Loc.T("Copier le réglage Claude Code"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 12), IsEnabled = !demo };
        copyClaude.Click += (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(Codex.ClaudeCodeObservations.Configuration(Environment.ProcessPath!)); copyClaude.Content = Loc.T("Réglage copié"); }
            catch (Exception) { ShowError(Loc.T("Le presse-papiers est indisponible. Réessayez.")); }
        };
        claude.Children.Add(copyClaude);
        claude.Children.Insert(1, _claudeStatus);
        claude.Children.Add(Ui.Text(Loc.T("Dans ~/.claude/settings.json (ou CLAUDE_CONFIG_DIR) : fusionnez SessionStart avec vos hooks existants et ajoutez statusLine. Si vous avez déjà une barre de statut, conservez-la et appelez le collecteur depuis son script. Les quotas arrivent quand vous utilisez Claude Code ; les dates de relevé sont conservées."), 11, "MutedBrush"));
        Page("Rappels", Loc.T("Rappels"), Loc.T("Choisissez les échéances, les comptes et les canaux utiles."));
        Section(Loc.T("Échéances"));
        ReloadableSection(() => ReminderSettingsView.Rules(preferences, owner.TrackerService, _commands));
        Section(Loc.T("Quotas"));
        var quotas = Block();
        quotas.Children.Add(Ui.Text(Loc.T("Prévenir quand le quota restant franchit un seuil."), 12, "MutedBrush"));
        var thresholds = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        thresholds.Children.Add(PreferenceCheck(Loc.T("20 %"), p => p.Alert20, (p, v) => p with { Alert20 = v }));
        thresholds.Children.Add(PreferenceCheck(Loc.T("10 %"), p => p.Alert10, (p, v) => p with { Alert10 = v }));
        thresholds.Children.Add(PreferenceCheck(Loc.T("5 %"), p => p.Alert5, (p, v) => p with { Alert5 = v })); quotas.Children.Add(thresholds);
        Toggle(Loc.T("Prévenir avant l’épuisement"), Loc.T("Si le rythme observé vide un quota avant sa recharge, une notification arrive environ une heure avant. Une fois par période, sans estimation si la consommation est irrégulière."),
            p => p.ForecastNotifications, (p, value) => p with { ForecastNotifications = value });
        Row(ReminderSettingsView.WindowsTest(owner.Reminders, demo));
        Toggle(Loc.T("Prévenir après un reset"), Loc.T("Notification Windows après confirmation par Codex, ou à l’échéance d’un compte inactif : quota probablement à 100 %, à confirmer. Reprise des échéances récentes après veille."), p => p.ResetNotifications, (p, value) => p with { ResetNotifications = value });
        Section(Loc.T("Annonces de resets généraux"));
        Toggle(Loc.T("Vérifier les annonces publiques"), Loc.T("Toutes les 15 min : index communautaire shixilin.com, puis vérification des posts originaux via X. Aucune donnée de compte transmise. Certaines formulations restent indétectables."), p => p.MonitorGlobalResets, (p, value) => p with { MonitorGlobalResets = value });
        var announcements = Block();
        announcements.Children.Add(Ui.Text(Loc.T("Les comptes inactifs concernés affichent ≈100 % pendant 24 h maximum (5 h pour le quota court). Le dernier relevé reste conservé. Les notifications suivent le réglage « Prévenir après un reset »."), 11, "MutedBrush"));
        _announcementsStatus.Margin = new Thickness(0, 8, 0, 0); announcements.Children.Add(_announcementsStatus);
        _checkAnnouncements.Click += async (_, _) =>
        {
            _checkAnnouncements.IsEnabled = false;
            try { await _owner.TrackerService.CheckGlobalResetAnnouncementsAsync(_lifetime.Token); }
            catch (OperationCanceledException) { }
            finally { if (!_closed) RefreshHealth(); }
        };
        _checkAnnouncements.Margin = new Thickness(0, 12, 0, 0); announcements.Children.Add(_checkAnnouncements);
        Page("Canaux", Loc.T("Canaux"), Loc.T("Notifications Windows et connecteurs facultatifs."));
        Section(null);
        ReloadableSection(() => ReminderSettingsView.Channels(preferences, owner.Reminders, demo, _commands));
        Page("Historique", Loc.T("Historique"), Loc.T("Le suivi local de vos rappels sur les 30 derniers jours."));
        Section(null);
        Row(ReminderSettingsView.History(owner.Reminders));
        Page("Calendrier", Loc.T("Calendrier"), Loc.T("Retrouvez les échéances de vos comptes dans votre agenda."));
        Section(null);
        var agenda = Block();
        var calendar = new Button { Content = Loc.T("Ouvrir les options Google Agenda…"), HorizontalAlignment = HorizontalAlignment.Left };
        calendar.Click += (_, _) => owner.OpenCalendar(); agenda.Children.Add(calendar);
        var calendarHint = Ui.Text(Loc.T("Ajout direct d’une échéance ou import groupé. Export compatible avec les autres agendas."), 11, "MutedBrush"); calendarHint.Margin = new Thickness(0, 9, 0, 0); agenda.Children.Add(calendarHint);
        Page("Assistants", Loc.T("Assistants"), Loc.T("Pilotez le tracker depuis votre assistant de code."));
        Section(null);
        Row(Mcp.AssistantSettingsView.Create(owner, preferences, demo));
        Page("Application", Loc.T("Application"), Loc.T("Démarrage, mises à jour et version installée."));
        Section(Loc.T("Démarrage"));
        var startup = new CheckBox { Content = Loc.T("Démarrer avec Windows"), IsChecked = StartupSettings.IsEnabled, IsEnabled = !demo, Style = (Style)FindResource("Switch") };
        startup.Click += (_, _) => { try { StartupSettings.SetEnabled(startup.IsChecked == true); } catch (Exception error) { ShowError(error.Message); startup.IsChecked = StartupSettings.IsEnabled; } }; Row(startup);
        Section(Loc.T("Mises à jour"));
        var version = Ui.Text(_updates.CurrentVersion, 13); version.FontWeight = FontWeights.Medium;
        Row(Labelled(Loc.T("Version installée"), version));
        Toggle(Loc.T("Télécharger automatiquement les mises à jour"), Loc.T("Recherche au démarrage puis toutes les 15 minutes sur GitHub. Le suivi continue pendant le téléchargement."),
            p => p.DownloadUpdatesAutomatically, (p, value) => p with { DownloadUpdatesAutomatically = value });
        Toggle(Loc.T("Installer au prochain démarrage du tracker"), Loc.T("Installe une version déjà téléchargée et vérifiée. Aucune fermeture automatique pendant votre utilisation."),
            p => p.InstallUpdatesAtStartup, (p, value) => p with { InstallUpdatesAtStartup = value });
        Toggle(Loc.T("Recevoir aussi les préversions"), Loc.T("Désactivé : versions stables uniquement. Activez pour essayer les versions bêta avant leur validation complète."),
            p => p.IncludePrereleaseUpdates, (p, value) => p with { IncludePrereleaseUpdates = value });
        var lastUpdate = demo ? null : updates.ReadLastResult();
        var status = Block();
        _updateStatus = Ui.Text(demo ? Loc.T("Les mises à jour sont désactivées dans la démonstration.") : lastUpdate is not null ? Display.SafeText(lastUpdate.Message, preferences.Current.PrivacyMode) : Loc.T("Vérifiez les versions publiées sur le dépôt officiel."), 11, "MutedBrush"); _updateStatus.Margin = new Thickness(0, 0, 0, 10); status.Children.Add(_updateStatus);
        _preparationStatus = Ui.Text("", 11, "MutedBrush"); _preparationStatus.Margin = new Thickness(0, 0, 0, 10); _preparationStatus.Visibility = Visibility.Collapsed; status.Children.Add(_preparationStatus);
        _automaticStatus = Ui.Text("", 11, "MutedBrush"); _automaticStatus.Margin = new Thickness(0, 0, 0, 10); _automaticStatus.Visibility = demo ? Visibility.Collapsed : Visibility.Visible; status.Children.Add(_automaticStatus);
        var actions = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        _check = new Button { Content = Loc.T("Rechercher une mise à jour"), IsEnabled = !demo, Margin = new Thickness(0, 0, 8, 0) }; _check.Click += async (_, _) => await CheckAsync(); actions.Children.Add(_check);
        _install = new Button { Content = Loc.T("Installer et relancer"), Visibility = Visibility.Collapsed, Style = (Style)FindResource("PrimaryButton") }; _install.Click += async (_, _) => await InstallAsync(); actions.Children.Add(_install); status.Children.Add(actions);
        var releases = new Button { Content = Loc.T("Voir les versions sur GitHub ↗"),Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        releases.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(UpdateService.ReleasesPage.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) { ShowError(error.Message); }
        };
        status.Children.Add(releases);
        if (!demo && updates.ReadLatestCheck() is { } cached)
        {
            ShowCheckResult(cached);
            if (lastUpdate is { Success: false }) _updateStatus.Text = Display.SafeText(lastUpdate.Message, preferences.Current.PrivacyMode) + "\n" + _updateStatus.Text;
        }
        if (!demo) { updates.CheckChanged += CheckChanged; updates.PreparationChanged += PreparationChanged; ShowPreparation(); }
        _updateTimer.Tick += (_, _) => SyncUpdateButton();
        Loaded += (_, _) => { if (!_closed) { RefreshHealth(); RefreshCheck(); _updateTimer.Start(); } };
        Unloaded += (_, _) => _updateTimer.Stop();
        Section(Loc.T("Profil"));
        var transfer = Block();
        transfer.Children.Add(Ui.Text(Loc.T("Emportez vos comptes, leurs derniers relevés, l’historique, les réglages, noms et avatars sur un autre PC. Le fichier est chiffré par un mot de passe ; les clés des canaux et l’accès des assistants restent sur ce PC."), 12, "MutedBrush"));
        var transferActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var exportProfile = new Button { Content = Loc.T("Exporter le profil…"), IsEnabled = !demo, Margin = new Thickness(0, 0, 8, 0) };
        exportProfile.Click += async (_, _) => await ExportProfileAsync(); transferActions.Children.Add(exportProfile);
        var importProfile = new Button { Content = Loc.T("Importer un profil…"), IsEnabled = !demo };
        importProfile.Click += async (_, _) => await ImportProfileAsync(); transferActions.Children.Add(importProfile);
        transfer.Children.Add(transferActions);
        Section(Loc.T("Diagnostic"));
        var support = Block();
        support.Children.Add(_health);
        var diagnostic = new Button { Content = Loc.T("Préparer un diagnostic…"), HorizontalAlignment = HorizontalAlignment.Left };
        diagnostic.Click += (_, _) => ShowDiagnostic(); support.Children.Add(diagnostic);
        var privacy = Ui.Text(Loc.T("Aperçu avant copie. Aucun compte, quota, chemin personnel ni identifiant secret."), 11, "MutedBrush"); privacy.Margin = new Thickness(0, 9, 0, 0); support.Children.Add(privacy);
        _group = null;
        var quit = new Button { Content = Loc.T("Quitter Codex Tracker"),Style = (Style)FindResource("QuietButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-2, 16, 0, 0) };
        quit.SetResourceReference(ForegroundProperty, "DangerBrush");
        quit.Click += async (_, _) => await ((App)System.Windows.Application.Current).ExitAsync(); _page.Children.Add(quit);
        preferences.Changed += PreferencesChanged;
        Sync();
        ShowPage("Général");
    }
    public void Dispose()
    {
        if (_closed) return;
        _closed = true; _updateTimer.Stop(); _lifetime.Cancel();
        _preferences.Changed -= PreferencesChanged; _updates.CheckChanged -= CheckChanged; _updates.PreparationChanged -= PreparationChanged;
        _pageScroll.Content = null; Content = null;
    }
    private void ReloadableSection(Func<FrameworkElement> create)
    {
        var host = new ContentControl { Content = create(), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var reload = new Button { Content = Loc.T("Recharger la section"), Style = (Style)FindResource("LinkButton"),
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 2, 0, 6),
            ToolTip = Loc.T("Relire la configuration actuelle. Les modifications non enregistrées de cette section seront abandonnées.") };
        reload.Click += (_, _) => { host.Content = create(); _pageScroll.ScrollToTop(); };
        var block = Block(); block.Children.Add(reload); block.Children.Add(host);
    }
    /// <summary>The French <paramref name="key"/> identifies the page for navigation; <paramref name="title"/> is displayed.</summary>
    private void Page(string key, string title, string description)
    {
        _page = new StackPanel { Margin = new Thickness(6, 10, 14, 28) }; _group = null; UiMotion.SetStagger(_page, true);
        var heading = Ui.Text(title, 22); heading.FontWeight = FontWeights.Medium; _page.Children.Add(heading);
        var hint = Ui.Text(description, 12, "MutedBrush"); hint.Margin = new Thickness(0, 5, 0, 0); _page.Children.Add(hint);
        var button = new Button { Content = title, Style = (Style)FindResource("SettingsNavigation"), Margin = new Thickness(0, 0, 0, 2), Tag = FindResource(key switch { "Assistants" => "SettingsAssistantsIcon", "Général" => "SettingsGeneralIcon", "Rappels" => "SettingsNotificationsIcon", "Canaux" => "SettingsChannelsIcon", "Historique" => "DropdownClockIcon", "Calendrier" => "DropdownCalendarIcon", _ => "SettingsApplicationIcon" }) };
        button.Click += (_, _) => ShowPage(key); _navigation.Children.Add(button); _pages.Add(key, (_page, button));
    }
    internal void ShowPage(string title)
    {
        if (!_pages.TryGetValue(title, out var page)) return;
        if (CurrentPage != title || _pageScroll.Content is null)
        {
            CurrentPage = title; _pageScroll.Content = page.Content; _pageScroll.ScrollToTop();
            UiMotion.FadeIn(_pageScroll);
        }
        RefreshHealth(); RefreshClaudeStatus();
        foreach (var (name, value) in _pages)
            System.Windows.Automation.AutomationProperties.SetItemStatus(value.Navigation, name == title ? Loc.T("Section active") : "");
        _navigationPill.Move();
    }
    /// <summary>Starts a titled card; following rows and blocks are placed inside it.</summary>
    private void Section(string? title)
    {
        if (title is not null)
        {
            var text = Ui.Text(title, 12, "MutedBrush"); text.FontWeight = FontWeights.Medium; text.Margin = new Thickness(2, 24, 0, 9); _page.Children.Add(text);
        }
        _group = new StackPanel();
        var card = Ui.Panel(_group, new Thickness(18, 2, 18, 2)); card.Margin = new Thickness(0, title is null ? 22 : 0, 0, 0); _page.Children.Add(card);
    }
    private void Row(FrameworkElement row)
    {
        var target = _group ?? _page;
        if (_group is { Children.Count: > 0 }) target.Children.Add(Ui.Divider(new Thickness(0)));
        row.Margin = new Thickness(0, 14, 0, 14); target.Children.Add(row);
    }
    private StackPanel Block() { var block = new StackPanel(); Row(block); return block; }
    private static Grid Labelled(string title, FrameworkElement control)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Ui.Text(title, 13); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(0, 0, 16, 0); row.Children.Add(label);
        if (control is ComboBox) control.Width = 190;
        control.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(control, 1); row.Children.Add(control); return row;
    }
    private ComboBox Choice(string title, string icon, NumberChoice[] choices, Action<int> save)
    {
        var combo = new ComboBox { Tag = FindResource(icon), ItemsSource = choices, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
        combo.SelectionChanged += (_, _) => { if (!_syncing && combo.SelectedValue is int value) save(value); };
        Row(Labelled(title, combo)); return combo;
    }
    private CheckBox PreferenceCheck(string title, Func<TrackerPreferences, bool> read, Func<TrackerPreferences, bool, TrackerPreferences> set)
    {
        var check = new CheckBox { Content = title, Margin = new Thickness(0, 0, 24, 0) };
        check.Click += (_, _) => { if (!_syncing) Save(p => set(p, check.IsChecked == true)); }; _toggles.Add((check, read)); return check;
    }
    private void Toggle(string title, string? description, Func<TrackerPreferences, bool> read, Func<TrackerPreferences, bool, TrackerPreferences> set)
    {
        var check = PreferenceCheck(title, read, set); check.Style = (Style)FindResource("Switch"); check.Margin = new Thickness(0);
        var row = new StackPanel(); row.Children.Add(check);
        if (description is not null) { var hint = Ui.Text(description, 11, "MutedBrush"); hint.Margin = new Thickness(0, 4, 64, 0); row.Children.Add(hint); }
        Row(row);
    }
    private void Save(Func<TrackerPreferences, TrackerPreferences> update)
    {
        try { _commands.SavePreferences(update); }
        catch (Exception error) { ShowError(error.Message); Sync(); }
    }
    private void PreferencesChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Sync);
    private void Sync()
    {
        if (_closed) return;
        if (_includePrereleases != _preferences.Current.IncludePrereleaseUpdates)
        {
            _includePrereleases = _preferences.Current.IncludePrereleaseUpdates;
            _updates.SetIncludePrereleases(_includePrereleases);
            _release = null; _nextCheckAt = null; _install.Visibility = Visibility.Collapsed;
            _updateStatus.Text = _includePrereleases ? Loc.T("Préversions incluses. Recherchez une mise à jour.") : Loc.T("Versions stables uniquement. Recherchez une mise à jour.");
            ShowPreparation();
            _ = ReloadPreparedAsync();
        }
        _syncing = true; _themeSelector.SelectedValue = _preferences.Current.ThemeMode;
        _languageSelector.SelectedValue = _preferences.Current.Language;
        _refreshSelector.SelectedValue = _preferences.Current.RefreshMinutes;
        foreach (var (box, read) in _toggles) box.IsChecked = read(_preferences.Current); _syncing = false;
        _updateStatus.Text = Display.SafeText(_updateStatus.Text, _preferences.Current.PrivacyMode);
        SyncUpdateButton();
        RefreshHealth();
    }
    // Terminal readings carry the exact reset dates: say whether they are set up and still arriving.
    private void RefreshClaudeStatus()
    {
        if (_closed) return;
        if (_demo) { _claudeStatus.Visibility = Visibility.Collapsed; return; }
        var location = Codex.ClaudeCodeLocation.Resolve(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var status = new Codex.ClaudeCodeObservations(_preferences.DataDirectory, location).ReadStatus(Environment.ProcessPath!);
        var stale = status.LastReading is { } last && DateTimeOffset.UtcNow - last > TimeSpan.FromDays(7);
        _claudeStatus.Text = status.LastReading is { } reading
            ? Loc.F("Relevés du terminal actifs · dernier le {0}", reading.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Loc.Culture)) +
              (stale ? "\n" + Loc.T("Ancien : ouvrez une session Claude Code pour remettre à jour la date du reset.") : "")
            : status.Configured ? Loc.T("Réglage détecté · en attente d’une session Claude Code.")
            : Loc.T("Relevés du terminal non configurés : la date du reset de la semaine reste estimée.");
        _claudeStatus.SetResourceReference(TextBlock.ForegroundProperty, status.LastReading is null ? "MutedBrush" : stale ? "WarningBrush" : "GoodBrush");
    }
    internal void RefreshHealth()
    {
        if (_closed) return;
        var feed = _owner.TrackerService.State.GlobalResetFeed;
        _announcementsStatus.Text = !_preferences.Current.MonitorGlobalResets ? Loc.T("Vérification désactivée.")
            : _demo ? Loc.T("Démonstration · aucun accès réseau.")
            : (feed?.IsChecking == true ? Loc.T("Vérification des sources en cours…") + "\n" : "") +
                (feed?.CheckedAt is { } checkedAt ? Loc.F("Dernier contrôle réussi : {0} · {1}", Display.Exact(checkedAt), Display.Zone(checkedAt)) : Loc.T("Aucun contrôle réussi pour le moment.")) +
                (feed?.Error is { } error ? "\n" + error : feed?.CheckedAt is not null && feed.Announcements.All(a => !a.IsCurrent(PreviewClock.UtcNow)) ? "\n" + Loc.T("Aucune annonce récente reconnue.") : "") +
                (feed?.NextCheckAt is { } next && !feed.IsChecking ? next <= PreviewClock.UtcNow ? "\n" + Loc.T("Prochaine vérification imminente.") : "\n" + Loc.F("Prochain essai : {0}", Display.Exact(next)) : "");
        _checkAnnouncements.Content = feed?.IsChecking == true ? Loc.T("Vérification…") : Loc.T("Vérifier les annonces");
        _checkAnnouncements.IsEnabled = !_demo && _preferences.Current.MonitorGlobalResets && feed?.IsChecking != true &&
            !(PreviewClock.UtcNow < feed?.ManualRetryAt);
        _checkAnnouncements.ToolTip = _demo ? Loc.T("Aucun accès réseau dans la démonstration.") : feed?.ManualRetryAt > PreviewClock.UtcNow
            ? Loc.F("Prochain contrôle manuel possible le {0}. Les limites de la source sont respectées.", Display.Exact(feed.ManualRetryAt))
            : Loc.T("Relit les annonces publiques sans actualiser les comptes. Un contrôle manuel par minute maximum.");
        var messages = _owner.HealthWarnings();
        if (_healthMessages.SequenceEqual(messages) && _recoveryRequired == _preferences.RecoveryRequired) return;
        _healthMessages = messages; _recoveryRequired = _preferences.RecoveryRequired;
        _health.Children.Clear();
        foreach (var message in messages)
        {
            var notice = Ui.Text(message, 11, "MutedBrush"); notice.Margin = new Thickness(0, 0, 0, 10); _health.Children.Add(notice);
        }
        if (!_preferences.RecoveryRequired) return;
        var acknowledge = new Button { Content = Loc.T("Valider les réglages récupérés"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        acknowledge.Click += (_, _) =>
        {
            try { _preferences.AcknowledgeRecovery(); Sync(); }
            catch (Exception) { ShowError(Loc.T("Les réglages récupérés n’ont pas pu être enregistrés. Les fichiers existants sont conservés.")); }
        };
        _health.Children.Add(acknowledge);
    }
    private void ShowDiagnostic()
    {
        var report = _owner.BuildDiagnostic();
        var dialog = new TrackerDialog(_owner, Loc.T("Diagnostic à partager"), report, Loc.T("Copier"), Loc.T("Fermer"));
        if (dialog.ShowDialog() != true) return;
        try { Clipboard.SetText(report); }
        catch (Exception) { ShowError(Loc.T("Le presse-papiers est occupé. Réessayez dans quelques secondes.")); }
    }
    private async Task ReloadPreparedAsync()
    {
        try { if (!_demo) await _updates.LoadPreparedAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
    }
    private const string ProfileExtension = ".codextracker";
    private async Task ExportProfileAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = Loc.T("Profil Codex Tracker") + "|*" + ProfileExtension, FileName = $"CodexTracker-profil-{DateTime.Now:yyyy-MM-dd}{ProfileExtension}", DefaultExt = ProfileExtension, AddExtension = true };
        if (dialog.ShowDialog(_owner) != true) return;
        var password = AskPassword(Loc.T("Protéger le profil"), Loc.T("Ce mot de passe sera demandé à l’import. Il n’est enregistré nulle part : sans lui, le fichier est illisible."), confirm: true);
        if (password is null) return;
        try
        {
            var service = _owner.TrackerService;
            var archive = await Task.Run(() => ProfileTransfer.Export(service, _preferences, password, DateTimeOffset.UtcNow));
            await File.WriteAllBytesAsync(dialog.FileName, archive);
            ShowInfo(Loc.T("Profil exporté"), Loc.F("{0} comptes, leur historique et vos réglages sont dans le fichier chiffré. Importez-le depuis Réglages → Application sur l’autre PC.", service.State.Accounts.Count));
        }
        catch (Exception error) when (error is Codex.TrackerException or IOException or UnauthorizedAccessException) { ShowError(error.Message); }
    }
    private async Task ImportProfileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Profil Codex Tracker") + "|*" + ProfileExtension };
        if (dialog.ShowDialog(_owner) != true) return;
        var password = AskPassword(Loc.T("Importer un profil"), Loc.T("Saisissez le mot de passe choisi lors de l’export."), confirm: false);
        if (password is null) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > Codex.ProfileArchive.MaximumBytes) throw new Codex.TrackerException(Loc.T("Fichier de profil trop volumineux."));
            var archive = await File.ReadAllBytesAsync(dialog.FileName);
            var bundle = await Task.Run(() => ProfileTransfer.Read(archive, password));
            var confirm = new TrackerDialog(_owner, Loc.T("Importer ce profil ?"), Loc.F("{0} comptes, exportés le {1:dd/MM/yyyy à HH:mm}.\nLes comptes et l’historique s’ajoutent à ceux de ce PC ; un relevé plus récent ici est conservé. Les réglages, rappels, noms et avatars du fichier remplacent ceux de ce PC. Les clés des canaux et l’accès des assistants ne changent pas.",
                bundle.Profiles.Accounts.Count, bundle.ExportedAt.ToLocalTime()), Loc.T("Importer"), Loc.T("Annuler"));
            if (confirm.ShowDialog() != true) return;
            var language = _preferences.Current.Language;
            var result = await ProfileTransfer.ImportAsync(bundle, _owner.TrackerService, _commands, _preferences);
            ShowInfo(Loc.T("Profil importé"), Loc.F("{0} comptes ajoutés, {1} mis à jour.", result.Added, result.Updated) +
                (_preferences.Current.Language != language ? "\n" + Loc.T("La langue du profil s’appliquera au prochain démarrage.") : ""));
        }
        catch (Exception error) when (error is Codex.TrackerException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException) { ShowError(error.Message); }
    }
    private string? AskPassword(string title, string message, bool confirm)
    {
        var hint = message;
        while (true)
        {
            var dialog = new TrackerDialog(_owner, title, hint, Loc.T("Continuer"), Loc.T("Annuler"));
            PasswordBox Field(string label)
            {
                var caption = Ui.Text(label, 11, "MutedBrush"); caption.Margin = new Thickness(0, 16, 0, 0); dialog.Extra.Children.Add(caption);
                var box = new PasswordBox { Margin = new Thickness(0, 5, 0, 0), Padding = new Thickness(9, 7, 9, 7), MinHeight = 34 };
                System.Windows.Automation.AutomationProperties.SetName(box, label); dialog.Extra.Children.Add(box); return box;
            }
            var first = Field(Loc.T("Mot de passe"));
            var second = confirm ? Field(Loc.T("Confirmer le mot de passe")) : null;
            dialog.Loaded += (_, _) => first.Focus();
            if (dialog.ShowDialog() != true) return null;
            if (first.Password.Length < Codex.ProfileArchive.MinimumPasswordLength)
                hint = message + "\n\n" + Loc.F("Choisissez un mot de passe d’au moins {0} caractères.", Codex.ProfileArchive.MinimumPasswordLength);
            else if (second is not null && second.Password != first.Password) hint = message + "\n\n" + Loc.T("Les deux mots de passe diffèrent.");
            else return first.Password;
        }
    }
    private void ShowInfo(string title, string message) => new TrackerDialog(_owner, title, message, Loc.T("Fermer"), null).ShowDialog();
    private void ShowError(string message) => new TrackerDialog(_owner, Loc.T("Action indisponible"), Display.SafeText(message, _preferences.Current.PrivacyMode), Loc.T("Fermer"), null).ShowDialog();
    private async Task CheckAsync()
    {
        if (_demo) return;
        _updateBusy = true; SyncUpdateButton(); _install.Visibility = Visibility.Collapsed; _updateStatus.Text = Loc.T("Recherche en cours…");
        try
        {
            var result = await _updates.CheckDetailedAsync(_lifetime.Token);
            if (_closed) return;
            ShowCheckResult(result);
            if (_preferences.Current.DownloadUpdatesAutomatically && result.IsVerifiedNow && result.Release is { } release)
                await _updates.PrepareAsync(release, _lifetime.Token);
        }
        catch (OperationCanceledException) { if (!_closed) _updateStatus.Text = Loc.T("Recherche annulée."); }
        catch (Exception error) { if (!_closed) _updateStatus.Text = Display.SafeText(error.Message, _preferences.Current.PrivacyMode); }
        finally { _updateBusy = false; if (!_closed) SyncUpdateButton(); }
    }
    private void ShowCheckResult(UpdateCheckResult result)
    {
        _release = result.Release; _nextCheckAt = result.NextCheckAt;
        var text = result.IsVerifiedNow
            ? _release is null ? Loc.F("Aucune version plus récente sur le canal {0}.", _updates.IncludePrereleases ? Loc.T("stable + préversions") : Loc.T("stable")) : Loc.F("Version {0} disponible.", _release.Version)
            : result.Message + " " + (_release is null ? Loc.T("La version actuelle n’a pas été revérifiée.") : Loc.F("Version {0} connue dans le cache.", _release.Version));
        if (result.VerifiedAt is { } checkedAt)
            text += "\n" + (result.IsVerifiedNow ? Loc.F("Vérifié le {0:dd/MM/yyyy à HH:mm:ss}.", checkedAt.ToLocalTime()) : Loc.F("Cache vérifié le {0:dd/MM/yyyy à HH:mm:ss}.", checkedAt.ToLocalTime()));
        if (result.NextCheckAt is { } next && next > DateTimeOffset.UtcNow)
            text += "\n" + Loc.F("Nouvelle vérification possible dès le {0:dd/MM/yyyy à HH:mm:ss}.", next.ToLocalTime());
        _updateStatus.Text = text;
        _install.Visibility = _release is null ? Visibility.Collapsed : Visibility.Visible;
        SyncUpdateButton();
    }
    private void SyncUpdateButton()
    {
        _check.IsEnabled = !_demo && !_updateBusy && !_updates.IsPreparing && !(_nextCheckAt > DateTimeOffset.UtcNow);
        _install.IsEnabled = !_demo && !_updateBusy && !_updates.IsPreparing;
        _check.ToolTip = _nextCheckAt > DateTimeOffset.UtcNow ? Loc.F("Disponible à {0:HH:mm:ss}.", _nextCheckAt.Value.ToLocalTime()) : null;
        _automaticStatus.Text = !_preferences.Current.DownloadUpdatesAutomatically ? Loc.T("Recherche automatique désactivée.")
            : _automaticUpdates is null ? Loc.T("Recherche automatique indisponible dans cet exécutable.")
            : !_automaticUpdates.IsEnabled ? Loc.T("Recherche automatique en pause.")
            : _automaticUpdates.IsChecking ? _updates.IsPreparing ? Loc.T("Téléchargement automatique en cours…") : Loc.T("Recherche automatique en cours…")
            : _automaticUpdates.NextCheck <= DateTimeOffset.UtcNow ? Loc.T("Prochaine recherche automatique imminente.")
            : Loc.F("Prochaine recherche automatique : {0:dd/MM/yyyy à HH:mm:ss}.", _automaticUpdates.NextCheck.ToLocalTime());
    }
    private void CheckChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(RefreshCheck);
    private void RefreshCheck()
    {
        if (_closed || _demo || _updateBusy) return;
        if (_updates.ReadLatestCheck() is { } result) ShowCheckResult(result);
        ShowPreparation();
    }
    private void PreparationChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(ShowPreparation);
    private void ShowPreparation()
    {
        if (_closed) return;
        _preparationStatus.Text = _updates.PreparationMessage ?? "";
        _preparationStatus.Visibility = _preparationStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_updates.Prepared is { } ready)
        {
            _release = ready.Release;
            _install.Content = Loc.T("Mettre à jour et relancer");
            _install.Visibility = Visibility.Visible;
        }
        SyncUpdateButton();
    }
    private async Task InstallAsync()
    {
        if (_release is null || _demo) return;
        _updateBusy = true; SyncUpdateButton(); _install.IsEnabled = false; _updateStatus.Text = Loc.T("Téléchargement et vérification de la mise à jour…");
        try
        {
            await _updates.PrepareAsync(_release, _lifetime.Token);
            if (!_closed) await _owner.InstallReadyUpdateAsync();
        }
        catch (OperationCanceledException) { if (!_closed) _updateStatus.Text = Loc.T("Installation annulée."); }
        catch (Exception error) { if (!_closed) _updateStatus.Text = Display.SafeText(error.Message, _preferences.Current.PrivacyMode); }
        finally { _updateBusy = false; if (!_closed) SyncUpdateButton(); }
    }
}
