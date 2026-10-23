using System.IO;
using System.Security.Cryptography;
using System.Text;
using Transcriptor.Core;

namespace Transcriptor.App.Tests;

public sealed class WindowsStorageTests
{
    [Fact]
    public async Task HistoryPersistsAndReloadsWithRealWindowsUserProtection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Senda-DpapiTest-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "history.dat");
        LocalStorage Store() => new(directory,
            bytes => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser),
            bytes => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        try
        {
            var entry = new HistoryEntry(Guid.NewGuid(), DateTimeOffset.UtcNow,
                "Senda DPAPI integration test", 2, 1, "test");
            await Store().AddAsync(entry, 30);
            Assert.DoesNotContain(entry.Text, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
            Assert.Equal(entry, Assert.Single(await Store().ReadAsync()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
}
