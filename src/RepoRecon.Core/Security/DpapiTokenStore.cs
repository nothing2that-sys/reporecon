using System.Runtime.InteropServices;
using System.Text;

namespace RepoRecon.Core.Security;

/// <summary>
/// Windows DPAPI(Data Protection API)를 사용하여 토큰을 사용자 계정 범위로 암호화 저장하는 저장소 (§9.2, §9.3).
/// </summary>
public class DpapiTokenStore : ISecureTokenStore
{
    private readonly string _credentialsDirectory;

    public DpapiTokenStore(string? customStorageDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(customStorageDirectory))
        {
            _credentialsDirectory = customStorageDirectory;
        }
        else
        {
#if DEBUG
            // 검증 스크립트 전용 우회 경로 (§9.3 미문서화, §12 기록). DatabasePathResolver와
            // 같은 조건으로 감싼다 — 그러지 않으면 인덱스는 정상 위치에, 자격증명만 이 변수를
            // 따라가는 불일치가 Release 빌드에서 생긴다.
            var envRoot = Environment.GetEnvironmentVariable("REPORECON_PORTABLE_ROOT");
            if (!string.IsNullOrWhiteSpace(envRoot))
            {
                _credentialsDirectory = Path.Combine(envRoot, "credentials");
            }
            else
#endif
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var portableMarker = Path.Combine(baseDir, "reporecon.portable");
                if (File.Exists(portableMarker))
                {
                    _credentialsDirectory = Path.Combine(baseDir, "credentials");
                }
                else
                {
                    _credentialsDirectory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "RepoRecon",
                        "credentials");
                }
            }
        }

        Directory.CreateDirectory(_credentialsDirectory);
    }

    public async Task<string> SaveTokenAsync(string plainToken, CancellationToken cancellationToken = default)
    {
        if (plainToken == null) throw new ArgumentNullException(nameof(plainToken));

        var tokenRef = Guid.NewGuid().ToString("N");
        var tokenBytes = Encoding.UTF8.GetBytes(plainToken);
        var encryptedBytes = ProtectData(tokenBytes);

        var filePath = Path.Combine(_credentialsDirectory, $"{tokenRef}.dat");
        await File.WriteAllBytesAsync(filePath, encryptedBytes, cancellationToken).ConfigureAwait(false);

        return tokenRef;
    }

    public async Task<string?> GetTokenAsync(string tokenRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tokenRef)) return null;

        var filePath = Path.Combine(_credentialsDirectory, $"{tokenRef}.dat");
        if (!File.Exists(filePath)) return null;

        var encryptedBytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        var decryptedBytes = UnprotectData(encryptedBytes);
        return Encoding.UTF8.GetString(decryptedBytes);
    }

    public Task DeleteTokenAsync(string tokenRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tokenRef)) return Task.CompletedTask;

        var filePath = Path.Combine(_credentialsDirectory, $"{tokenRef}.dat");
        if (File.Exists(filePath))
        {
            try
            {
                File.Delete(filePath);
            }
            catch
            {
            }
        }

        return Task.CompletedTask;
    }

    #region Win32 DPAPI P/Invoke

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        StringBuilder? ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    private static byte[] ProtectData(byte[] plainData)
    {
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();

        try
        {
            inBlob.cbData = plainData.Length;
            inBlob.pbData = Marshal.AllocHGlobal(plainData.Length);
            Marshal.Copy(plainData, 0, inBlob.pbData, plainData.Length);

            if (!CryptProtectData(ref inBlob, "RepoReconToken", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"DPAPI CryptProtectData failed with error code: {error}");
            }

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    private static byte[] UnprotectData(byte[] encryptedData)
    {
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();

        try
        {
            inBlob.cbData = encryptedData.Length;
            inBlob.pbData = Marshal.AllocHGlobal(encryptedData.Length);
            Marshal.Copy(encryptedData, 0, inBlob.pbData, encryptedData.Length);

            if (!CryptUnprotectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"DPAPI CryptUnprotectData failed with error code: {error}");
            }

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    #endregion
}
