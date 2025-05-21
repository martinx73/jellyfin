using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.Dto; // Adjusted namespace if needed
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies; // For Movie
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities; // For BaseItemKind, ExtraType etc.
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using MediaBrowser.Common; // For IApplicationHost

// If DtoService is in Emby.Server.Implementations.Dto, then:
// using Emby.Server.Implementations.Dto;

// If DtoService is in Jellyfin.Server.Implementations.Dto, then:
// using Jellyfin.Server.Implementations.Dto;
// For the purpose of this test, I'll assume the original path was indicative of the namespace.

namespace Jellyfin.Server.Implementations.Tests.Dto
{
    [TestFixture]
    public class DtoServiceTests
    {
        private Mock<ILogger<DtoService>> _mockLogger;
        private Mock<ILibraryManager> _mockLibraryManager;
        private Mock<IUserDataManager> _mockUserDataManager;
        private Mock<IImageProcessor> _mockImageProcessor;
        private Mock<IProviderManager> _mockProviderManager;
        private Mock<IRecordingsManager> _mockRecordingsManager;
        private Mock<IApplicationHost> _mockAppHost;
        private Mock<IMediaSourceManager> _mockMediaSourceManager;
        private Mock<Lazy<ILiveTvManager>> _mockLazyLiveTvManager;
        private Mock<ILiveTvManager> _mockLiveTvManager; // Actual instance for the Lazy mock
        private Mock<ITrickplayManager> _mockTrickplayManager;
        private Mock<IChapterManager> _mockChapterManager;

        private DtoService _dtoService;

        [SetUp]
        public void SetUp()
        {
            _mockLogger = new Mock<ILogger<DtoService>>(MockBehavior.Loose);
            _mockLibraryManager = new Mock<ILibraryManager>(MockBehavior.Loose);
            _mockUserDataManager = new Mock<IUserDataManager>(MockBehavior.Loose);
            _mockImageProcessor = new Mock<IImageProcessor>(MockBehavior.Loose);
            _mockProviderManager = new Mock<IProviderManager>(MockBehavior.Loose);
            _mockRecordingsManager = new Mock<IRecordingsManager>(MockBehavior.Loose);
            _mockAppHost = new Mock<IApplicationHost>(MockBehavior.Loose);
            _mockMediaSourceManager = new Mock<IMediaSourceManager>(MockBehavior.Loose);
            _mockLiveTvManager = new Mock<ILiveTvManager>(MockBehavior.Loose);
            _mockLazyLiveTvManager = new Mock<Lazy<ILiveTvManager>>(() => _mockLiveTvManager.Object);
            _mockTrickplayManager = new Mock<ITrickplayManager>(MockBehavior.Loose);
            _mockChapterManager = new Mock<IChapterManager>(MockBehavior.Loose);

            // Setup common default behaviors
            _mockAppHost.Setup(x => x.SystemId).Returns("test-system-id");
            _mockProviderManager.Setup(x => x.GetExternalUrls(It.IsAny<BaseItem>())).Returns(Array.Empty<ExternalUrl>());


            _dtoService = new DtoService(
                _mockLogger.Object,
                _mockLibraryManager.Object,
                _mockUserDataManager.Object,
                _mockImageProcessor.Object,
                _mockProviderManager.Object,
                _mockRecordingsManager.Object,
                _mockAppHost.Object,
                _mockMediaSourceManager.Object,
                _mockLazyLiveTvManager.Object,
                _mockTrickplayManager.Object,
                _mockChapterManager.Object);
        }

        private DtoOptions GetDefaultDtoOptions()
        {
            return new DtoOptions { Fields = new[] { ItemFields.Overview, ItemFields.Taglines, ItemFields.OriginalTitle } };
        }

        [Test]
        public void AttachBasicFields_MultilingualOverviewAndTagline_ParsesCorrectly()
        {
            // Arrange
            var movie = new Movie
            {
                Name = "English Title",
                OriginalTitle = "Spanish Title", // This should be directly mapped
                Overview = "English Overview\n[JFLANG:ES]\nSpanish Overview",
                Tagline = "English Tagline[JFLANG:ES]Spanish Tagline"
            };
            var options = GetDefaultDtoOptions();

            // Act
            var dto = _dtoService.GetBaseItemDto(movie, options, null, null);

            // Assert
            Assert.AreEqual("English Title", dto.Name);
            Assert.AreEqual("Spanish Title", dto.OriginalTitle); // DtoService directly maps item.OriginalTitle
            Assert.AreEqual("English Overview", dto.Overview);
            Assert.AreEqual("Spanish Overview", dto.OverviewEs);
            CollectionAssert.AreEqual(new[] { "English Tagline", "Spanish Tagline" }, dto.Taglines);
        }

