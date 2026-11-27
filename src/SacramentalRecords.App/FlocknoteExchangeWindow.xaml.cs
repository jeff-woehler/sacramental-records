using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SacramentalRecords.Application;
using SacramentalRecords.Domain;
using SacramentalRecords.Infrastructure;

namespace SacramentalRecords.App;

public partial class FlocknoteExchangeWindow : Window, INotifyPropertyChanged
{
    private enum Step { Choose, Scope, Match, Review, Apply }

    public event PropertyChangedEventHandler? PropertyChanged;

    private sealed record GroupOption(FlocknoteGroup? Group, string Label, Func<FlocknoteFieldChange, bool>? Filter = null);
    private sealed record ScopePerson(Person Person, string DisplayName);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private readonly RegistryService _registry;
    private readonly bool _readOnly;
    private readonly Func<Task>? _publish;
    private readonly Person? _onlyPerson;
    private Step _step = Step.Choose;
    private string? _snapshotPath;
    private FlocknoteExchange? _exchange;
    private IReadOnlyList<Person>? _people;
    private IReadOnlyList<SacramentRecord>? _sacraments;
        private List<FlocknoteFieldChange> _review = [];
        private readonly HashSet<Guid> _selectedPeople = [];
    private bool _updatingScopePeople;
    private bool _applied;


    private bool _bulk;

    public IReadOnlyList<FlocknoteOption<FlocknoteDecision>> DecisionOptions
    {
        get
        {
            // Only filter if controls are initialized; during XAML initialization, return all
            if (ImportRadio == null || ExportRadio == null)
                return FlocknoteMaps.Decisions;
            
            var import = ImportRadio.IsChecked == true;
            return import
                ? FlocknoteMaps.Decisions.Where(d => d.Value is FlocknoteDecision.UseFlocknote or FlocknoteDecision.LeaveUnchanged).ToList()
                : FlocknoteMaps.Decisions.Where(d => d.Value is FlocknoteDecision.KeepRegistry or FlocknoteDecision.LeaveUnchanged).ToList();
        }
    }

    public FlocknoteExchangeWindow(RegistryService registry, bool readOnly, Func<Task>? publish, Person? onlyPerson = null)
    {
        InitializeComponent();
        WindowControlHelper.RemoveMinimizeButton(this);
        DataContext = this;
        _registry = registry;
        _readOnly = readOnly;
        _publish = publish;
        _onlyPerson = onlyPerson;

        if (readOnly)
        {
            ImportRadio.IsEnabled = false;
            ExportRadio.IsChecked = true;
            StatusText.Text = "The package is open read-only, so only Flocknote updates can be prepared.";
        }
        if (onlyPerson is not null)
        {
            TitleText.Text = FlocknoteExchange.Describe(onlyPerson);
            StepScope.Visibility = StepMatch.Visibility = Visibility.Collapsed;
        }

        var configured = MainWindow.FileSettings.GetValueOrDefault(MainWindow.FlocknoteSnapshotSettingKey);
        SetSnapshotPath(string.IsNullOrWhiteSpace(configured) ? null : configured);
        ShowStep(Step.Choose);
    }

    private bool IsImport => ImportRadio.IsChecked == true;
    private FlocknoteDirection Direction => IsImport ? FlocknoteDirection.Import : FlocknoteDirection.Export;
    private bool IncludeUnlinked => IsImport && ImportAllRadio.IsChecked == true && _onlyPerson is null;

    private void SetSnapshotPath(string? path)
    {
        _snapshotPath = path;
        _exchange = null;
        if (path is null)
        {
            SnapshotPathText.Text = string.Empty;
            SnapshotInfoText.Text = "No Flocknote Sacrament snapshot is set in Admin settings. Use Locate file to choose the spreadsheet Flocknote provided.";
        }
        else
        {
            SnapshotPathText.Text = path;
            SnapshotInfoText.Text = File.Exists(path)
                ? $"Spreadsheet snapshot saved {File.GetLastWriteTime(path):g}. This is a copy of Flocknote data at that time, not live Flocknote data."
                : "The configured snapshot file was not found. Use Locate file to choose it.";
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Locate the Flocknote Sacrament snapshot",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            CheckFileExists = true
        };
        if (_snapshotPath is not null && Directory.Exists(Path.GetDirectoryName(_snapshotPath))) dialog.InitialDirectory = Path.GetDirectoryName(_snapshotPath);
        if (dialog.ShowDialog(this) == true) SetSnapshotPath(Path.GetFullPath(dialog.FileName));
    }

