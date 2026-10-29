using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Transcriptor.App.ViewModels;
using Transcriptor.Core;

namespace Transcriptor.App.Tests;

[CollectionDefinition("Windows UI", DisableParallelization = true)]
public sealed class WindowsUiCollection;

[Collection("Windows UI")]
public sealed class ModelSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Senda-ModelSettings-" + Guid.NewGuid().ToString("N"));
    private LocalStorage Storage => new(_directory, data => data, data => data);

    private string Model(string fileName)
    {
        var models = Path.Combine(_directory, "models"); Directory.CreateDirectory(models);
        var path = Path.Combine(models, fileName);
        using var file = File.Create(path);
        file.Write(BitConverter.GetBytes(0x67676d6c)); file.SetLength(1_000_000);
        return path;
    }

    [Fact]
    public async Task LegacySmallDefaultRestoresBothActiveModelAndDownloadPicker()
    {
        var small = Model(ModelDownload.SmallFileName); Model(ModelDownload.FileName);
        await Storage.WriteSettingsAsync(new() { ModelPath = small, SaveHistory = false });
        using var first = new MainViewModel(_directory);
        await first.InitializeAsync();
        Assert.Equal(small, first.Settings.ModelPath);
        Assert.Equal(ModelDownload.SmallFileName, first.DownloadModelFile);
        Assert.Equal(small, first.SelectedInstalledModel?.Path);
        Assert.False(first.DeleteModelCommand.CanExecute(null));
        Assert.False(first.UseModelCommand.CanExecute(null));
        Assert.Equal(2, first.InstalledModels.Count);
    }

    [Fact]
    public async Task SwitchingAnInstalledModelPersistsWithoutDownloadingOrLosingOtherSettings()
    {
        var basePath = Model(ModelDownload.FileName); var small = Model(ModelDownload.SmallFileName);
        await Storage.WriteSettingsAsync(new() { ModelPath = basePath, SaveHistory = false, Hotkey = "Ctrl + Shift + D",
            Language = "en", Acceleration = "vulkan", PressEnterAfterPaste = true });
        using var first = new MainViewModel(_directory);
        await first.InitializeAsync();
        var released = false; first.ReleaseModelAsync = () => { released = true; return Task.CompletedTask; };
        await first.ActivateModelAsync(small);
        Assert.True(released);
        using var restarted = new MainViewModel(_directory);
        await restarted.InitializeAsync();
        Assert.Equal(small, restarted.Settings.ModelPath);
        Assert.Equal(ModelDownload.SmallFileName, restarted.DownloadModelFile);
        Assert.Equal("Ctrl + Shift + D", restarted.Settings.Hotkey);
        Assert.Equal("vulkan", restarted.Settings.Acceleration);
        Assert.True(restarted.Settings.PressEnterAfterPaste);
        Assert.True(File.Exists(basePath)); Assert.True(File.Exists(small));
    }

    [Fact]
    public async Task SavingOtherSettingsKeepsTheDefaultAndPersistsDownloadChoiceSeparately()
    {
        var small = Model(ModelDownload.SmallFileName);
        await Storage.WriteSettingsAsync(new() { ModelPath = small, SaveHistory = false });
        using var viewModel = new MainViewModel(_directory, _ => { });
        await viewModel.InitializeAsync();
        viewModel.DownloadModelFile = ModelDownload.TurboFileName;
        viewModel.Theme = "light";
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SettingsChanged += _ => saved.TrySetResult();
        viewModel.SaveSettingsCommand.Execute(null);
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var restarted = new MainViewModel(_directory);
        await restarted.InitializeAsync();
        Assert.Equal(small, restarted.Settings.ModelPath);
        Assert.Equal(ModelDownload.TurboFileName, restarted.DownloadModelFile);
        Assert.Equal("light", restarted.Settings.Theme);
    }

    [Fact]
    public async Task CannotSwitchModelsDuringDictationOrToAnUninstalledFile()
    {
        var path = Model(ModelDownload.FileName);
        using var viewModel = new MainViewModel(_directory);
        await viewModel.InitializeAsync(); viewModel.Busy = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.ActivateModelAsync(path));
        viewModel.Busy = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.ActivateModelAsync(Path.Combine(_directory, "missing.bin")));
    }

    [Fact]
    public async Task DownloadingTurboSelectsItImmediatelyAndKeepsItAcrossMultipleRestarts()
    {
        var small = Model(ModelDownload.SmallFileName);
        await Storage.WriteSettingsAsync(new() { ModelPath = small, SaveHistory = false });
        using var http = new HttpClient(new TurboHost());
        using (var viewModel = new MainViewModel(_directory, _ => { }, http))
        {
            await viewModel.InitializeAsync();
            Assert.Equal(small, viewModel.SelectedInstalledModel?.Path);
            viewModel.DownloadModelFile = ModelDownload.TurboFileName;
            await viewModel.DownloadSelectedModelAsync();
            Assert.Equal("Whisper Large v3 Turbo · OpenAI", viewModel.ModelName);
            Assert.True(viewModel.SelectedInstalledModel?.IsDefault);
            Assert.Equal(viewModel.Settings.ModelPath, viewModel.SelectedInstalledModel?.Path);
        }
        for (var i = 0; i < 3; i++)
        {
            using var restarted = new MainViewModel(_directory);
            await restarted.InitializeAsync();
            Assert.Equal(ModelDownload.TurboFileName, Path.GetFileName(restarted.Settings.ModelPath));
            Assert.Equal("Whisper Large v3 Turbo · OpenAI", restarted.ModelName);
            Assert.Equal(restarted.Settings.ModelPath, restarted.SelectedInstalledModel?.Path);
            Assert.Equal(2, restarted.InstalledModels.Count);
            Assert.True(restarted.SelectedInstalledModel?.IsDefault);
        }
    }

    [Fact]
    public async Task RefreshRecoversSavedTurboInsteadOfOverwritingItWithStaleSmall()
    {
        var small = Model(ModelDownload.SmallFileName); var turbo = Model(ModelDownload.TurboFileName);
        await Storage.WriteSettingsAsync(new() { ModelPath = small, SaveHistory = false });
        using var viewModel = new MainViewModel(_directory, _ => { });
        await viewModel.InitializeAsync();
        await Storage.WriteSettingsAsync(new() { ModelPath = turbo, DownloadModelFile = ModelDownload.TurboFileName, SaveHistory = false });
        await viewModel.RefreshModelStateAsync();
        Assert.Equal(turbo, viewModel.Settings.ModelPath);
        Assert.Equal(turbo, viewModel.SelectedInstalledModel?.Path);
        Assert.Equal("Whisper Large v3 Turbo · OpenAI", viewModel.ModelName);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SettingsChanged += _ => saved.TrySetResult();
        viewModel.SaveSettingsCommand.Execute(null);
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(turbo, (await Storage.ReadSettingsAsync()).ModelPath);
    }

    [Fact]
    public async Task MissingDefaultIsShownExplicitlyAndNeverFallsBackToSmall()
    {
        Model(ModelDownload.SmallFileName);
        var turbo = Path.Combine(_directory, "models", ModelDownload.TurboFileName);
        await Storage.WriteSettingsAsync(new() { ModelPath = turbo, SaveHistory = false });
        using var viewModel = new MainViewModel(_directory);
        await viewModel.InitializeAsync();
        Assert.Equal(turbo, viewModel.Settings.ModelPath);
        Assert.Equal(turbo, viewModel.SelectedInstalledModel?.Path);
        Assert.False(viewModel.ModelReady);
        Assert.Contains("Whisper Large v3 Turbo", viewModel.ModelName);
        Assert.True(viewModel.SelectedInstalledModel?.IsDefault);
    }

    [Fact]
    public async Task DeletedBaseRemainsInTheDownloadCatalogAndRefreshKeepsPendingDownloadChoice()
    {
        var small = Model(ModelDownload.SmallFileName);
        await Storage.WriteSettingsAsync(new() { ModelPath = small, SaveHistory = false });
        using var viewModel = new MainViewModel(_directory);
        await viewModel.InitializeAsync();
        viewModel.DownloadModelFile = ModelDownload.FileName;
        await viewModel.RefreshModelStateAsync();
        Assert.Equal(ModelDownload.FileName, viewModel.DownloadModelFile);
        Assert.Contains(viewModel.DownloadModels, model => model.Value == ModelDownload.FileName && model.Label.Contains("Whisper Base · OpenAI"));
        Assert.DoesNotContain(viewModel.InstalledModels, model => Path.GetFileName(model.Path) == ModelDownload.FileName);
        Assert.Equal(small, viewModel.Settings.ModelPath);
    }

    private sealed class TurboHost : HttpMessageHandler
    {
        private readonly byte[] _model = CreateModel();
        private static byte[] CreateModel()
        {
            var bytes = new byte[1_000_000];
            BitConverter.GetBytes(0x67676d6c).CopyTo(bytes, 0);
            BitConverter.GetBytes(51866).CopyTo(bytes, 4);
            return bytes;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpContent content = path.Contains("/resolve/") ? new ByteArrayContent(_model) : new StringContent(
                path.Contains("/tree/") ? JsonSerializer.Serialize(new[] { new { path = ModelDownload.TurboFileName, size = _model.Length,
                    lfs = new { oid = Convert.ToHexString(SHA256.HashData(_model)).ToLowerInvariant() } } }) :
                    JsonSerializer.Serialize(new { sha = new string('a', 40) }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
