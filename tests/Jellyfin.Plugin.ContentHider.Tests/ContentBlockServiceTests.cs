using System;
using System.IO;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ContentHider.Configuration;
using Jellyfin.Plugin.ContentHider.Data;
using Jellyfin.Plugin.ContentHider.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Search;
using Moq;
using SQLitePCL;
using Xunit;

namespace Jellyfin.Plugin.ContentHider.Tests;

public sealed class ContentBlockServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteContentBlockRepository _repository;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly PluginConfiguration _config;
    private DateTime _currentUtc;

    static ContentBlockServiceTests()
    {
        Batteries_V2.Init();
    }

    public ContentBlockServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ContentHiderSvcTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "content_blocks.sqlite");
        _repository = new SqliteContentBlockRepository(_dbPath);
        _libraryManagerMock = new Mock<ILibraryManager>();
        _config = new PluginConfiguration
        {
            DefaultPauseDurationMinutes = 60,
            NormalizeEpisodesToSeries = true,
            HideEmptyCollections = true
        };
        _currentUtc = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void ToggleBlocking_SupportsManualAndTimedAutoReenable_AndPersistsToDisk()
    {
        var service = CreateService();
        var userId = Guid.NewGuid();
        var movieId = Guid.NewGuid();

        service.AddBlock(userId, movieId);
        Assert.True(service.IsBlockingActive(userId));
        Assert.Contains(movieId, service.GetExpandedBlockedIds(userId));

        // 1. Timed pause for 60 minutes.
        service.SetBlockingEnabled(userId, false, 60);
        Assert.False(service.IsBlockingActive(userId));
        Assert.Empty(service.GetExpandedBlockedIds(userId));

        // Verify persisted to SQLite on disk.
        var diskSettings = _repository.GetUserSettings(userId);
        Assert.NotNull(diskSettings);
        Assert.False(diskSettings.IsBlockingEnabled);
        Assert.Equal(_currentUtc.AddMinutes(60), diskSettings.DisabledUntilUtc);

        // Advance clock by 59 minutes -> still paused.
        _currentUtc = _currentUtc.AddMinutes(59);
        Assert.False(service.IsBlockingActive(userId));

        // Advance clock by 2 more minutes -> auto-reenables and persists back to disk!
        _currentUtc = _currentUtc.AddMinutes(2);
        Assert.True(service.IsBlockingActive(userId));
        Assert.Contains(movieId, service.GetExpandedBlockedIds(userId));

        var reenabledOnDisk = _repository.GetUserSettings(userId);
        Assert.NotNull(reenabledOnDisk);
        Assert.True(reenabledOnDisk.IsBlockingEnabled);
        Assert.Null(reenabledOnDisk.DisabledUntilUtc);

        // 2. Manual toggle off (0 minutes) -> stays off indefinitely until manually re-enabled.
        service.SetBlockingEnabled(userId, false, 0);
        _currentUtc = _currentUtc.AddHours(24);
        Assert.False(service.IsBlockingActive(userId));

        service.SetBlockingEnabled(userId, true);
        Assert.True(service.IsBlockingActive(userId));
    }

    [Fact]
    public void AddBlock_NormalizesEpisodeToParentSeries_AndExpandsDescendants()
    {
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();

        var series = new Series { Id = seriesId, Name = "The Bear" };
        var episode = new Episode { Id = episodeId, SeriesId = seriesId, SeasonId = seasonId, Name = "Pilot" };

        _libraryManagerMock.Setup(m => m.GetItemById(episodeId)).Returns(episode);
        _libraryManagerMock.Setup(m => m.GetItemById(seriesId)).Returns(series);
        _libraryManagerMock
            .Setup(m => m.GetItemIds(It.IsAny<InternalItemsQuery>()))
            .Returns([seasonId, episodeId]);

        var service = CreateService();

        // Blocking the episode normalizes to the parent Series on disk.
        var record = service.AddBlock(userId, episodeId);
        Assert.Equal(seriesId, record.MediaId);
        Assert.Equal("The Bear", record.MediaName);
        Assert.Equal(nameof(BaseItemKind.Series), record.MediaType);

        var expanded = service.GetExpandedBlockedIds(userId);
        Assert.Contains(seriesId, expanded);
        Assert.Contains(seasonId, expanded);
        Assert.Contains(episodeId, expanded);

        // DTO and SearchHint checks for child episode also return true.
        var episodeDto = new BaseItemDto { Id = Guid.NewGuid(), SeriesId = seriesId, Type = BaseItemKind.Episode };
        Assert.True(service.IsDtoBlocked(userId, episodeDto, expanded));

        var hint = new SearchHint { Id = episodeId, Name = "Pilot", Type = BaseItemKind.Episode };
        Assert.True(service.IsSearchHintBlocked(userId, hint, expanded));
    }

    [Fact]
    public void ExternalSqliteModificationOnDisk_IsDetectedAndReloadedAutomatically()
    {
        var service = CreateService();
        var userId = Guid.NewGuid();
        var externalMovieId = Guid.NewGuid();

        Assert.Empty(service.GetUserBlocks(userId));

        // Simulate an external write to the on-disk .sqlite file and bump last-write timestamp.
        var externalRepo = new SqliteContentBlockRepository(_dbPath);
        externalRepo.UpsertBlock(new ContentBlockRecord(userId, externalMovieId, "Movie", "Added Externally", _currentUtc));
        File.SetLastWriteTimeUtc(_dbPath, DateTime.UtcNow.AddSeconds(5));

        var blocks = service.GetUserBlocks(userId);
        Assert.Single(blocks);
        Assert.Equal(externalMovieId, blocks[0].MediaId);
    }

    private ContentBlockService CreateService()
    {
        return new ContentBlockService(
            _repository,
            _libraryManagerMock.Object,
            () => _config,
            () => _currentUtc);
    }
}
