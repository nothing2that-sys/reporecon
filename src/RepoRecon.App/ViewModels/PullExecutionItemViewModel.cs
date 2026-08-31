using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using RepoRecon.Core.Pull;

namespace RepoRecon.App.ViewModels;

public partial class PullExecutionItemViewModel : ObservableObject
{
    public long RepoId { get; }
    public string AbsPath { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBadgeColor))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeText))]
    private PullJobStatus _status;

    [ObservableProperty]
    private string? _detail;

    public string FolderName => Path.GetFileName(AbsPath.TrimEnd('\\', '/'));

    public string StatusBadgeText => Status switch
    {
        PullJobStatus.Pending => "대기",
        PullJobStatus.Running => "실행 중",
        PullJobStatus.Success => "성공",
        PullJobStatus.Failed => "실패",
        PullJobStatus.Skipped => "스킵",
        PullJobStatus.Canceled => "취소",
        _ => "기타"
    };

    public string StatusBadgeColor => Status switch
    {
        PullJobStatus.Pending => AppColors.NeutralMuted,
        PullJobStatus.Running => AppColors.Info,
        PullJobStatus.Success => AppColors.Success,
        PullJobStatus.Failed => AppColors.Error,
        PullJobStatus.Skipped => AppColors.Warning,
        PullJobStatus.Canceled => AppColors.NeutralMuted,
        _ => AppColors.NeutralMuted
    };

    public PullExecutionItemViewModel(long repoId, string absPath, PullJobStatus initialStatus, string? detail = null)
    {
        RepoId = repoId;
        AbsPath = absPath;
        Status = initialStatus;
        Detail = detail;
    }
}
