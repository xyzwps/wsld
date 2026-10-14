using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;
using WslcDesktop.Core.WslcCli;
using static WslcDesktop.Core.I18n.Loc;

namespace WslcDesktop.Core.ViewModels;

public sealed partial class ContainerDetailViewModel : ObservableObject
{
    private readonly ContainersService _service;
    private readonly IUIThread? _uiThread;

    public ContainerDetailViewModel(ContainersService service, IUIThread? uiThread = null)
    {
        _service = service;
        _uiThread = uiThread;
    }

    [ObservableProperty] private string _containerId = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _image = "";
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _runningFor = "";
    [ObservableProperty] private string _createdAt = "";
    [ObservableProperty] private string _ports = "";
    [ObservableProperty] private string _networks = "";
    [ObservableProperty] private string _labels = "";
    [ObservableProperty] private string _size = "";
    [ObservableProperty] private string _healthStatus = "";
    [ObservableProperty] private string _platform = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;

    partial void OnErrorChanged(string? value) => HasError = !string.IsNullOrEmpty(value);

    public bool CanStart => !IsBusy && !IsRunning;
    public bool CanStop => !IsBusy && IsRunning;
    public bool CanRestart => !IsBusy;
    public bool CanRemove => !IsBusy;

    public AsyncRelayCommand StartCommand { get; private set; } = null!;
    public AsyncRelayCommand StopCommand { get; private set; } = null!;
    public AsyncRelayCommand RestartCommand { get; private set; } = null!;
    public AsyncRelayCommand RemoveCommand { get; private set; } = null!;

    public event EventHandler? Removed;
    public event EventHandler? RefreshRequested;

    public void InitializeCommands(Func<ContainerAction, Task> executeAction)
    {
        StartCommand = new AsyncRelayCommand(() => ExecuteAsync(ContainerAction.Start));
        StopCommand = new AsyncRelayCommand(() => ExecuteAsync(ContainerAction.Stop));
        RestartCommand = new AsyncRelayCommand(() => ExecuteAsync(ContainerAction.Restart));
        RemoveCommand = new AsyncRelayCommand(() => ExecuteRemoveAsync());
    }

    public async Task LoadAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ContainerId = containerId;
        try
        {
            var containers = await _service.ListAsync(cancellationToken).ConfigureAwait(false);
            var container = containers.FirstOrDefault(c => c.Id == containerId);
            if (container is null)
            {
                await RunOnUiAsync(() => Error = T("container-detail.not-found")).ConfigureAwait(false);
                return;
            }
            await RunOnUiAsync(() => ApplyModel(container)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await RunOnUiAsync(() => Error = WslcError.Describe(ex)).ConfigureAwait(false);
        }
    }

    public void ApplyModel(ContainerInfo c)
    {
        DisplayName = c.DisplayName;
        Id = c.Id;
        Image = c.Image;
        Command = c.Command;
        Status = c.Status;
        State = c.State;
        RunningFor = c.RunningFor;
        CreatedAt = c.CreatedAt;
        Ports = string.IsNullOrWhiteSpace(c.Ports) ? "—" : c.Ports;
        Networks = string.IsNullOrWhiteSpace(c.Networks) ? "—" : c.Networks;
        Labels = string.IsNullOrWhiteSpace(c.Labels) ? "—" : c.Labels;
        Size = string.IsNullOrWhiteSpace(c.Size) ? "—" : c.Size;
        HealthStatus = string.IsNullOrWhiteSpace(c.HealthStatus) ? "—" : c.HealthStatus;
        Platform = c.Platform is { } p && (!string.IsNullOrEmpty(p.OS) || !string.IsNullOrEmpty(p.Architecture))
            ? $"{p.OS} / {p.Architecture}"
            : "—";
        IsRunning = c.IsRunning;
        Error = null;
        RaiseActionStates();
    }

    private async Task ExecuteAsync(ContainerAction action)
    {
        IsBusy = true;
        RaiseActionStates();
        try
        {
            await _service.RunAsync(action, ContainerId, force: false).ConfigureAwait(false);
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await RunOnUiAsync(() => Error = WslcError.Describe(ex)).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
            RaiseActionStates();
        }
    }

    private async Task ExecuteRemoveAsync()
    {
        IsBusy = true;
        RaiseActionStates();
        try
        {
            await _service.RemoveAsync(ContainerId, force: IsRunning).ConfigureAwait(false);
            Removed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await RunOnUiAsync(() => Error = WslcError.Describe(ex)).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
            RaiseActionStates();
        }
    }

    private void RaiseActionStates()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(CanRemove));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    private Task RunOnUiAsync(Action action)
    {
        if (_uiThread is null) { action(); return Task.CompletedTask; }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiThread.Post(() => { try { action(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } });
        return tcs.Task;
    }
}