    private void Direction_Changed(object sender, RoutedEventArgs e)
    {
        OnPropertyChanged(nameof(DecisionOptions));
        if (BulkDecision == null) return;
        var options = IsImport
            ? FlocknoteMaps.Decisions.Where(d => d.Value is FlocknoteDecision.UseFlocknote or FlocknoteDecision.LeaveUnchanged).ToList()
            : FlocknoteMaps.Decisions.Where(d => d.Value is FlocknoteDecision.KeepRegistry or FlocknoteDecision.LeaveUnchanged).ToList();
        BulkDecision.ItemsSource = options;
        if (options.Count > 0) BulkDecision.SelectedIndex = 0;
    }

    private IEnumerable<Step> Steps()
    {
        yield return Step.Choose;
        if (_onlyPerson is null) yield return Step.Scope;
        if (IncludeUnlinked) yield return Step.Match;
        yield return Step.Review;
        yield return Step.Apply;
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
                if (_step == Step.Choose && !await LoadAsync()) return;
        if (_step == Step.Scope && _selectedPeople.Count == 0)
        {
            MessageBox.Show(this, "Select at least one person to continue.", "People required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var steps = Steps().ToList();

        var index = steps.IndexOf(_step);
        if (index < steps.Count - 1) ShowStep(steps[index + 1]);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        var steps = Steps().ToList();
        var index = steps.IndexOf(_step);
        if (index > 0) ShowStep(steps[index - 1]);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async Task<bool> LoadAsync()
    {
        if (_snapshotPath is null || !File.Exists(_snapshotPath))
        {
            MessageBox.Show(this, "Locate the Flocknote Sacrament snapshot first.", "Snapshot required", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (_exchange is not null) return true;

        SetBusy("Reading the Flocknote snapshot and registry…");
        try
        {
            var path = _snapshotPath;
            var snapshot = await Task.Run(() => FlocknoteSnapshot.Load(path));
            _people ??= await _registry.SearchPeopleAsync(string.Empty);
            _sacraments ??= await _registry.GetAllSacramentsAsync();
            var people = _people;
            var sacraments = _sacraments;
            var only = _onlyPerson?.RecordId;
            _exchange = await Task.Run(() => FlocknoteExchange.Build(snapshot, people, sacraments, only));

            var missing = snapshot.MissingHeaders();
            StatusText.Text = missing.Count == 0
                ? $"{snapshot.Rows.Count:N0} rows read."
                : $"{snapshot.Rows.Count:N0} rows read. Columns not found (ignored): {string.Join(", ", missing)}";
                        ScopeSummaryText.Text =
                $"The snapshot has {snapshot.Rows.Count:N0} rows: {_exchange.Linked.Count:N0} linked to registry people, " +
                $"{_exchange.Matches.Count(m => m.HasCandidates):N0} unlinked with a possible match, {_exchange.Matches.Count(m => !m.HasCandidates):N0} unlinked with no match, " +
                $"and {_exchange.Invalid.Count:N0} invalid or incomplete. Search and select the registry people to include.";
            PopulateScopePeople();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to read the snapshot", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { ClearBusy(); }
    }

    private void ShowStep(Step step)
    {
        _step = step;
        ChoosePanel.Visibility = step == Step.Choose ? Visibility.Visible : Visibility.Collapsed;
        ScopePanel.Visibility = step == Step.Scope ? Visibility.Visible : Visibility.Collapsed;
        MatchPanel.Visibility = step == Step.Match ? Visibility.Visible : Visibility.Collapsed;
        ReviewPanel.Visibility = step == Step.Review ? Visibility.Visible : Visibility.Collapsed;
        ApplyPanel.Visibility = step == Step.Apply ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (text, s) in new[] { (StepChoose, Step.Choose), (StepScope, Step.Scope), (StepMatch, Step.Match), (StepReview, Step.Review), (StepApply, Step.Apply) })
        {
            text.FontWeight = s == step ? FontWeights.Bold : FontWeights.Normal;
            text.Foreground = (Brush)FindResource(s == step ? "AccentBrush" : "MutedBrush");
        }
        StepMatch.Opacity = IncludeUnlinked || _onlyPerson is not null ? 1 : 0.4;
        StepApply.Text = IsImport ? "5. Apply import" : "5. Publish update file";
        TitleText.Text = _onlyPerson is not null ? FlocknoteExchange.Describe(_onlyPerson) : IsImport ? "Import from Flocknote" : "Prepare updates for Flocknote";

        BackButton.IsEnabled = step != Step.Choose && !_applied;
        NextButton.IsEnabled = step != Step.Apply;
        ImportScopeBox.Visibility = IsImport ? Visibility.Visible : Visibility.Collapsed;
        ExportScopeBox.Visibility = IsImport ? Visibility.Collapsed : Visibility.Visible;

        if (BulkDecision != null)
        {
            var options = IsImport
                ? FlocknoteMaps.Decisions.Where(d => d.Value is FlocknoteDecision.UseFlocknote or FlocknoteDecision.LeaveUnchanged).ToList()
                : FlocknoteMaps.Decisions.Where(d => d.Value is FlocknoteDecision.KeepRegistry or FlocknoteDecision.LeaveUnchanged).ToList();
            BulkDecision.ItemsSource = options;
            if (options.Count > 0) BulkDecision.SelectedIndex = 0;
        }
        OnPropertyChanged(nameof(DecisionOptions));

        if (step == Step.Match) ShowMatches();
        if (step == Step.Review) ShowReview();
        if (step == Step.Apply) ShowApply();
    }

        private void PopulateScopePeople() => UpdateScopePeople();

        private void ScopeSearch_TextChanged(object sender, TextChangedEventArgs e) => UpdateScopePeople();

        private void UpdateScopePeople()
        {
            if (_people is null) return;

        var search = ScopeSearchBox.Text.Trim();
        var shown = _people
            .Where(person => string.IsNullOrEmpty(search) || $"{person.GivenName} {person.MiddleName} {person.FamilyName} {person.MaidenName} {person.FlocknoteId}"
                .Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(person => person.FamilyName)
            .ThenBy(person => person.GivenName)
            .Select(person => new ScopePerson(person, $"{FlocknoteExchange.Describe(person)}{(string.IsNullOrWhiteSpace(person.FlocknoteId) ? "" : $" — Flocknote ID {person.FlocknoteId}")}"))
            .ToList();
                _updatingScopePeople = true;
        try
        {
            ScopePeopleList.ItemsSource = shown;
            foreach (var person in shown.Where(p => _selectedPeople.Contains(p.Person.RecordId))) ScopePeopleList.SelectedItems.Add(person);
        }
        finally { _updatingScopePeople = false; }
        UpdateSelectedPeopleText();
    }

    private void ScopePeople_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingScopePeople) return;
        foreach (var person in e.RemovedItems.OfType<ScopePerson>()) _selectedPeople.Remove(person.Person.RecordId);

        foreach (var person in e.AddedItems.OfType<ScopePerson>()) _selectedPeople.Add(person.Person.RecordId);
        UpdateSelectedPeopleText();
    }

    private void SelectShownPeople_Click(object sender, RoutedEventArgs e)
    {
        foreach (var person in ScopePeopleList.Items.OfType<ScopePerson>()) _selectedPeople.Add(person.Person.RecordId);
                UpdateScopePeople();

    }

    private void ClearSelectedPeople_Click(object sender, RoutedEventArgs e)
    {
        _selectedPeople.Clear();
        ScopePeopleList.SelectedItems.Clear();
        UpdateSelectedPeopleText();
    }

    private void UpdateSelectedPeopleText() => SelectedPeopleText.Text = _selectedPeople.Count == 0
        ? "No people selected."
        : $"{_selectedPeople.Count:N0} person{(_selectedPeople.Count == 1 ? string.Empty : "s")} selected.";

    private void ShowMatches()

    {
        var matches = _exchange!.Matches;
        MatchSummaryText.Text = $"{matches.Count:N0} snapshot rows are not linked. {matches.Count(m => m.HasCandidates):N0} have possible registry matches; " +
                                "confirm a link, add the person as an active member, or skip the row.";
        MatchFilter_Changed(this, new RoutedEventArgs());
    }

    private void MatchFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_exchange is null) return;
        MatchGrid.ItemsSource = MatchCandidatesOnly.IsChecked == true ? _exchange.Matches.Where(m => m.HasCandidates).ToList() : _exchange.Matches;
    }

    private void MatchCreate_Click(object sender, RoutedEventArgs e) => SetSelectedMatches(FlocknoteMatchAction.CreatePerson);
    private void MatchSkip_Click(object sender, RoutedEventArgs e) => SetSelectedMatches(FlocknoteMatchAction.Skip);

    private void SetSelectedMatches(FlocknoteMatchAction action)
    {
        foreach (var match in MatchGrid.SelectedItems.OfType<FlocknoteMatch>())
            match.Selected = match.Options.First(o => o.Action == action);
    }

    private void ShowReview()
    {
        _review = _exchange!.BuildReview(Direction, IncludeUnlinked, _onlyPerson is null ? _selectedPeople : null);
        foreach (var change in _review) change.PropertyChanged += (_, _) => { if (!_bulk) UpdateReviewSummary(); };
        var options = new List<GroupOption> { new(null, $"All groups ({_review.Count:N0})") };
        options.AddRange(Enum.GetValues<FlocknoteGroup>().Select(g => new GroupOption(g, $"{FlocknoteMaps.GroupLabel(g)} ({_review.Count(c => c.Group == g):N0})")));
        
        // Add special "Conflicts to review" filter that shows only conflicts with Flocknote values
        var conflictCount = _review.Count(c => c.Kind == FlocknoteChangeKind.Conflict && c.FlocknoteValue is not null);
        if (conflictCount > 0)
        {
            options.Add(new GroupOption(null, $"Conflicts to review ({conflictCount})", 
                c => c.Kind == FlocknoteChangeKind.Conflict && c.FlocknoteValue is not null));
        }
        
        GroupFilter.ItemsSource = options;
        GroupFilter.SelectedIndex = _review.Any(c => c.Group == FlocknoteGroup.ProposedChanges) ? 2 : 0;
        UpdateReviewSummary();
    }

    private void GroupFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupFilter.SelectedItem is not GroupOption option) return;
        
        List<FlocknoteFieldChange> filtered;
        if (option.Filter is not null)
        {
            filtered = _review.Where(option.Filter).ToList();
            // For conflicts filter, set default decision to LeaveUnchanged
            foreach (var change in filtered.Where(c => c.IsDecidable))
                change.Decision = FlocknoteDecision.LeaveUnchanged;
        }
        else
        {
            filtered = option.Group is null ? _review : _review.Where(c => c.Group == option.Group).ToList();
        }
        
        ReviewGrid.ItemsSource = filtered;
    }

