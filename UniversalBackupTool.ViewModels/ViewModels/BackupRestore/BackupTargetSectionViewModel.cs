using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UniversalBackupTool.ViewModel.ViewModels.BackupRestore
{
    /// <summary>
    /// One group of the Backup profile picker: the games managed by Vortex, or the other games. A backup never mixes the
    /// two, because their components differ (staging and the shared Vortex AppData only exist for Vortex games): while a
    /// game of one section is selected, the other section is locked.
    /// </summary>
    public sealed partial class BackupTargetSectionViewModel : ObservableObject
    {
        private readonly string _otherSectionName;

        public BackupTargetSectionViewModel(string title, bool usesVortex, string otherSectionName)
        {
            Title = title;
            UsesVortex = usesVortex;
            _otherSectionName = otherSectionName;
        }

        public string Title { get; }

        /// <summary>
        /// Whether the section holds the games managed by Vortex.
        /// </summary>
        public bool UsesVortex { get; }

        /// <summary>
        /// Every game of the section, sorted by name.
        /// </summary>
        public List<BackupTargetItemViewModel> Targets { get; } = [];

        /// <summary>
        /// The games matching the search and not hidden as not installed; this is what the section shows.
        /// </summary>
        public ObservableCollection<BackupTargetItemViewModel> VisibleTargets { get; } = [];

        /// <summary>
        /// True while a game of the other section is selected: the checkboxes and Select all are disabled.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary))]
        [NotifyPropertyChangedFor(nameof(IsUnlocked))]
        [NotifyCanExecuteChangedFor(nameof(ToggleSelectAllCommand))]
        private bool _isLocked;

        public bool IsUnlocked => !IsLocked;

        /// <summary>
        /// Shown while the section has games matching the search.
        /// </summary>
        public bool IsVisible => VisibleTargets.Count > 0;

        public int SelectedCount => Targets.Count(target => target.IsSelected);

        public bool HasSelection => Targets.Any(target => target.IsSelected);

        public string Summary => IsLocked
            ? $"{SelectedCount} of {ShownCount} selected · clear the {_otherSectionName} selection to pick these"
            : $"{SelectedCount} of {ShownCount} selected";

        private int ShownCount => Targets.Count(target => target.IsOnDisk);

        /// <summary>
        /// True when every listed game of the section (the matching ones while searching) is selected.
        /// </summary>
        public bool AreAllVisibleSelected => VisibleTargets.Count > 0 && VisibleTargets.All(target => target.IsSelected);

        public string SelectAllButtonText => AreAllVisibleSelected ? "Deselect all" : "Select all";

        [RelayCommand(CanExecute = nameof(IsUnlocked))]
        private void ToggleSelectAll()
        {
            // Acts on the games of this section currently listed, so with a search it only affects the matching ones.
            var select = !AreAllVisibleSelected;

            foreach (var target in VisibleTargets)
                target.IsSelected = select;
        }

        public void Refresh(string filter)
        {
            VisibleTargets.Clear();

            foreach (var target in Targets.Where(target => target.IsOnDisk && (filter.Length == 0 || target.GameName.Contains(filter, StringComparison.CurrentCultureIgnoreCase))))
                VisibleTargets.Add(target);

            OnPropertyChanged(nameof(IsVisible));
            NotifySelectionChanged();
        }

        public void SetLocked(bool locked)
        {
            IsLocked = locked;

            foreach (var target in Targets)
                target.IsSelectable = !locked;
        }

        public void NotifySelectionChanged()
        {
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(AreAllVisibleSelected));
            OnPropertyChanged(nameof(SelectAllButtonText));
        }
    }
}