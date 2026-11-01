using System;
using System.IO;
using Jellyfin.Plugin.ContentHider.Data;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Xunit;

namespace Jellyfin.Plugin.ContentHider.Tests;

public sealed class SqliteContentBlockRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    static SqliteContentBlockRepositoryTests()
    {
        Batteries_V2.Init();
    }

    public SqliteContentBlockRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ContentHiderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "content_blocks.sqlite");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void EnsureInitialized_CreatesStandaloneSqliteFileOnDisk_WithoutWalSidecar()
    {
        var repo = new SqliteContentBlockRepository(_dbPath);
        repo.EnsureInitialized();

        Assert.True(File.Exists(_dbPath));
        Assert.False(File.Exists(_dbPath + "-wal"));

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = cmd.ExecuteScalar()?.ToString();
        Assert.Equal("delete", mode, ignoreCase: true);
    }

    [Fact]
    public void UpsertAndRemoveBlock_PersistsToDiskAcrossInstances_AndIsolatesUsers()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var movie1 = Guid.NewGuid();
        var show1 = Guid.NewGuid();
        var now = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        var repo1 = new SqliteContentBlockRepository(_dbPath);
        repo1.UpsertBlock(new ContentBlockRecord(userA, movie1, "Movie", "Inception", now));
        repo1.UpsertBlock(new ContentBlockRecord(userA, show1, "Series", "Severance", now.AddMinutes(1)));
        repo1.UpsertBlock(new ContentBlockRecord(userB, movie1, "Movie", "Inception", now));

        // Open a brand new repository instance against the same on-disk .sqlite file.
        var repo2 = new SqliteContentBlockRepository(_dbPath);
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

        var repo1 = new SqliteContentBlockRepository(_dbPath);
        repo1.UpsertUserSettings(new UserBlockSettings(userId, false, disabledUntil, playlistId, now));

        var repo2 = new SqliteContentBlockRepository(_dbPath);
        var loaded = repo2.GetUserSettings(userId);

        Assert.NotNull(loaded);
        Assert.False(loaded.IsBlockingEnabled);
        Assert.Equal(disabledUntil, loaded.DisabledUntilUtc);
        Assert.Equal(playlistId, loaded.MagicPlaylistId);
    }
}
