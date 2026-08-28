using CommunityToolkit.Mvvm.ComponentModel;
using RepoRecon.Core.Models;

namespace RepoRecon.App.ViewModels;

public partial class ScanRootItemViewModel : ObservableObject
{
    private readonly Action<ScanRootItemViewModel>? _onChanged;

    [ObservableProperty]
    private long _id;

    [ObservableProperty]
    private string _path;

    [ObservableProperty]
    private int _maxDepth;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private DateTime? _lastScannedAt;

    [ObservableProperty]
    private bool _isOffline;

    public ScanRootItemViewModel(ScanRoot model, Action<ScanRootItemViewModel>? onChanged = null)
    {
        _id = model.Id;
        _path = model.Path;
        _maxDepth = model.MaxDepth;
        _isActive = model.IsActive;
        _lastScannedAt = model.LastScannedAt;
        _isOffline = model.IsOffline;
        _onChanged = onChanged;
    }

    partial void OnIsActiveChanged(bool value)
    {
        _onChanged?.Invoke(this);
    }

    public ScanRoot ToModel() => new(Id, Path, MaxDepth, IsActive, LastScannedAt, IsOffline);
}