    private void BulkDecision_Click(object sender, RoutedEventArgs e)
    {
        if (BulkDecision.SelectedValue is not FlocknoteDecision decision) return;
        _bulk = true;
        try
        {
            foreach (var change in ReviewGrid.SelectedItems.OfType<FlocknoteFieldChange>().Where(c => c.IsDecidable)) change.Decision = decision;
        }
        finally { _bulk = false; }
        UpdateReviewSummary();
    }

    private void UpdateReviewSummary()
    {
        var decidable = _review.Where(c => c.IsDecidable).ToList();
        ReviewSummaryText.Text = $"{decidable.Count:N0} differences: " +
            string.Join(", ", FlocknoteMaps.Decisions.Select(d => $"{decidable.Count(c => c.Decision == d.Value):N0} {d.Label.ToLowerInvariant()}"));
    }

    private void ShowApply()
    {
        var decidable = _review.Where(c => c.IsDecidable).ToList();
        if (IsImport)
        {
            var batch = _exchange!.BuildImport(_review, IncludeUnlinked, _onlyPerson is null ? _selectedPeople : null);
            ApplySummaryText.Text =
                $"Ready to import: {batch.FieldsApplied:N0} approved Flocknote values across {batch.Records.Count:N0} register entries, " +
                $"{batch.Linked:N0} people to link, and {batch.Created:N0} new people (Active member).\n\n" +
                "Changes are written to the open registry session. Use Save to OneDrive afterwards to publish them.";
            ApplyButton.Content = "Import into registry";
            ApplyButton.IsEnabled = !_readOnly && !_applied && (batch.People.Count > 0 || batch.Records.Count > 0);
        }
        else
        {
            var outbound = decidable.Count(c => c.Decision == FlocknoteDecision.KeepRegistry && c.RegistryValue is not null);
            var allPeople = ExportAllRadio.IsChecked == true;
            ApplySummaryText.Text =
                $"Ready to prepare a Flocknote update file with {outbound:N0} approved registry values" +
                (allPeople ? $" for all {_selectedPeople.Count:N0} selected people." : " (only the selected people with values to send).") +
                "\n\nYou will choose where to save a new spreadsheet. The Flocknote snapshot file is never overwritten. " +
                "Send the saved file to Flocknote support or import it in Flocknote.";
            ApplyButton.Content = "Save update file…";
            ApplyButton.IsEnabled = outbound > 0 || allPeople;
        }
        if (!_applied) ApplyResultText.Text = string.Empty;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (IsImport) await ApplyImportAsync();
        else SaveExport();
    }

