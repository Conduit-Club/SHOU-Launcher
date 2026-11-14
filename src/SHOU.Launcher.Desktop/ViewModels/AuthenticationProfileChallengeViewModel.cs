using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

/// <summary>A single nonsecret role-selection challenge. Commands cannot complete a later challenge.</summary>
public sealed class AuthenticationProfileChallengeViewModel : ObservableObject
{
    private readonly TaskCompletionSource<string?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<bool> _isCurrent;
    private AuthenticationProfileChoice? _selectedChoice;
    public IReadOnlyList<AuthenticationProfileChoice> Choices { get; }
    public AuthenticationProfileChoice? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (!IsPending || !_isCurrent()) return;
            if (value is not null && !Choices.Any(c => c.Id == value.Id)) return;
            if (SetProperty(ref _selectedChoice, value)) ConfirmCommand.NotifyCanExecuteChanged();
        }
    }
    public bool IsPending => !_completion.Task.IsCompleted;
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }
    internal Task<string?> Completion => _completion.Task;

    internal AuthenticationProfileChallengeViewModel(IReadOnlyList<AuthenticationProfileChoice> choices,
        Func<bool> isCurrent, Action cancelAuthentication)
    {
        Choices = choices.ToArray();
        _isCurrent = isCurrent;
        ConfirmCommand = new(() =>
        {
            if (!CanConfirm()) return;
            _completion.TrySetResult(SelectedChoice!.Id);
            NotifyCompleted();
        }, CanConfirm);
        CancelCommand = new(() => { if (IsPending && _isCurrent()) cancelAuthentication(); }, () => IsPending && _isCurrent());
    }
    private bool CanConfirm() => IsPending && _isCurrent() && SelectedChoice is { } choice && Choices.Any(c => c.Id == choice.Id);
    internal void Cancel(CancellationToken token)
    {
        _completion.TrySetCanceled(token.IsCancellationRequested ? token : new CancellationToken(true));
    }
    internal void NotifyCompleted()
    {
        OnPropertyChanged(nameof(IsPending));
        ConfirmCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
}
