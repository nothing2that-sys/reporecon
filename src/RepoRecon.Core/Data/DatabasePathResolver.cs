namespace RepoRecon.Core.Data;

public static class DatabasePathResolver
{
    public const string PortableMarkerFileName = "reporecon.portable";
    public const string DatabaseFileName = "index.db";

    public static string ResolveDatabasePath(string? baseAppDirectory = null)
    {
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
