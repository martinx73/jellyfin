using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Providers.Plugins.Tmdb;
using MediaBrowser.Providers.Plugins.Tmdb.Movies;
using Moq;
using NUnit.Framework;
using TMDbLib.Objects.General; // Required for Country and other TMDbLib nested objects
using TMDbLib.Objects.Movies;  // Required for TMDbLib.Objects.Movies.Movie

// Minimal mock for Plugin and Configuration for tests
namespace MediaBrowser.Providers.Plugins.Tmdb
{
    // This is a simplified mock for testing.
    // If a shared test utility for this exists, it should be used instead.
    public class Plugin
    {
        private static Plugin _instance;
        public static Plugin Instance => _instance ??= new Plugin();

        public TmdbPluginConfiguration Configuration { get; private set; }

        private Plugin()
        {
            Configuration = new TmdbPluginConfiguration();
        }

        public static void SetTestInstance(Plugin testInstance) => _instance = testInstance;
        public static void ResetInstance() => _instance = null;
    }

    public class TmdbPluginConfiguration
    {
        public string TmdbApiKey { get; set; } = "testkey";
        public int MaxCastMembers { get; set; } = 30; // Default value used in provider
        public string PosterSize { get; set; } = "w780";
        public string BackdropSize { get; set; } = "w1280";
        public string LogoSize { get; set; } = "w500";
        public string ProfileSize { get; set; } = "h632";
        public string StillSize { get; set; } = "w780";
        // Add other relevant properties if GetMetadata uses them
    }
}

namespace Jellyfin.Providers.Tests.Plugins.Tmdb.Movies
{
    [TestFixture]
    public class TmdbMovieProviderTests
    {
        private Mock<TmdbClientManager> _mockTmdbClientManager;
        private Mock<ILibraryManager> _mockLibraryManager;
        private Mock<IHttpClientFactory> _mockHttpClientFactory;
        private TmdbMovieProvider _tmdbMovieProvider;

        private const string TmdbId = "123";
        private const string ImdbId = "tt1234567";

        [SetUp]
        public void SetUp()
        {
            _mockTmdbClientManager = new Mock<TmdbClientManager>(MockBehavior.Strict, new Mock<Microsoft.Extensions.Caching.Memory.IMemoryCache>().Object);
            _mockLibraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
            _mockHttpClientFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);

            // Setup Plugin.Instance.Configuration
            Plugin.ResetInstance(); // Ensure a fresh instance for each test
            Plugin.Instance.Configuration.TmdbApiKey = "test-api-key";
            Plugin.Instance.Configuration.MaxCastMembers = 30;


            _tmdbMovieProvider = new TmdbMovieProvider(
                _mockLibraryManager.Object,
                _mockTmdbClientManager.Object,
                _mockHttpClientFactory.Object);

            // Common Setups
            _mockLibraryManager.Setup(x => x.ParseName(It.IsAny<string>()))
                .Returns<string>(name => new FileNameParseResult { Name = name, Year = null });

            // Setup for GetPosterUrl and GetProfileUrl as they might be called for non-multilingual parts
            _mockTmdbClientManager.Setup(c => c.GetPosterUrl(It.IsAny<string>())).Returns((string path) => $"http://example.com/poster{path}");
            _mockTmdbClientManager.Setup(c => c.GetProfileUrl(It.IsAny<string>())).Returns((string path) => $"http://example.com/profile{path}");

            // Default setup for GetMovieMultilingualAsync, can be overridden in tests
            _mockTmdbClientManager.Setup(x => x.GetMovieMultilingualAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, global::TMDbLib.Objects.Movies.Movie>());

            // Setup for other calls made by GetMetadata if necessary to avoid StrictMock errors
            // For example, if Keywords or Credits are processed:
            var emptyMovie = new global::TMDbLib.Objects.Movies.Movie
            {
                Keywords = new KeywordsContainer(),
                Credits = new CreditsWithGuestStars { Cast = new List<Cast>(), Crew = new List<Crew>() },
                Releases = new Releases { Countries = new List<Country>() },
                ProductionCountries = new List<ProductionCountry>(),
                Genres = new List<Genre>(),
                ProductionCompanies = new List<ProductionCompany>(),
                Videos = new ResultContainer<Video>() { Results = new List<Video>()} // Ensure Videos.Results is not null
            };
             _mockTmdbClientManager.Setup(x => x.GetMovieAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(emptyMovie); // Fallback for any direct GetMovieAsync calls if any
        }

