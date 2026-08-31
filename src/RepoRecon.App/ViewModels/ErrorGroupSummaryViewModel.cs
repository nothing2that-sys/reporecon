using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoRecon.Core.Models;

namespace RepoRecon.App.ViewModels;

public partial class ErrorGroupSummaryViewModel : ObservableObject
{
    public ErrorCategory Category { get; }

    [ObservableProperty]
    private string _categoryName = string.Empty;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private string _guideMessage = string.Empty;

    [ObservableProperty]
    private bool _canRetry;

    [ObservableProperty]
    private bool _hasDryRunPassPermissionFailNotice;

    public List<long> TargetRepoIds { get; } = new();

    public IAsyncRelayCommand? RetryCommand { get; set; }

    public ErrorGroupSummaryViewModel(
        ErrorCategory category,
        IEnumerable<long> repoIds,
        Func<ErrorCategory, IEnumerable<long>, Task>? onRetry = null,
        bool hasDryRunPassPermissionFailNotice = false)
    {
        Category = category;
        TargetRepoIds.AddRange(repoIds);
        FailedCount = TargetRepoIds.Count;
        HasDryRunPassPermissionFailNotice = hasDryRunPassPermissionFailNotice;

        CategoryName = category switch
        {
            ErrorCategory.Authentication => "인증 실패",
            ErrorCategory.Permission => "권한 부족",
            ErrorCategory.Network => "네트워크 오류",
            ErrorCategory.Timeout => "타임아웃",
            ErrorCategory.Conflict => "충돌",
            ErrorCategory.Path => "경로/로컬 파일시스템 오류",
            _ => "기타 오류"
        };

        // Criteria 5: Retry button and guide text rules (§10.2)
        switch (category)
        {
            case ErrorCategory.Authentication:
                CanRetry = true;
                GuideMessage = "자격증명 또는 API 토큰을 갱신한 후 재시도하세요.";
                break;
            case ErrorCategory.Network:
                CanRetry = true;
                GuideMessage = "일시적인 네트워크 장애일 수 있습니다. 재시도 가능합니다.";
                break;
            case ErrorCategory.Timeout:
                CanRetry = true;
                GuideMessage = "응답 대기 시간을 초과했습니다. 재시도 가능합니다.";
                break;
            case ErrorCategory.Permission:
                CanRetry = false; // NO retry button (§10.2)
                GuideMessage = hasDryRunPassPermissionFailNotice
                    ? "dry-run은 통과했으나 실행 시 권한 거부됨 (저장소 관리자에게 쓰기 권한 요청 필요)"
                    : "저장소 관리자에게 쓰기/읽기 권한을 요청하세요. (재시도 불가)";
                break;
            case ErrorCategory.Conflict:
                CanRetry = false; // NO retry button (§10.2)
                GuideMessage = "수동으로 머지 또는 충돌을 해결하세요. (재시도 불가)";
                break;
            case ErrorCategory.Path:
                CanRetry = false; // NO retry button (§10.2)
                GuideMessage = "로컬 파일 경로 길이, 파일 잠금, 디스크 공간 등을 조치하세요. (재시도 불가)";
                break;
            default:
                CanRetry = true;
                GuideMessage = "기타 미분류 오류입니다. 재시도 성공을 보장하지 않습니다.";
                break;
        }

        if (onRetry != null && CanRetry)
        {
            RetryCommand = new AsyncRelayCommand(async () =>
            {
                await onRetry(Category, TargetRepoIds);
            });
        }
    }
}