        [Test]
        public void AttachBasicFields_EnglishOnlyOverviewAndTagline_ParsesCorrectly()
        {
            // Arrange
            var movie = new Movie
            {
                Name = "English Title",
                OriginalTitle = "English Original",
                Overview = "English Overview", // No separator
                Tagline = "English Tagline"    // No separator
            };
            var options = GetDefaultDtoOptions();

            // Act
            var dto = _dtoService.GetBaseItemDto(movie, options, null, null);

            // Assert
            Assert.AreEqual("English Overview", dto.Overview);
            Assert.IsNull(dto.OverviewEs, "OverviewEs should be null for English-only overview.");
            CollectionAssert.AreEqual(new[] { "English Tagline" }, dto.Taglines);
        }

        [Test]
        public void AttachBasicFields_SpanishOnlyOverviewAndTagline_ParsesCorrectly()
        {
            // Arrange
            var movie = new Movie
            {
                Name = "Spanish Name", // Assuming name is Spanish if that's the only data
                OriginalTitle = "Spanish Original",
                Overview = "\n[JFLANG:ES]\nSpanish Overview",
                Tagline = "[JFLANG:ES]Spanish Tagline"
            };
            var options = GetDefaultDtoOptions();

            // Act
            var dto = _dtoService.GetBaseItemDto(movie, options, null, null);

            // Assert
            // DtoService parsing logic: if primary part is empty, it becomes empty string
            Assert.AreEqual(string.Empty, dto.Overview, "Primary overview should be empty string.");
            Assert.AreEqual("Spanish Overview", dto.OverviewEs);
            // DtoService parsing logic for taglines: leading separator results in an empty first element
            CollectionAssert.AreEqual(new[] { string.Empty, "Spanish Tagline" }, dto.Taglines);
        }

        [Test]
        public void AttachBasicFields_OverviewSeparatorPresentButNoSpanishText_HandlesCorrectly()
        {
            // Arrange
            var movie = new Movie
            {
                Overview = "English Overview\n[JFLANG:ES]\n" // Separator but empty Spanish part
            };
            var options = GetDefaultDtoOptions();

            // Act
            var dto = _dtoService.GetBaseItemDto(movie, options, null, null);

            // Assert
            Assert.AreEqual("English Overview", dto.Overview);
            Assert.AreEqual(string.Empty, dto.OverviewEs, "OverviewEs should be an empty string.");
        }

        [Test]
        public void AttachBasicFields_TaglineSeparatorPresentButNoSpanishText_HandlesCorrectly()
        {
            // Arrange
            var movie = new Movie
            {
                Tagline = "English Tagline[JFLANG:ES]" // Separator but empty Spanish part
            };
            var options = GetDefaultDtoOptions();

            // Act
            var dto = _dtoService.GetBaseItemDto(movie, options, null, null);

            // Assert
            CollectionAssert.AreEqual(new[] { "English Tagline", string.Empty }, dto.Taglines);
        }

        [Test]
        public void AttachBasicFields_OverviewAndTaglineNull_DoesNotThrow()
        {
            // Arrange
            var movie = new Movie
            {
                Overview = null,
                Tagline = null
            };
            var options = GetDefaultDtoOptions();

            // Act
            var dto = _dtoService.GetBaseItemDto(movie, options, null, null);

            // Assert
            Assert.IsNull(dto.Overview);
            Assert.IsNull(dto.OverviewEs);
            // DtoService initializes Taglines to empty array if item.Tagline is null/empty and no processing happens
            Assert.IsEmpty(dto.Taglines);
        }

        [Test]
        public void AttachBasicFields_OverviewWithNoSeparator_SpanishOverviewIsNull()
        {
            // Arrange
            var movie = new Movie
            {
                Overview = "This is a single language overview."
            };
            var options = GetDefaultDtoOptions();

            // Act
            var dto = _dtoService.GetBaseItemDto(movie, options, null, null);

            // Assert
            Assert.AreEqual("This is a single language overview.", dto.Overview);
            Assert.IsNull(dto.OverviewEs);
        }

        [Test]
        public void AttachBasicFields_TaglineWithNoSeparator_CorrectTaglineArray()
        {
            // Arrange
            var movie = new Movie
            {
                Tagline = "This is a single language tagline."
            };
            var options = GetDefaultDtoOptions();

            // Act
            var dto = _dtoService.GetBaseItemDto(movie, options, null, null);

            // Assert
            CollectionAssert.AreEqual(new[] { "This is a single language tagline." }, dto.Taglines);
        }
    }
}
