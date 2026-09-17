using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.Core.Adapters;
using RepoRecon.Core.Data;
using RepoRecon.Core.Models;
using RepoRecon.Core.Security;
using RepoRecon.Core.Urls;

namespace RepoRecon.App.ViewModels;

public partial class ProfileManagementViewModel : ObservableObject
{
    private readonly SqliteIndexStore _indexStore;
    private readonly ISecureTokenStore _tokenStore;
    private readonly IServerAdapterFactory _adapterFactory;
    private readonly HostAliasTable _aliasTable;
    private readonly ILogger<ProfileManagementViewModel> _logger;

    public ObservableCollection<ServerProfile> Profiles { get; } = new();

    public Array AvailableApiKinds => Enum.GetValues(typeof(ServerApiKind));

    [ObservableProperty]
    private ServerProfile? _selectedProfile;

    [ObservableProperty]
    private string _editHost = string.Empty;

    [ObservableProperty]
    private ServerApiKind _editApiKind = ServerApiKind.Gitea;

    [ObservableProperty]
    private string _editAliases = string.Empty;

    [ObservableProperty]
    private string _editToken = string.Empty;

    [ObservableProperty]
    private string _tokenStatusText = "토큰을 입력하세요";

    [ObservableProperty]
    private string? _editApiVersion;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private bool _isTestSuccess;

    [ObservableProperty]
    private string _testResultMessage = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public Func<Task>? OnAliasesInvalidatedFunc { get; set; }

    public ProfileManagementViewModel(
        SqliteIndexStore indexStore,
        ISecureTokenStore tokenStore,
        IServerAdapterFactory adapterFactory,
        HostAliasTable aliasTable,
        ILogger<ProfileManagementViewModel>? logger = null)
    {
        _indexStore = indexStore;
        _tokenStore = tokenStore;
        _adapterFactory = adapterFactory;
        _aliasTable = aliasTable;
        _logger = logger ?? NullLogger<ProfileManagementViewModel>.Instance;
    }

    public async Task InitializeAsync()
    {
        await LoadProfilesAsync();
    }

    [RelayCommand]
    public async Task LoadProfilesAsync()
    {
        Profiles.Clear();
        var list = await _indexStore.GetServerProfilesAsync();
        foreach (var p in list)
        {
            Profiles.Add(p);
        }

        _aliasTable.ReloadFromProfiles(list);
    }

    partial void OnSelectedProfileChanged(ServerProfile? value)
    {
        if (value != null)
        {
            EditHost = value.Host;
            EditApiKind = value.ApiKind;
            EditAliases = string.Join(", ", value.Aliases);
            EditApiVersion = value.ApiVersion;
            EditToken = string.Empty;
            TokenStatusText = "기존 토큰이 등록되어 있습니다 (변경 시에만 새로 입력)";
            TestResultMessage = string.Empty;
        }
        else
        {
            ResetForm();
        }
    }

