namespace RepoRecon.Core.Data;

public static class DatabasePathResolver
{
    public const string PortableMarkerFileName = "reporecon.portable";
    public const string DatabaseFileName = "index.db";

    public static string ResolveDatabasePath(string? baseAppDirectory = null)
    {
#if DEBUG
        // 검증 스크립트 전용 우회 경로 (§9.3 미문서화, §12 기록). Release 빌드에는 이 코드
        // 자체가 없어 사용자 PC에 이 이름의 환경변수가 우연히 설정돼도 영향이 없다.
        var envRoot = Environment.GetEnvironmentVariable("REPORECON_PORTABLE_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            Directory.CreateDirectory(envRoot);
            return Path.Combine(envRoot, DatabaseFileName);
        }
#endif

        var appDir = baseAppDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
        var portableMarkerPath = Path.Combine(appDir, PortableMarkerFileName);

        if (File.Exists(portableMarkerPath))
        {
            return Path.Combine(appDir, DatabaseFileName);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var targetDir = Path.Combine(localAppData, "RepoRecon");
        Directory.CreateDirectory(targetDir);

        return Path.Combine(targetDir, DatabaseFileName);
    }
}
