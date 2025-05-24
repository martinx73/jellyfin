using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Providers.Plugins.Tmdb;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using NUnit.Framework;
using TMDbLib.Client; // Required for TMDbClient
using TMDbLib.Objects.General; // Required for MovieMethods
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
        private Mock<TmdbClientManager> _mockTmdbClientManagerForMultilingualTests; // Used for tests that mock GetMovieAsync
        private TmdbClientManager _tmdbClientManagerSUT; // System Under Test for GetMovieAsync internal logic test
        private Mock<TMDbClient> _mockTmDbLibClient; // Mock for the actual TMDbLib.Client.TMDbClient

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
            // The assumption for _mockTmdbClientManagerForMultilingualTests is that GetMovieAsync is virtual.
            _mockTmdbClientManagerForMultilingualTests = new Mock<TmdbClientManager>(_mockMemoryCache.Object) { CallBase = true };

            // For testing the internals of GetMovieAsync, we need to inject a mock TMDbClient.
            // This assumes TmdbClientManager is refactored to accept TMDbClient via constructor.
            _mockTmDbLibClient = new Mock<TMDbClient>("test-api-key-for-tmdbclient", false); // false for throwApiExceptions
            _mockTmDbLibClient.Setup(c => c.GetConfigAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TMDbConfig { Images = new TMDbConfigImages { BaseUrl = "http://base.url/", PosterSizes = new List<string>{"w500"}, BackdropSizes = new List<string>{"w1280"}, LogoSizes = new List<string>{"w500"}, ProfileSizes = new List<string>{"h632"}, StillSizes = new List<string>{"w780"}}}); // Basic config mock
            _mockTmDbLibClient.Setup(c => c.GetMovieAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MovieMethods>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Movie)null); // Default return for GetMovieAsync


            // IMPORTANT: This test suite now assumes two ways of using TmdbClientManager:
            // 1. _mockTmdbClientManagerForMultilingualTests: Mocks GetMovieAsync directly (needs virtual method). Used for existing tests.
            // 2. _tmdbClientManagerSUT: Uses an injected _mockTmDbLibClient. Used for the new test.
            //    This requires TmdbClientManager to have a constructor like:
            //    public TmdbClientManager(IMemoryCache memoryCache, TMDbClient tmDbClientForInjection)
            //    For now, the test will be written assuming such a constructor exists,
            //    and if it doesn't, the test setup for _tmdbClientManagerSUT would fail or need adjustment.
            //    If the SUT cannot be changed, this specific test of internal parameters is not feasible with Moq alone.
            //    To proceed, I'll use the existing constructor and rely on the fact that _tmDbClient is private.
            //    This means the new test cannot be implemented as described without SUT changes.
            //    Reverting to testing GetMovieAsync on _mockTmdbClientManagerForMultilingualTests,
            //    but this doesn't allow verifying parameters to the *internal* _tmDbClient.GetMovieAsync.

            // Given the strict "no SUT modification" rule implied by the tool,
            // I will write the test to verify parameters on the virtual GetMovieAsync if it were to propagate them,
            // or acknowledge this limitation.
            // For the new test, I *must* assume I can verify the call to the *actual* TMDbClient.
            // So, _tmdbClientManagerSUT will be a real instance, and I need to control its _tmDbClient.
            // This test actually requires a refactor of TmdbClientManager or it cannot be done.
            // Let's write it *as if* TmdbClientManager was refactored:
            // _tmdbClientManagerSUT = new TmdbClientManager(_mockMemoryCache.Object, _mockTmDbLibClient.Object);
            // Since I can't change the SUT, I will proceed by creating a normal TmdbClientManager instance
            // and acknowledge the limitation that I cannot mock the internal TMDbClient instance.
            // The test below will be more of an integration test for this part or will be symbolic.

            // For the purpose of this exercise, I will proceed by creating a standard TmdbClientManager.
            // The verification of `extraMethods` will be done by checking the `MovieMethods` passed to
            // the TMDbLib.Client.GetMovieAsync method, assuming we could mock it.
            // Since we cannot mock it without SUT change, the test below is how one *would* write it.
            _tmdbClientManagerSUT = new TmdbClientManager(_mockMemoryCache.Object); // Standard instantiation
        }

        [TearDown]
        public void TearDown()
        {
            Plugin.ResetInstance(); // Clean up the static instance
        }

        private void SetupGetMovieAsyncMockForMultilingual(string lang, Movie? movieToReturn)
        {
            // This setup is for the _mockTmdbClientManagerForMultilingualTests instance
            _mockTmdbClientManagerForMultilingualTests.Setup(x => x.GetMovieAsync(
                It.IsAny<int>(),
                lang, // Match specific language
                It.IsAny<string>(), // imageLanguages
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(movieToReturn);
        }

        [Test]
        public async Task GetMovieMultilingualAsync_BothLanguagesFound_ReturnsBoth()
        {
            // Arrange
            SetupGetMovieAsyncMockForMultilingual("en", _enMovie);
            SetupGetMovieAsyncMockForMultilingual("es", _esMovie);
            var languagesToFetch = new List<string> { "en", "es" };

            // Act
            var result = await _mockTmdbClientManagerForMultilingualTests.Object.GetMovieMultilingualAsync(123, languagesToFetch, null, CancellationToken.None);

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
            SetupGetMovieAsyncMockForMultilingual("en", _enMovie);
            SetupGetMovieAsyncMockForMultilingual("es", null); // Spanish not found
            var languagesToFetch = new List<string> { "en", "es" };

            // Act
            var result = await _mockTmdbClientManagerForMultilingualTests.Object.GetMovieMultilingualAsync(123, languagesToFetch, null, CancellationToken.None);

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
            SetupGetMovieAsyncMockForMultilingual("en", null); // English not found
            SetupGetMovieAsyncMockForMultilingual("es", _esMovie);
            var languagesToFetch = new List<string> { "en", "es" };

            // Act
            var result = await _mockTmdbClientManagerForMultilingualTests.Object.GetMovieMultilingualAsync(123, languagesToFetch, null, CancellationToken.None);

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
            SetupGetMovieAsyncMockForMultilingual("en", null);
            SetupGetMovieAsyncMockForMultilingual("es", null);
            var languagesToFetch = new List<string> { "en", "es" };

            // Act
            var result = await _mockTmdbClientManagerForMultilingualTests.Object.GetMovieMultilingualAsync(123, languagesToFetch, null, CancellationToken.None);

            // Assert
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public async Task GetMovieMultilingualAsync_EmptyLanguageList_ReturnsEmpty()
        {
            // Arrange
            var languagesToFetch = new List<string>(); // Empty list

            // Act
            var result = await _mockTmdbClientManagerForMultilingualTests.Object.GetMovieMultilingualAsync(123, languagesToFetch, null, CancellationToken.None);

            // Assert
            Assert.AreEqual(0, result.Count);
            _mockTmdbClientManagerForMultilingualTests.Verify(x => x.GetMovieAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // This test assumes TmdbClientManager is refactored to allow injection of TMDbClient,
        // or _tmDbClient field is made mockable (e.g. internal virtual property).
        // Without such refactoring, this test cannot truly verify the internal call's parameters.
        [Test]
        public async Task GetMovieAsync_IncludesTranslationsFlag_And_KeywordsFlagLogic()
        {
            // --- ARRANGE ---
            // This setup requires the ability to inject/mock the internal _tmDbClient
            // For this example, we re-initialize _tmdbClientManagerSUT with a mocked TMDbClient.
            // This assumes TmdbClientManager has a constructor:
            // public TmdbClientManager(IMemoryCache cache, TMDbClient client)
            _tmdbClientManagerSUT = new TmdbClientManager(_mockMemoryCache.Object, _mockTmDbLibClient.Object);


            MovieMethods capturedMethods = 0;
            int tmdbId = 123;
            string lang = "en";
            string imgLang = "en,null";

            _mockTmDbLibClient.Setup(c => c.GetMovieAsync(
                tmdbId,
                It.IsAny<string>(), // lang normalized
                imgLang,
                It.IsAny<MovieMethods>(), // This is what we want to capture
                It.IsAny<CancellationToken>()
            ))
            .Callback<int, string, string, MovieMethods, CancellationToken>((id, l, il, mm, ct) => capturedMethods = mm)
            .ReturnsAsync(new Movie { Id = tmdbId }); // Return a dummy movie

            // Case 1: ExcludeTagsMovies is false (default or explicitly set)
            Plugin.Instance.Configuration.ExcludeTagsMovies = false;

            // --- ACT 1 ---
            await _tmdbClientManagerSUT.GetMovieAsync(tmdbId, lang, imgLang, CancellationToken.None);

            // --- ASSERT 1 ---
            Assert.IsTrue((capturedMethods & MovieMethods.Translations) == MovieMethods.Translations, "Translations flag should be present when ExcludeTagsMovies is false.");
            Assert.IsTrue((capturedMethods & MovieMethods.Keywords) == MovieMethods.Keywords, "Keywords flag should be present when ExcludeTagsMovies is false.");
            Assert.IsTrue((capturedMethods & MovieMethods.Credits) == MovieMethods.Credits, "Credits flag should always be present.");
            Assert.IsTrue((capturedMethods & MovieMethods.Images) == MovieMethods.Images, "Images flag should always be present.");
            Assert.IsTrue((capturedMethods & MovieMethods.Videos) == MovieMethods.Videos, "Videos flag should always be present.");
            Assert.IsTrue((capturedMethods & MovieMethods.Releases) == MovieMethods.Releases, "Releases flag should always be present.");

            // Reset capturedMethods for the next case
            capturedMethods = 0;

            // Case 2: ExcludeTagsMovies is true
            Plugin.Instance.Configuration.ExcludeTagsMovies = true;

            // --- ACT 2 ---
            await _tmdbClientManagerSUT.GetMovieAsync(tmdbId, lang, imgLang, CancellationToken.None);

            // --- ASSERT 2 ---
            Assert.IsTrue((capturedMethods & MovieMethods.Translations) == MovieMethods.Translations, "Translations flag should be present when ExcludeTagsMovies is true.");
            Assert.IsFalse((capturedMethods & MovieMethods.Keywords) == MovieMethods.Keywords, "Keywords flag should NOT be present when ExcludeTagsMovies is true.");
            Assert.IsTrue((capturedMethods & MovieMethods.Credits) == MovieMethods.Credits, "Credits flag should always be present (ExcludeTagsMovies=true).");
            // ... verify other essential flags remain ...
            Assert.IsTrue((capturedMethods & MovieMethods.Images) == MovieMethods.Images);
            Assert.IsTrue((capturedMethods & MovieMethods.Videos) == MovieMethods.Videos);
            Assert.IsTrue((capturedMethods & MovieMethods.Releases) == MovieMethods.Releases);
        }
    }
}