    [RelayCommand]
    public void ResetForm()
    {
        SelectedProfile = null;
        EditHost = string.Empty;
        EditApiKind = ServerApiKind.Gitea;
        EditAliases = string.Empty;
        EditToken = string.Empty;
        EditApiVersion = null;
        TokenStatusText = "토큰을 입력하세요 (평문 저장 방지: Windows DPAPI 암호화)";
        TestResultMessage = string.Empty;
    }

    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(EditHost))
        {
            TestResultMessage = "호스트 주소를 입력하세요.";
            IsTestSuccess = false;
            return;
        }

        IsTesting = true;
        TestResultMessage = "연결 테스트 중...";

        try
        {
            var tokenToUse = EditToken;
            if (string.IsNullOrWhiteSpace(tokenToUse) && SelectedProfile != null && !string.IsNullOrWhiteSpace(SelectedProfile.TokenRef))
            {
                tokenToUse = await _tokenStore.GetTokenAsync(SelectedProfile.TokenRef);
            }

            var adapter = _adapterFactory.GetAdapter(EditApiKind);
            var result = await adapter.TestConnectionAsync(EditHost, tokenToUse);

            IsTestSuccess = result.IsSuccess;
            if (result.IsSuccess)
            {
                EditApiVersion = result.ApiVersion;
                TestResultMessage = $"✅ 연결 성공! (API 버전: {result.ApiVersion ?? "확인됨"})";
            }
            else
            {
                TestResultMessage = $"❌ 연결 실패: [{result.ErrorCategory ?? "Other"}] {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            IsTestSuccess = false;
            TestResultMessage = $"❌ 오류: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    public async Task SaveProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(EditHost))
        {
            StatusMessage = "호스트 주소는 필수입니다.";
            return;
        }

        try
        {
            string tokenRef;

            if (!string.IsNullOrWhiteSpace(EditToken))
            {
                // Encrypt token via DPAPI
                tokenRef = await _tokenStore.SaveTokenAsync(EditToken);
                _logger.LogInformation("Saved token for {Host}, tokenRef={TokenRef}, tokenLength={Len}", EditHost, tokenRef, EditToken.Length);
                // Clear plain token from property
                EditToken = string.Empty;
            }
            else if (SelectedProfile != null && !string.IsNullOrWhiteSpace(SelectedProfile.TokenRef))
            {
                tokenRef = SelectedProfile.TokenRef;
                _logger.LogInformation("Reusing existing tokenRef={TokenRef} for {Host}", tokenRef, EditHost);
            }
            else
            {
                // Empty token ref
                tokenRef = string.Empty;
                _logger.LogWarning("No token provided for {Host}, tokenRef is empty", EditHost);
            }

            var aliasesList = EditAliases.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Select(a => a.Trim())
                                         .Distinct(StringComparer.OrdinalIgnoreCase)
                                         .ToList();

            var previousAliases = SelectedProfile?.Aliases ?? new List<string>();

            // This window is non-modal (MainViewModel opens it with Show(), not ShowDialog()),
            // so it can sit open while a catalog sync elsewhere auto-registers a new alias on
            // this same profile (§4.2 (4)). EditAliases only reflects the snapshot taken when
            // the profile was selected, so saving as-is would silently erase that alias. Fold in
            // anything that appeared on disk since load; aliases the user actually deleted from
            // the field (present at load, absent now) are still respected.
            if (SelectedProfile != null)
            {
                var currentOnDisk = (await _indexStore.GetServerProfilesAsync())
                    .FirstOrDefault(p => p.Id == SelectedProfile.Id);
                var addedElsewhere = (currentOnDisk?.Aliases ?? new List<string>())
                    .Except(previousAliases, StringComparer.OrdinalIgnoreCase);
                aliasesList = aliasesList.Concat(addedElsewhere)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var profileToSave = new ServerProfile(
                Id: SelectedProfile?.Id ?? 0,
                Host: EditHost.Trim(),
                ApiKind: EditApiKind,
                Aliases: aliasesList,
                TokenRef: tokenRef,
                ApiVersion: EditApiVersion,
                CreatedAt: SelectedProfile?.CreatedAt ?? DateTime.UtcNow,
                LastSyncedAt: SelectedProfile?.LastSyncedAt);

            var savedId = await _indexStore.UpsertServerProfileAsync(profileToSave);
            StatusMessage = $"프로파일 '{EditHost}'이(가) 저장되었습니다.";

            // §2.3/§4.4: an added/changed alias means some local_repos rows
            // are now keyed under the wrong (pre-alias) authority; a removed
            // alias means we can no longer tell which rows it used to cover
            // (they already read as the canonical authority), so invalidate
            // everything already resolved to this profile's canonical host.
            var addedAliases = aliasesList.Except(previousAliases, StringComparer.OrdinalIgnoreCase).ToList();
            var removedAliases = previousAliases.Except(aliasesList, StringComparer.OrdinalIgnoreCase).ToList();

            var invalidatedCount = 0;
            foreach (var addedAlias in addedAliases)
            {
                invalidatedCount += await _indexStore.InvalidateNormalizedKeysForAliasAsync(addedAlias);
            }

            if (removedAliases.Count > 0)
            {
                var canonicalAuthority = RemoteUrlNormalizer.ExtractAuthority(profileToSave.Host);
                if (canonicalAuthority != null)
                {
                    invalidatedCount += await _indexStore.InvalidateNormalizedKeysForAliasAsync(canonicalAuthority);
                }
            }

            if (invalidatedCount > 0)
            {
                _logger.LogInformation(
                    "Invalidated {Count} local_repos.normalized_key rows after alias change on profile {Host} (added={Added}, removed={Removed})",
                    invalidatedCount, profileToSave.Host, addedAliases.Count, removedAliases.Count);
            }

            await LoadProfilesAsync();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == savedId);

            if (invalidatedCount > 0 && OnAliasesInvalidatedFunc != null)
            {
                await OnAliasesInvalidatedFunc();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save server profile {Host}", EditHost);
            StatusMessage = $"저장 실패: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task DeleteProfileAsync()
    {
        if (SelectedProfile == null) return;

        try
        {
            var profileId = SelectedProfile.Id;
            var tokenRef = SelectedProfile.TokenRef;
            var canonicalAuthority = RemoteUrlNormalizer.ExtractAuthority(SelectedProfile.Host);

            await _indexStore.DeleteServerProfileAsync(profileId);
            if (!string.IsNullOrWhiteSpace(tokenRef))
            {
                await _tokenStore.DeleteTokenAsync(tokenRef);
            }

            // §2.3/§4.4: same treatment as removing all of this profile's
            // aliases -- invalidate whatever is already resolved to its
            // canonical authority so a stale key isn't left unrecoverable.
            var invalidatedCount = 0;
            if (canonicalAuthority != null)
            {
                invalidatedCount = await _indexStore.InvalidateNormalizedKeysForAliasAsync(canonicalAuthority);
                if (invalidatedCount > 0)
                {
                    _logger.LogInformation(
                        "Invalidated {Count} local_repos.normalized_key rows after deleting profile {Host}",
                        invalidatedCount, SelectedProfile.Host);
                }
            }

            StatusMessage = "프로파일이 삭제되었습니다.";
            ResetForm();
            await LoadProfilesAsync();

            if (invalidatedCount > 0 && OnAliasesInvalidatedFunc != null)
            {
                await OnAliasesInvalidatedFunc();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete server profile");
            StatusMessage = $"삭제 실패: {ex.Message}";
        }
    }
}
