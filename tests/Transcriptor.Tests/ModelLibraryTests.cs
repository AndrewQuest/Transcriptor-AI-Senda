using Transcriptor.Core;

namespace Transcriptor.Tests;

public sealed class ModelLibraryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Senda-Models-" + Guid.NewGuid().ToString("N"));

    private string Model(string name)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        using var stream = File.Create(path);
        stream.Write(BitConverter.GetBytes(0x67676d6c)); stream.SetLength(1_000_000);
        return path;
    }

    [Fact]
    public void ListsOnlyCompleteGgmlModelsAndKeepsEveryInstalledChoice()
    {
        var first = Model(ModelDownload.FileName); var second = Model(ModelDownload.SmallFileName);
        Model("unfinished.bin.partial"); File.WriteAllText(Path.Combine(_directory, "invalid.bin"), "not a model");
        var models = new ModelLibrary(_directory).Read();
        Assert.Equal(2, models.Count);
        Assert.Contains(models, model => model.Path == first);
        Assert.Contains(models, model => model.Path == second);
    }

    [Fact]
    public void DeletingAnUnusedModelPreservesTheDefaultAndUnrelatedFiles()
    {
        var active = Model(ModelDownload.SmallFileName); var unused = Model(ModelDownload.FileName);
        var other = Path.Combine(_directory, "notes.txt"); File.WriteAllText(other, "keep");
        new ModelLibrary(_directory).Delete(unused, active);
        Assert.True(File.Exists(active)); Assert.True(File.Exists(other)); Assert.False(File.Exists(unused));
    }

    [Fact]
    public void CannotDeleteTheDefaultThroughEquivalentPaths()
    {
        var active = Model(ModelDownload.SmallFileName);
        Assert.Throws<InvalidOperationException>(() => new ModelLibrary(_directory).Delete(
            Path.Combine(_directory, ".", ModelDownload.SmallFileName), active));
        Assert.True(File.Exists(active));
    }

    [Fact]
    public void CannotDeleteAFileOutsideTheManagedDirectory()
    {
        var active = Model(ModelDownload.FileName);
        Assert.Throws<InvalidOperationException>(() => new ModelLibrary(_directory).Delete(
            Path.Combine(_directory, "..", "outside.bin"), active));
        Assert.True(File.Exists(active));
    }

    [Fact]
    public void ModelInUseDoesNotDisappearFromTheInventory()
    {
        var turbo = Model(ModelDownload.TurboFileName);
        using var nativeHandle = File.Open(turbo, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.Contains(new ModelLibrary(_directory).Read(), model => model.Path == turbo);
    }

    [Theory]
    [InlineData(ModelDownload.TinyFileName, "Whisper Tiny · OpenAI")]
    [InlineData(ModelDownload.FileName, "Whisper Base · OpenAI")]
    [InlineData(ModelDownload.SmallFileName, "Whisper Small · OpenAI")]
    [InlineData(ModelDownload.MediumFileName, "Whisper Medium · OpenAI")]
    [InlineData(ModelDownload.TurboFileName, "Whisper Large v3 Turbo · OpenAI")]
    public void CatalogModelsUseRecognizableNames(string file, string expected)
    {
        Assert.Equal(expected, ModelDownload.DisplayName(file));
        Assert.StartsWith(expected, ModelDownload.DownloadLabel(file));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
