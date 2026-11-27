using CommunityToolkit.Mvvm.Input;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class ContentProjectRowViewModel
{
    private string favoriteNote = "", favoriteNoteDraft = ""; private bool editingFavoriteNote;
    public string FavoriteNote => favoriteNote;
    public bool HasFavoriteNote => favoriteNote.Length > 0;
    public string FavoriteNoteDraft { get => favoriteNoteDraft; set => SetProperty(ref favoriteNoteDraft, value ?? ""); }
    public bool IsEditingFavoriteNote { get => editingFavoriteNote; private set => SetProperty(ref editingFavoriteNote, value); }
    public RelayCommand EditFavoriteNoteCommand { get; private set; } = null!;
    public RelayCommand CancelFavoriteNoteCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveFavoriteNoteCommand { get; private set; } = null!;
    public AsyncRelayCommand RemoveFavoriteCommand { get; private set; } = null!;
    private void InitializeFavoriteRow(DownloadsPageViewModel owner)
    {
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsSelected)) owner.FavoriteSelectionChanged(); };
        EditFavoriteNoteCommand = new(() => { FavoriteNoteDraft = FavoriteNote; IsEditingFavoriteNote = true; });
        CancelFavoriteNoteCommand = new(() => { FavoriteNoteDraft = FavoriteNote; IsEditingFavoriteNote = false; });
        SaveFavoriteNoteCommand = new(async () => { if (await owner.SaveFavoriteNoteAsync(this, FavoriteNoteDraft)) IsEditingFavoriteNote = false; });
        RemoveFavoriteCommand = new(() => owner.RemoveFavoriteRowAsync(this));
    }
    internal void SetFavoriteNote(string value) { SetProperty(ref favoriteNote, value, nameof(FavoriteNote)); OnPropertyChanged(nameof(HasFavoriteNote)); FavoriteNoteDraft = value; IsEditingFavoriteNote = false; }
}
