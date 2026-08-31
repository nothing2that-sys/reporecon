using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoRecon.Core.Models;

namespace RepoRecon.App.ViewModels;

public partial class JobHistoryItemViewModel : ObservableObject
{
    public JobHistoryEntry Entry { get; }

    public long Id => Entry.Id;
    public string JobType => Entry.JobType;
    public string TargetPath => Entry.TargetPath;
    public string? PreHead => Entry.PreHead;
    public string? PostHead => Entry.PostHead;
    public string Result => Entry.Result;
    public string? ErrorCategory => Entry.ErrorCategory;
    public DateTime CreatedAt => Entry.CreatedAt;

    public string TimeFormatted => Entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public string HeadTransition
    {
        get
        {
            if (string.Equals(JobType, "remote_url_change", StringComparison.OrdinalIgnoreCase))
            {
                var oldU = PreHead ?? "-";
                var newU = PostHead ?? "-";
                return $"{oldU} → {newU}";
            }

            if (!string.IsNullOrEmpty(PreHead) && !string.IsNullOrEmpty(PostHead))
            {
                var preShort = PreHead.Length > 8 ? PreHead[..8] : PreHead;
                var postShort = PostHead.Length > 8 ? PostHead[..8] : PostHead;
                return $"{preShort} → {postShort}";
            }

            if (!string.IsNullOrEmpty(PreHead))
            {
                return PreHead.Length > 8 ? PreHead[..8] : PreHead;
            }

            return "-";
        }
    }

    /// <summary>
    /// 되돌리기 가능 여부 (설계 §10.3, 완료 기준 12).
    /// push/push_force 및 remote_url_change 이력에는 되돌리기 버튼을 제공하지 않습니다.
    /// </summary>
    public bool CanRollback =>
        !string.Equals(JobType, "push", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(JobType, "push_force", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(JobType, "remote_url_change", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(PreHead);

    public IAsyncRelayCommand? RollbackCommand { get; set; }

    public JobHistoryItemViewModel(
        JobHistoryEntry entry,
        Func<JobHistoryItemViewModel, Task>? onRollback = null)
    {
        Entry = entry;
        if (onRollback != null && CanRollback)
        {
            RollbackCommand = new AsyncRelayCommand(async () =>
            {
                await onRollback(this);
            });
        }
    }
}