        [TearDown]
        public void TearDown()
        {
            Plugin.ResetInstance();
        }

        private MovieInfo CreateMovieInfo(string metadataLanguage = "en", string metadataCountryCode = "US")
        {
            var movieInfo = new MovieInfo
            {
                Name = "Test Movie",
                MetadataLanguage = metadataLanguage,
                MetadataCountryCode = metadataCountryCode
            };
            movieInfo.SetProviderId(MetadataProvider.Tmdb, TmdbId);
            return movieInfo;
        }

        private global::TMDbLib.Objects.Movies.Movie CreateTmdbLibMovie(
            string title, string originalTitle, string overview, string tagline,
            List<ProductionCountry> productionCountries = null, List<Genre> genres = null,
            KeywordsContainer keywords = null, CreditsWithGuestStars credits = null, Releases releases = null,
            List<ProductionCompany> productionCompanies = null, ResultContainer<Video> videos = null)
        {
            return new global::TMDbLib.Objects.Movies.Movie
            {
                Title = title,
                OriginalTitle = originalTitle,
                Overview = overview,
                Tagline = tagline,
                ImdbId = ImdbId, // For TrySetProviderId
                ProductionCountries = productionCountries ?? new List<ProductionCountry>(),
                Genres = genres ?? new List<Genre>(),
                Keywords = keywords ?? new KeywordsContainer(),
                Credits = credits ?? new CreditsWithGuestStars { Cast = new List<Cast>(), Crew = new List<Crew>() },
                Releases = releases ?? new Releases { Countries = new List<Country>() },
                ProductionCompanies = productionCompanies ?? new List<ProductionCompany>(),
                Videos = videos ?? new ResultContainer<Video>() { Results = new List<Video>() }
            };
        }

