using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ContentHider.Configuration;
using Jellyfin.Plugin.ContentHider.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Search;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ContentHider.Tests;

public sealed class ContentBlockServiceTests
{
    private readonly Mock<IPlaylistManager> _playlistManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private DateTime _currentUtc;

    public ContentBlockServiceTests()
    {
        _playlistManagerMock = new Mock<IPlaylistManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration
        {
            DefaultPauseDurationMinutes = 60,
            HideEmptyCollections = true
        };
        _currentUtc = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    }

    [Fact]
    public async Task ToggleBlocking_SyncsWithPlaylistFavoriteState_AndSupportsTimedAutoReenable()
    {
        var service = CreateService();
        var userId = Guid.NewGuid();
        var playlistId = Guid.NewGuid();
        var movieId = Guid.NewGuid();

        var user = new User("doug", "DefaultAuth", "DefaultReset") { Id = userId };
        var playlist = new Playlist
        {
            Id = playlistId,
            OwnerUserId = userId,
            Name = "[doug: Hide Content]",
            Tags = [MagicPlaylistService.ManagedPlaylistTag]
        };
        var userData = new UserItemData { Key = playlistId.ToString("N"), IsFavorite = false };
        var movie = new Movie { Id = movieId, Name = "Inception" };

        _userManagerMock.Setup(u => u.GetUserById(userId)).Returns(user);
        _playlistManagerMock.Setup(p => p.GetPlaylists(userId)).Returns([playlist]);
        _playlistManagerMock.Setup(p => p.GetPlaylistForUser(playlistId, userId)).Returns(playlist);
        _userDataManagerMock.Setup(d => d.GetUserData(user, playlist)).Returns(userData);
        _libraryManagerMock.Setup(m => m.GetItemById(movieId)).Returns(movie);

        Assert.True(await service.TryAddBlockAsync(userId, movieId));
        Assert.True(service.IsBlockingActive(userId));
        Assert.Contains(movieId, service.GetExpandedBlockedIds(userId));

        // 1. Timed pause for 60 minutes sets IsFavorite = true on the playlist's UserItemData.
        service.SetBlockingEnabled(userId, false, 60);
        Assert.True(userData.IsFavorite);
        Assert.False(service.IsBlockingActive(userId));
        Assert.Empty(service.GetExpandedBlockedIds(userId));

        // Advance clock by 59 minutes -> still paused.
        _currentUtc = _currentUtc.AddMinutes(59);
        Assert.False(service.IsBlockingActive(userId));

        // Advance clock by 2 more minutes -> auto-reenables and clears IsFavorite!
        _currentUtc = _currentUtc.AddMinutes(2);
        Assert.True(service.IsBlockingActive(userId));
        Assert.False(userData.IsFavorite);
        Assert.Contains(movieId, service.GetExpandedBlockedIds(userId));

        // 2. Manual toggle off (0 minutes) -> stays off indefinitely until manually re-enabled.
        service.SetBlockingEnabled(userId, false, 0);
        _currentUtc = _currentUtc.AddHours(24);
        Assert.False(service.IsBlockingActive(userId));

        service.SetBlockingEnabled(userId, true);
        Assert.True(service.IsBlockingActive(userId));
        Assert.False(userData.IsFavorite);
    }