    private async Task ApplyImportAsync()
    {
        if (_readOnly || _applied) return;
        var batch = _exchange!.BuildImport(_review, IncludeUnlinked, _onlyPerson is null ? _selectedPeople : null);
        if (MessageBox.Show(this, $"Import {batch.FieldsApplied:N0} values, link {batch.Linked:N0} people, and add {batch.Created:N0} people?",
                "Confirm import", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SetBusy("Importing…");
        try
        {
            await _registry.SaveBatchAsync(batch.People, batch.Records,
                $"Flocknote import from {Path.GetFileName(_snapshotPath)}: {batch.FieldsApplied} values, {batch.Records.Count} entries, {batch.Linked} linked, {batch.Created} created");
            _applied = true;
            ApplyButton.IsEnabled = false;
            BackButton.IsEnabled = false;
            PublishButton.Visibility = _publish is null ? Visibility.Collapsed : Visibility.Visible;
            ApplyResultText.Text = $"Import complete: {batch.Records.Count:N0} register entries updated or added, {batch.Linked:N0} people linked, {batch.Created:N0} people added.";
        }
        catch (RecordConflictException ex)
        {
            ApplyResultText.Text = $"Nothing was imported. {ex.Message} Close this window and start the exchange again.";
        }
        catch (Exception ex)
        {
            ApplyResultText.Text = $"Nothing was imported. {ex.Message}";
        }
        finally { ClearBusy(); }
    }

    private void SaveExport()
    {
        var directory = Path.GetDirectoryName(_snapshotPath);
        var dialog = new SaveFileDialog
        {
            Title = "Save the Flocknote update file",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = $"Flocknote Sacrament update {DateTime.Now:yyyy-MM-dd}.xlsx",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (directory is not null && Directory.Exists(directory)) dialog.InitialDirectory = directory;
        while (true)
        {
            if (dialog.ShowDialog(this) != true) return;
            if (!string.Equals(Path.GetFullPath(dialog.FileName), _snapshotPath, StringComparison.OrdinalIgnoreCase)) break;
            MessageBox.Show(this, "That is the Flocknote snapshot file. Choose a different file name; the snapshot is never overwritten.",
                "Choose another file", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        try
        {
            var count = _exchange!.WriteExport(dialog.FileName, _review, ExportAllRadio.IsChecked == true);
            ApplyResultText.Text = $"Saved {count:N0} people to {dialog.FileName}.";
            if (MessageBox.Show(this, "The update file was saved. Open it now?", "Update file saved", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to save the update file", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (_publish is null) return;
        SetBusy("Saving to OneDrive…");
        try
        {
            await _publish();
            PublishButton.IsEnabled = false;
            ApplyResultText.Text += "\nSaved to OneDrive.";
        }
        catch (Exception ex) { ApplyResultText.Text += $"\nSave to OneDrive failed: {ex.Message}"; }
        finally { ClearBusy(); }
    }

    private void SetBusy(string message)
    {
        BusyText.Text = message;
        BusyOverlay.Visibility = Visibility.Visible;
        NextButton.IsEnabled = BackButton.IsEnabled = false;
    }

    private void ClearBusy()
    {
        BusyOverlay.Visibility = Visibility.Collapsed;
        NextButton.IsEnabled = _step != Step.Apply;
        BackButton.IsEnabled = _step != Step.Choose && !_applied;
    }
}
