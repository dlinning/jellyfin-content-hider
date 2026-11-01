using System;
using System.IO;
using Jellyfin.Plugin.ContentHider.Data;
using Xunit;

namespace Jellyfin.Plugin.ContentHider.Tests;

public sealed class JsonContentBlockRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storagePath;

    public JsonContentBlockRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ContentHiderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _storagePath = Path.Combine(_tempDir, "content_blocks.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void EnsureInitialized_CreatesStandaloneJsonFileOnDisk()
    {
        var repo = new JsonContentBlockRepository(_storagePath);
        repo.EnsureInitialized();

        Assert.True(File.Exists(_storagePath));
        var content = File.ReadAllText(_storagePath);
        Assert.Contains("\"version\": 1", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpsertAndRemoveBlock_PersistsToDiskAcrossInstances_AndIsolatesUsers()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var movie1 = Guid.NewGuid();
        var show1 = Guid.NewGuid();
        var now = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        var repo1 = new JsonContentBlockRepository(_storagePath);
        repo1.UpsertBlock(new ContentBlockRecord(userA, movie1, "Movie", "Inception", now));
        repo1.UpsertBlock(new ContentBlockRecord(userA, show1, "Series", "Severance", now.AddMinutes(1)));
        repo1.UpsertBlock(new ContentBlockRecord(userB, movie1, "Movie", "Inception", now));

        // Open a brand new repository instance against the same on-disk .json file.
        var repo2 = new JsonContentBlockRepository(_storagePath);
        var userABlocks = repo2.GetUserBlocks(userA);
        var userBBlocks = repo2.GetUserBlocks(userB);

        Assert.Equal(2, userABlocks.Count);
        Assert.Single(userBBlocks);
        Assert.Equal(show1, userABlocks[0].MediaId);
        Assert.Equal("Severance", userABlocks[0].MediaName);

        // Removing for userA does not affect userB.
        Assert.True(repo2.RemoveBlock(userA, movie1));
        Assert.False(repo2.RemoveBlock(userA, movie1));
        Assert.Single(repo2.GetUserBlocks(userA));
        Assert.Single(repo2.GetUserBlocks(userB));
    }

    [Fact]
    public void UpsertUserSettings_PersistsToggleAndTimerAndMagicPlaylist_AcrossRestarts()
    {
        var userId = Guid.NewGuid();
        var playlistId = Guid.NewGuid();
        var disabledUntil = new DateTime(2026, 11, 1, 2, 30, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Utc);

        var repo1 = new JsonContentBlockRepository(_storagePath);
        repo1.UpsertUserSettings(new UserBlockSettings(userId, false, disabledUntil, playlistId, now));

        var repo2 = new JsonContentBlockRepository(_storagePath);
        var loaded = repo2.GetUserSettings(userId);

        Assert.NotNull(loaded);
        Assert.False(loaded.IsBlockingEnabled);
        Assert.Equal(disabledUntil, loaded.DisabledUntilUtc);
        Assert.Equal(playlistId, loaded.MagicPlaylistId);
    }

    [Fact]
    public void DeleteAllUserData_RemovesBlocksAndSettings()
    {
        var userId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var repo = new JsonContentBlockRepository(_storagePath);
        repo.UpsertBlock(new ContentBlockRecord(userId, mediaId, "Movie", "Test Movie", now));
        repo.UpsertUserSettings(new UserBlockSettings(userId, true, null, null, now));

        Assert.Single(repo.GetUserBlocks(userId));
        Assert.NotNull(repo.GetUserSettings(userId));

        var deleted = repo.DeleteAllUserData(userId);
        Assert.Equal(2, deleted);

        Assert.Empty(repo.GetUserBlocks(userId));
        Assert.Null(repo.GetUserSettings(userId));
    }

    [Fact]
    public void DeleteMediaBlocks_RemovesAcrossAllUsers()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        var targetMedia = Guid.NewGuid();
        var otherMedia = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var repo = new JsonContentBlockRepository(_storagePath);
        repo.UpsertBlock(new ContentBlockRecord(user1, targetMedia, "Movie", "Target", now));
        repo.UpsertBlock(new ContentBlockRecord(user2, targetMedia, "Movie", "Target", now));
        repo.UpsertBlock(new ContentBlockRecord(user1, otherMedia, "Movie", "Keep", now));

        var deleted = repo.DeleteMediaBlocks(targetMedia);
        Assert.Equal(2, deleted);

        Assert.Single(repo.GetUserBlocks(user1));
        Assert.Empty(repo.GetUserBlocks(user2));
    }
}
