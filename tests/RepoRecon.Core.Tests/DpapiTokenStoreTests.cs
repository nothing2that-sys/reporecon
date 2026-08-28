using System.Text;
using RepoRecon.Core.Security;
using Xunit;

namespace RepoRecon.Core.Tests;

public class DpapiTokenStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly DpapiTokenStore _store;

    public DpapiTokenStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"reporecon-dpapi-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _store = new DpapiTokenStore(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try
            {
                Directory.Delete(_testDir, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task DpapiTokenStore_EncryptsToken_AndStoresNonPlaintextFileOnDisk()
    {
        const string plainToken = "glpat-SecretGitLabToken1234567890_XYZ";

        // Save token
        var tokenRef = await _store.SaveTokenAsync(plainToken);

        Assert.False(string.IsNullOrWhiteSpace(tokenRef));

        // Check file on disk
        var filePath = Path.Combine(_testDir, $"{tokenRef}.dat");
        Assert.True(File.Exists(filePath));

        var rawFileBytes = await File.ReadAllBytesAsync(filePath);
        var rawFileText = Encoding.UTF8.GetString(rawFileBytes);

        // Plaintext must NOT appear in encrypted file (§10 Criterion 2)
        Assert.DoesNotContain(plainToken, rawFileText);

        // Retrieve token
        var retrievedToken = await _store.GetTokenAsync(tokenRef);
        Assert.Equal(plainToken, retrievedToken);

        // Delete token
        await _store.DeleteTokenAsync(tokenRef);
        Assert.False(File.Exists(filePath));
    }
}