        [Test]
        public async Task GetMetadata_Multilingual_BothLanguagesPresent_CorrectlyFormatsFields()
        {
            // Arrange
            var info = CreateMovieInfo("en");
            var enMovie = CreateTmdbLibMovie("English Title", "English Original", "English Overview.", "English Tagline.");
            var esMovie = CreateTmdbLibMovie("Spanish Title", "Spanish Original", "Spanish Overview.", "Spanish Tagline.");

            var multilingualData = new Dictionary<string, global::TMDbLib.Objects.Movies.Movie>
            {
                { "en", enMovie },
                { "es", esMovie }
            };
            _mockTmdbClientManager.Setup(x => x.GetMovieMultilingualAsync(Convert.ToInt32(TmdbId), It.Is<IEnumerable<string>>(langs => langs.Contains("en") && langs.Contains("es")), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                                  .ReturnsAsync(multilingualData);

            // Act
            var result = await _tmdbMovieProvider.GetMetadata(info, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("en", result.ResultLanguage); // Assuming 'en' is primary in this setup
            Assert.AreEqual("English Title", result.Item.Name);
            Assert.AreEqual("Spanish Title", result.Item.OriginalTitle); // As per requirement: OriginalTitle should be Spanish if primary is English
            Assert.AreEqual("English Overview.\n[JFLANG:ES]\nSpanish Overview.", result.Item.Overview);
            Assert.AreEqual("English Tagline.[JFLANG:ES]Spanish Tagline.", result.Item.Tagline);
        }

        [Test]
        public async Task GetMetadata_Multilingual_EnglishOnly_CorrectlyFormatsFields()
        {
            // Arrange
            var info = CreateMovieInfo("en");
            var enMovie = CreateTmdbLibMovie("English Title", "English Original Title", "English Overview.", "English Tagline.");
            var multilingualData = new Dictionary<string, global::TMDbLib.Objects.Movies.Movie> { { "en", enMovie } };
            _mockTmdbClientManager.Setup(x => x.GetMovieMultilingualAsync(Convert.ToInt32(TmdbId), It.IsAny<IEnumerable<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                                  .ReturnsAsync(multilingualData);

            // Act
            var result = await _tmdbMovieProvider.GetMetadata(info, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("en", result.ResultLanguage);
            Assert.AreEqual("English Title", result.Item.Name);
            Assert.AreEqual("English Original Title", result.Item.OriginalTitle); // No Spanish data, so OriginalTitle is from 'en'
            Assert.AreEqual("English Overview.", result.Item.Overview);
            Assert.AreEqual("English Tagline.", result.Item.Tagline);
        }

        [Test]
        public async Task GetMetadata_Multilingual_SpanishOnly_CorrectlyFormatsFields()
        {
            // Arrange
            var info = CreateMovieInfo("es"); // User's preference is Spanish
            var esMovie = CreateTmdbLibMovie("Título Español", "Título Original Español", "Resumen en Español.", "Lema en Español.");
            var multilingualData = new Dictionary<string, global::TMDbLib.Objects.Movies.Movie> { { "es", esMovie } };
             _mockTmdbClientManager.Setup(x => x.GetMovieMultilingualAsync(Convert.ToInt32(TmdbId), It.Is<IEnumerable<string>>(langs => langs.First() == "es"), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                                  .ReturnsAsync(multilingualData);


            // Act
            var result = await _tmdbMovieProvider.GetMetadata(info, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("es", result.ResultLanguage); // Spanish is primary
            Assert.AreEqual("Título Español", result.Item.Name);
            Assert.AreEqual("Título Original Español", result.Item.OriginalTitle); // No English data, OriginalTitle from 'es'
            Assert.AreEqual("Resumen en Español.", result.Item.Overview); // Overview is Spanish
            Assert.AreEqual("Lema en Español.", result.Item.Tagline);   // Tagline is Spanish
        }

        [Test]
        public async Task GetMetadata_Multilingual_NeitherLanguage_ReturnsNoMetadata()
        {
            // Arrange
            var info = CreateMovieInfo();
            _mockTmdbClientManager.Setup(x => x.GetMovieMultilingualAsync(Convert.ToInt32(TmdbId), It.IsAny<IEnumerable<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                                  .ReturnsAsync(new Dictionary<string, global::TMDbLib.Objects.Movies.Movie>()); // Empty result

            // Act
            var result = await _tmdbMovieProvider.GetMetadata(info, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.HasMetadata);
        }

        [Test]
        public async Task GetMetadata_Multilingual_EnglishOverviewSpanishEmpty_FormatsCorrectly()
        {
            // Arrange
            var info = CreateMovieInfo("en");
            var enMovie = CreateTmdbLibMovie("English Title", "English Original", "English Overview.", "English Tagline.");
            var esMovie = CreateTmdbLibMovie("Spanish Title", "Spanish Original", "", ""); // Empty Spanish overview/tagline
            var multilingualData = new Dictionary<string, global::TMDbLib.Objects.Movies.Movie> { { "en", enMovie }, { "es", esMovie } };
            _mockTmdbClientManager.Setup(x => x.GetMovieMultilingualAsync(It.IsAny<int>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                                  .ReturnsAsync(multilingualData);

            // Act
            var result = await _tmdbMovieProvider.GetMetadata(info, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("English Overview.", result.Item.Overview); // Only English overview, no separator
            Assert.AreEqual("English Tagline.", result.Item.Tagline);   // Only English tagline, no separator
        }

        [Test]
        public async Task GetMetadata_Multilingual_SpanishOverviewEnglishEmpty_FormatsCorrectly()
        {
            // Arrange
            var info = CreateMovieInfo("en"); // User prefers English, but English data is sparse
            var enMovie = CreateTmdbLibMovie("English Title", "English Original", "", ""); // Empty English overview/tagline
            var esMovie = CreateTmdbLibMovie("Spanish Title", "Spanish Original", "Spanish Overview.", "Spanish Tagline.");
             var multilingualData = new Dictionary<string, global::TMDbLib.Objects.Movies.Movie> { { "en", enMovie }, { "es", esMovie } };
            _mockTmdbClientManager.Setup(x => x.GetMovieMultilingualAsync(It.IsAny<int>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                                  .ReturnsAsync(multilingualData);

            // Act
            var result = await _tmdbMovieProvider.GetMetadata(info, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.HasMetadata);
            // Provider logic: if primary (English) overview is empty, and Spanish is present, it formats with separator
            Assert.AreEqual("\n[JFLANG:ES]\nSpanish Overview.", result.Item.Overview);
            Assert.AreEqual("[JFLANG:ES]Spanish Tagline.", result.Item.Tagline);
        }


        [Test]
        public async Task GetMetadata_Multilingual_PrimaryLanguageNotEnOrEs_FetchedAndCombined()
        {
            // Arrange
            var info = CreateMovieInfo("de"); // User's preference is German
            var deMovie = CreateTmdbLibMovie("Deutscher Titel", "Deutscher Originaltitel", "Deutsche Übersicht.", "Deutscher Slogan.");
            var enMovie = CreateTmdbLibMovie("English Title", "English Original", "English Overview.", "English Tagline.");
            var esMovie = CreateTmdbLibMovie("Spanish Title", "Spanish Original", "Spanish Overview.", "Spanish Tagline.");

            var multilingualData = new Dictionary<string, global::TMDbLib.Objects.Movies.Movie>
            {
                { "de", deMovie }, // This will be primaryMovieData
                { "en", enMovie }, // This will be enMovieDataForTitle
                { "es", esMovie }  // This will be esMovieData
            };
            _mockTmdbClientManager.Setup(x => x.GetMovieMultilingualAsync(
                Convert.ToInt32(TmdbId),
                It.Is<IEnumerable<string>>(langs => langs.SequenceEqual(new[] { "de", "en", "es" })), // Order matters for primary selection
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(multilingualData);

            // Act
            var result = await _tmdbMovieProvider.GetMetadata(info, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("de", result.ResultLanguage); // German is primary
            Assert.AreEqual("Deutscher Titel", result.Item.Name);
            // OriginalTitle logic when primary is not 'en' or 'es':
            // If primary is 'de', OriginalTitle becomes 'primaryMovieData.OriginalTitle'
            // unless specific logic for 'en'/'es' as secondary is hit.
            // The provider logic is: if primaryLang is 'en', OT is 'es'. If primaryLang is 'es', OT is 'en'. Otherwise OT is primary's OT.
            Assert.AreEqual("Deutscher Originaltitel", result.Item.OriginalTitle);

            // Overview: German (primary) + Spanish (secondary, if logic adds it)
            // Provider logic: primary overview + \n[JFLANG:ES]\n + es_overview (if es is found and not primary)
            Assert.AreEqual("Deutsche Übersicht.\n[JFLANG:ES]\nSpanish Overview.", result.Item.Overview);

            // Tagline: German (primary) + Spanish (secondary, if logic adds it)
            // Provider logic: primary tagline + [JFLANG:ES] + es_tagline (if es is found and not primary)
            Assert.AreEqual("Deutscher Slogan.[JFLANG:ES]Spanish Tagline.", result.Item.Tagline);
        }


        [Test]
        public async Task GetMetadata_Multilingual_EnglishPrimary_SpanishOriginalTitle_NoSpanishOverviewOrTagline()
        {
            // Arrange
            var info = CreateMovieInfo("en");
            var enMovie = CreateTmdbLibMovie("English Title", "English Original", "English Overview.", "English Tagline.");
            var esMovie = CreateTmdbLibMovie("Spanish Title Only", "Spanish Original Title Only", "", ""); // Spanish title, but no overview/tagline

            var multilingualData = new Dictionary<string, global::TMDbLib.Objects.Movies.Movie>
            {
                { "en", enMovie },
                { "es", esMovie }
            };
            _mockTmdbClientManager.Setup(x => x.GetMovieMultilingualAsync(Convert.ToInt32(TmdbId), It.IsAny<IEnumerable<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                                  .ReturnsAsync(multilingualData);

            // Act
            var result = await _tmdbMovieProvider.GetMetadata(info, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("en", result.ResultLanguage);
            Assert.AreEqual("English Title", result.Item.Name);
            Assert.AreEqual("Spanish Title Only", result.Item.OriginalTitle); // Spanish title should be used for OriginalTitle
            Assert.AreEqual("English Overview.", result.Item.Overview); // English overview, no Spanish part
            Assert.AreEqual("English Tagline.", result.Item.Tagline);   // English tagline, no Spanish part
        }
    }
}
