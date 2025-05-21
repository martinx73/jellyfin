using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Providers.Plugins.Tmdb;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using NUnit.Framework;
using TMDbLib.Objects.Movies; // Required for TMDbLib.Objects.Movies.Movie

// Mocking Plugin class and its Configuration property
namespace MediaBrowser.Providers.Plugins.Tmdb
{
    public class Plugin
    {
        private static Plugin _instance;
        public static Plugin Instance => _instance ??= new Plugin();

        public TmdbPluginConfiguration Configuration { get; private set; }

        // Private constructor to prevent external instantiation for singleton.
        // For testing, we might need a way to reset or set this.
        private Plugin()
        {
            Configuration = new TmdbPluginConfiguration();
        }

        // Allow tests to set a mock configuration
        public static void SetTestInstance(Plugin testInstance)
        {
            _instance = testInstance;
        }

        public static void ResetInstance()
        {
            _instance = null;
        }
    }

    public class TmdbPluginConfiguration
    {
        public string TmdbApiKey { get; set; } = "testkey"; // Default for tests
        public bool ExcludeTagsMovies { get; set; }
        // Add other configuration properties if TmdbClientManager directly uses them
    }
}

namespace Jellyfin.Providers.Tests.Plugins.Tmdb
{
    [TestFixture]
    public class TmdbClientManagerTests
    {
        private Mock<IMemoryCache> _mockMemoryCache;
        private Mock<TmdbClientManager> _mockTmdbClientManager; // Will be CallBase = true

        // Sample movie data
        private Movie _enMovie = new Movie { Id = 1, Title = "English Movie" };
        private Movie _esMovie = new Movie { Id = 2, Title = "Película Española" };

        [SetUp]
        public void SetUp()
        {
            _mockMemoryCache = new Mock<IMemoryCache>();

            // Mock IMemoryCache.TryGetValue to simulate cache misses
            object? outValue = null; // Define an object? variable for the out parameter
            _mockMemoryCache.Setup(m => m.TryGetValue(It.IsAny<object>(), out outValue))
                           .Returns(false);


            // Mock Plugin.Instance.Configuration
            var mockPluginConfiguration = new TmdbPluginConfiguration { TmdbApiKey = "test-api-key" };
            var mockPlugin = new Mock<Plugin>(); // Can't mock Plugin directly if constructor is private/internal
                                                 // Instead, we use the static SetTestInstance if available or ensure Configuration is settable.
                                                 // For this structure, let's reset and let the default test key be used or set it.
            Plugin.ResetInstance(); // Ensure a fresh instance for each test if needed
            Plugin.Instance.Configuration.TmdbApiKey = "test-api-key";


            // TmdbClientManager will be mocked with CallBase = true
            // This means the actual GetMovieMultilingualAsync will run,
            // but we can mock its dependency on GetMovieAsync.
            // The constructor of TmdbClientManager news up TMDbClient, which might be an issue
            // if TMDbClient itself makes network calls or needs heavy setup.
            // The assumption is that GetMovieAsync is virtual and can be mocked.
            _mockTmdbClientManager = new Mock<TmdbClientManager>(_mockMemoryCache.Object) { CallBase = true };
        }

        [TearDown]
        public void TearDown()
        {
            Plugin.ResetInstance(); // Clean up the static instance
        }

        private void SetupGetMovieAsyncMock(string lang, Movie? movieToReturn)
        {
            _mockTmdbClientManager.Setup(x => x.GetMovieAsync(
                It.IsAny<int>(),
                lang, // Match specific language
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(movieToReturn);
        }

        [Test]
        public async Task GetMovieMultilingualAsync_BothLanguagesFound_ReturnsBoth()
        {
            // Arrange
            SetupGetMovieAsyncMock("en", _enMovie);
            SetupGetMovieAsyncMock("es", _esMovie);
            var languagesToFetch = new List<string> { "en", "es" };

            // Act
            var result = await _mockTmdbClientManager.Object.GetMovieMultilingualAsync(123, languagesToFetch, null, CancellationToken.None);

            // Assert
            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result.ContainsKey("en"));
            Assert.AreEqual(_enMovie.Title, result["en"].Title);
            Assert.IsTrue(result.ContainsKey("es"));
            Assert.AreEqual(_esMovie.Title, result["es"].Title);
        }

        [Test]
        public async Task GetMovieMultilingualAsync_EnglishOnlyFound_ReturnsEnglish()
        {
            // Arrange
            SetupGetMovieAsyncMock("en", _enMovie);
            SetupGetMovieAsyncMock("es", null); // Spanish not found
            var languagesToFetch = new List<string> { "en", "es" };

            // Act
            var result = await _mockTmdbClientManager.Object.GetMovieMultilingualAsync(123, languagesToFetch, null, CancellationToken.None);

            // Assert
            Assert.AreEqual(1, result.Count);
            Assert.IsTrue(result.ContainsKey("en"));
            Assert.AreEqual(_enMovie.Title, result["en"].Title);
            Assert.IsFalse(result.ContainsKey("es"));
        }

        [Test]
        public async Task GetMovieMultilingualAsync_SpanishOnlyFound_ReturnsSpanish()
        {
            // Arrange
            SetupGetMovieAsyncMock("en", null); // English not found
            SetupGetMovieAsyncMock("es", _esMovie);
            var languagesToFetch = new List<string> { "en", "es" };

            // Act
            var result = await _mockTmdbClientManager.Object.GetMovieMultilingualAsync(123, languagesToFetch, null, CancellationToken.None);

            // Assert
            Assert.AreEqual(1, result.Count);
            Assert.IsFalse(result.ContainsKey("en"));
            Assert.IsTrue(result.ContainsKey("es"));
            Assert.AreEqual(_esMovie.Title, result["es"].Title);
        }

        [Test]
        public async Task GetMovieMultilingualAsync_NoLanguagesFound_ReturnsEmpty()
        {
            // Arrange
            SetupGetMovieAsyncMock("en", null);
            SetupGetMovieAsyncMock("es", null);
            var languagesToFetch = new List<string> { "en", "es" };

            // Act
            var result = await _mockTmdbClientManager.Object.GetMovieMultilingualAsync(123, languagesToFetch, null, CancellationToken.None);

            // Assert
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public async Task GetMovieMultilingualAsync_EmptyLanguageList_ReturnsEmpty()
        {
            // Arrange
            var languagesToFetch = new List<string>(); // Empty list

            // Act
            var result = await _mockTmdbClientManager.Object.GetMovieMultilingualAsync(123, languagesToFetch, null, CancellationToken.None);

            // Assert
            Assert.AreEqual(0, result.Count);
            _mockTmdbClientManager.Verify(x => x.GetMovieAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