    [Fact]
    public async Task TryAddBlockAsync_AllowsSeriesAndMovies_ExpandsDescendants_AndRejectsEpisodes()
    {
        var userId = Guid.NewGuid();
        var playlistId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var otherSeriesId = Guid.NewGuid();
        var otherEpisodeId = Guid.NewGuid();

        var playlist = new Playlist
        {
            Id = playlistId,
            OwnerUserId = userId,
            Name = "[doug: Hide Content]",
            Tags = [MagicPlaylistService.ManagedPlaylistTag]
        };

        var series = new Series { Id = seriesId, Name = "The Bear" };
        var episode = new Episode { Id = episodeId, SeriesId = seriesId, SeasonId = seasonId, Name = "Pilot" };
        var otherEpisode = new Episode { Id = otherEpisodeId, SeriesId = otherSeriesId, Name = "Other Episode" };

        _playlistManagerMock.Setup(p => p.GetPlaylists(userId)).Returns([playlist]);
        _playlistManagerMock.Setup(p => p.GetPlaylistForUser(playlistId, userId)).Returns(playlist);
        _libraryManagerMock.Setup(m => m.GetItemById(seriesId)).Returns(series);
        _libraryManagerMock.Setup(m => m.GetItemById(episodeId)).Returns(episode);
        _libraryManagerMock.Setup(m => m.GetItemById(otherEpisodeId)).Returns(otherEpisode);
        _libraryManagerMock
            .Setup(m => m.GetItemIds(It.IsAny<InternalItemsQuery>()))
            .Returns([seasonId, episodeId]);

        var service = CreateService();

        // 1. Attempting to block a single episode is rejected and does NOT hide its parent series.
        var episodeAdded = await service.TryAddBlockAsync(userId, otherEpisodeId);
        Assert.False(episodeAdded);
        Assert.Empty(playlist.LinkedChildren ?? []);
        Assert.DoesNotContain(otherSeriesId, service.GetExpandedBlockedIds(userId));

        // Even if a single episode were manually present in LinkedChildren, it must NOT hide the series.
        playlist.LinkedChildren =
        [
            new LinkedChild { ItemId = otherEpisodeId, Type = MediaBrowser.Controller.Entities.LinkedChildType.Manual }
        ];
        Assert.DoesNotContain(otherSeriesId, service.GetExpandedBlockedIds(userId));
        Assert.DoesNotContain(otherEpisodeId, service.GetExpandedBlockedIds(userId));

        // 2. Blocking a Series succeeds and expands downward to its child Seasons and Episodes.
        playlist.LinkedChildren = [];
        var seriesAdded = await service.TryAddBlockAsync(userId, seriesId);
        Assert.True(seriesAdded);
        Assert.Single(playlist.LinkedChildren);
        Assert.Equal(seriesId, playlist.LinkedChildren[0].ItemId);

        var expanded = service.GetExpandedBlockedIds(userId);
        Assert.Contains(seriesId, expanded);
        Assert.Contains(seasonId, expanded);
        Assert.Contains(episodeId, expanded);

        // DTO and SearchHint checks for child episode of the blocked series return true.
        var episodeDto = new BaseItemDto { Id = Guid.NewGuid(), SeriesId = seriesId, Type = BaseItemKind.Episode };
        Assert.True(service.IsDtoBlocked(userId, episodeDto, expanded));

        var hint = new SearchHint { Id = episodeId, Name = "Pilot", Type = BaseItemKind.Episode };
        Assert.True(service.IsSearchHintBlocked(userId, hint, expanded));
    }

    [Fact]
    public async Task TagBasedPlaylistResolution_FindsManagedPlaylistEvenWhenRenamed()
    {
        var service = CreateService();
        var userId = Guid.NewGuid();
        var regularPlaylistId = Guid.NewGuid();
        var managedPlaylistId = Guid.NewGuid();
        var movieId = Guid.NewGuid();

        var regularPlaylist = new Playlist
        {
            Id = regularPlaylistId,
            OwnerUserId = userId,
            Name = "[doug: Hide Content]",
            Tags = []
        };
        var managedPlaylist = new Playlist
        {
            Id = managedPlaylistId,
            OwnerUserId = userId,
            Name = "My Custom Renamed Hidden List",
            Tags = [MagicPlaylistService.ManagedPlaylistTag]
        };
        var movie = new Movie { Id = movieId, Name = "Arrival" };

        _playlistManagerMock.Setup(p => p.GetPlaylists(userId)).Returns([regularPlaylist, managedPlaylist]);
        _playlistManagerMock.Setup(p => p.GetPlaylistForUser(managedPlaylistId, userId)).Returns(managedPlaylist);
        _libraryManagerMock.Setup(m => m.GetItemById(movieId)).Returns(movie);

        Assert.Equal(managedPlaylistId, service.GetMagicPlaylistId(userId));

        Assert.True(await service.TryAddBlockAsync(userId, movieId));
        var blockedItems = service.GetBlockedItems(userId);
        Assert.Single(blockedItems);
        Assert.Equal(movieId, blockedItems[0].Id);
    }

    private ContentBlockService CreateService()
    {
        return new ContentBlockService(
            _playlistManagerMock.Object,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            () => _config,
            () => _currentUtc);
    }
}
