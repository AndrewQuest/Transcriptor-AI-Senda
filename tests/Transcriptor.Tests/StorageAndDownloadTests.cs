using System.Security.Cryptography;
using System.Text;
using System.Net;
using Transcriptor.Core;

namespace Transcriptor.Tests;

public sealed class StorageAndDownloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "senda-test-" + Guid.NewGuid());
    private LocalStorage Store => new(_directory, bytes => bytes.Select(x => (byte)(x ^ 0xA5)).ToArray(), bytes => bytes.Select(x => (byte)(x ^ 0xA5)).ToArray());

    [Fact]
    public async Task HistoryPrunesOldRecordsAndUpdatesCorrection()
    {
        var store = Store; var id = Guid.NewGuid();
        await store.AddAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-40), "Old", 1, 1, "test"), 30);
        await store.AddAsync(new(id, DateTimeOffset.UtcNow, "Hola", 2, 1, "test"), 30);
        await store.UpdateTextAsync(id, "Hola, mundo.");
        var records = await store.ReadAsync();
        Assert.Equal("Hola, mundo.", Assert.Single(records).Text);
        Assert.DoesNotContain("Hola", await File.ReadAllTextAsync(Path.Combine(_directory, "history.dat")));
        await store.ClearAsync(); Assert.Empty(await store.ReadAsync());
    }

    [Fact]
    public async Task ConcurrentHistoryWritesDoNotLoseEntries()
    {
        var store = Store;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.AddAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, i.ToString(), 1, 1, "test"), 30)));
        Assert.Equal(20, (await store.ReadAsync()).Count);
    }

    [Fact]
    public async Task CorruptHistoryIsPreserved()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.dat"); await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => Store.AddAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "test", 1, 1, "test"), 30));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task SettingsRoundTripWithoutPersistingTranscript()
    {
        await Store.WriteSettingsAsync(new() { Language = "es", CpuThreads = 2 });
        Assert.Equal("es", (await Store.ReadSettingsAsync()).Language);
        Assert.Equal(2, (await Store.ReadSettingsAsync()).CpuThreads);
    }

    [Fact]
    public async Task LegacySettingsAdoptDarkAppearanceAndKeepVoicePreferences()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Store.SettingsPath,
            "{\"Language\":\"es\",\"ModelPath\":\"existing-model.bin\",\"CpuThreads\":3,\"SaveHistory\":false}");
        var settings = await Store.ReadSettingsAsync();
        Assert.Equal("dark", settings.Theme);
        Assert.True(settings.UseGlassEffects);
        Assert.Equal("cpu", settings.Acceleration);
        Assert.False(settings.UsePunctuationPrompt);
        Assert.False(settings.PressEnterAfterPaste);
        Assert.Equal("es", settings.InterfaceLanguage);
        await Store.WriteSettingsAsync(settings with { Theme = "light", UseGlassEffects = false, Acceleration = "vulkan", UsePunctuationPrompt = true,
            InterfaceLanguage = "en", Hotkey = "Ctrl + Shift + D", PressEnterAfterPaste = true });
        var saved = await Store.ReadSettingsAsync();
        Assert.Equal("light", saved.Theme);
        Assert.False(saved.UseGlassEffects);
        Assert.Equal("vulkan", saved.Acceleration);
        Assert.True(saved.UsePunctuationPrompt);
        Assert.True(saved.PressEnterAfterPaste);
        Assert.Equal("en", saved.InterfaceLanguage);
        Assert.Equal("Ctrl + Shift + D", saved.Hotkey);
        Assert.Equal("existing-model.bin", saved.ModelPath);
        Assert.Equal("es", saved.Language);
        Assert.Equal(3, saved.CpuThreads);
        Assert.False(saved.SaveHistory);
    }

    [Fact]
    public async Task VerifiedDownloadAtomicallyReplacesFile()
    {
        var data = Encoding.UTF8.GetBytes("trusted model fixture");
        var path = Path.Combine(_directory, "model.bin");
        await ModelDownload.SaveVerifiedAsync(new MemoryStream(data), path, Convert.ToHexString(SHA256.HashData(data)), data.Length, null, default);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HashOrSizeMismatchPreservesExistingModel(bool wrongSize)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "model.bin"); await File.WriteAllTextAsync(path, "previous model");
        var data = Encoding.UTF8.GetBytes("new model");
        var hash = wrongSize ? Convert.ToHexString(SHA256.HashData(data)) : new string('0', 64);
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelDownload.SaveVerifiedAsync(new MemoryStream(data), path, hash, data.Length + (wrongSize ? 1 : 0), null, default));
        Assert.Equal("previous model", await File.ReadAllTextAsync(path)); Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CancelledDownloadDoesNotLeavePartialFiles()
    {
        var path = Path.Combine(_directory, "model.bin");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelDownload.SaveVerifiedAsync(new MemoryStream([1, 2]), path, new string('0', 64), 2, null, cancellation.Token));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(ModelDownload.FileName)]
    [InlineData(ModelDownload.SmallFileName)]
    [InlineData(ModelDownload.TinyFileName)]
    [InlineData(ModelDownload.MediumFileName)]
    [InlineData(ModelDownload.TurboFileName)]
    public async Task DownloadUsesPinnedRevisionAndReusesVerifiedLocalArtifact(string fileName)
    {
        using var handler = new ModelHost { FileName = fileName };
        using var client = new HttpClient(handler);
        var downloader = new ModelDownload(client);
        var path = await downloader.DownloadAsync(_directory, null, default, fileName);
        Assert.Equal(fileName, Path.GetFileName(path));
        Assert.Equal(handler.Model, await File.ReadAllBytesAsync(path));
        Assert.Single(handler.Requests, x => x.Contains("/resolve/"));
        Assert.Contains("/resolve/" + ModelHost.Revision + "/", handler.Requests.Last());
        await downloader.DownloadAsync(_directory, null, default, fileName);
        Assert.Single(handler.Requests, x => x.Contains("/resolve/"));
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("whistle.cact")]
    public async Task DownloadRejectsFilesOutsideTheCatalogWithoutNetworkAccess(string fileName)
    {
        using var handler = new ModelHost();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new ModelDownload(client).DownloadAsync(_directory, null, default, fileName));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DownloadRejectsTamperedPayloadWithoutChangingModel()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, ModelDownload.FileName);
        await File.WriteAllTextAsync(path, "previous model");
        using var handler = new ModelHost { Tampered = true };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ModelDownload(client).DownloadAsync(_directory, null, default));
        Assert.Equal("previous model", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    private sealed class ModelHost : HttpMessageHandler
    {
        public const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public byte[] Model { get; } = Encoding.UTF8.GetBytes("verified bytes");
        public List<string> Requests { get; } = [];
        public bool Tampered { get; init; }
        public string FileName { get; init; } = ModelDownload.FileName;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString(); Requests.Add(uri);
            HttpContent content;
            if (uri.Contains("/tree/"))
                content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new[] { new { path = FileName,
                    size = Model.Length, lfs = new { oid = Convert.ToHexString(SHA256.HashData(Model)) } } }), Encoding.UTF8, "application/json");
            else if (uri.Contains("/resolve/")) content = new ByteArrayContent(Tampered ? Encoding.UTF8.GetBytes("tampered bytes") : Model);
            else content = new StringContent("{\"sha\":\"" + Revision + "\"}", Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
