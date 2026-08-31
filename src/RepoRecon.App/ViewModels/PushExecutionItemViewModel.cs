using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RepoRecon.App.ViewModels;

public enum PushItemUiStatus
{
    Pending,
    Running,
    Success,
    Failed,
    Skipped,
    Canceled
}

public partial class PushExecutionItemViewModel : ObservableObject
{
    public long RepoId { get; }
    public string AbsPath { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBadgeColor))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeText))]
    private PushItemUiStatus _status;

    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    private string? _dirtyWarning;

    public string FolderName => Path.GetFileName(AbsPath.TrimEnd('\\', '/'));

    public string StatusBadgeText => Status switch
    {
        PushItemUiStatus.Pending => "대기",
        PushItemUiStatus.Running => "Push 중",
        PushItemUiStatus.Success => "성공",
        PushItemUiStatus.Failed => "실패",
        PushItemUiStatus.Skipped => "스킵",
        PushItemUiStatus.Canceled => "취소",
        _ => "기타"
    };

    public string StatusBadgeColor => Status switch
    {
        PushItemUiStatus.Pending => AppColors.NeutralMuted,
        PushItemUiStatus.Running => AppColors.Info,
        PushItemUiStatus.Success => AppColors.Success,
        PushItemUiStatus.Failed => AppColors.Error,
        PushItemUiStatus.Skipped => AppColors.Warning,
        PushItemUiStatus.Canceled => AppColors.NeutralMuted,
        _ => AppColors.NeutralMuted
    };

    public PushExecutionItemViewModel(long repoId, string absPath, PushItemUiStatus initialStatus, string? detail = null, string? dirtyWarning = null)
    {
        RepoId = repoId;
        AbsPath = absPath;
        Status = initialStatus;
        Detail = detail;
        DirtyWarning = dirtyWarning;
    }
}
