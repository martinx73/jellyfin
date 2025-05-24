using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using TMDbLib.Objects.Find;
using TMDbLib.Objects.Search;

namespace MediaBrowser.Providers.Plugins.Tmdb.Movies
{
    /// <summary>
    /// Movie provider powered by TMDb.
    /// </summary>
    public class TmdbMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILibraryManager _libraryManager;
        private readonly TmdbClientManager _tmdbClientManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="TmdbMovieProvider"/> class.
        /// </summary>
        /// <param name="libraryManager">The <see cref="ILibraryManager"/>.</param>
        /// <param name="httpClientFactory">The <see cref="IHttpClientFactory"/>.</param>
        /// <param name="tmdbClientManager">The <see cref="TmdbClientManager"/>.</param>
        public TmdbMovieProvider(
            ILibraryManager libraryManager,
            TmdbClientManager tmdbClientManager,
            IHttpClientFactory httpClientFactory)
        {
            _libraryManager = libraryManager;
            _tmdbClientManager = tmdbClientManager;
            _httpClientFactory = httpClientFactory;
        }

        /// <inheritdoc />
        public int Order => 1;

        /// <inheritdoc />
        public string Name => TmdbUtils.ProviderName;

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            if (searchInfo.TryGetProviderId(MetadataProvider.Tmdb, out var id))
            {
                var movie = await _tmdbClientManager
                    .GetMovieAsync(
                        int.Parse(id, CultureInfo.InvariantCulture),
                        searchInfo.MetadataLanguage,
                        TmdbUtils.GetImageLanguagesParam(searchInfo.MetadataLanguage),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (movie is not null)
                {
                    var remoteResult = new RemoteSearchResult
                    {
                        Name = movie.Title ?? movie.OriginalTitle,
                        SearchProviderName = Name,
                        ImageUrl = _tmdbClientManager.GetPosterUrl(movie.PosterPath),
                        Overview = movie.Overview
                    };

                    if (movie.ReleaseDate is not null)
                    {
                        var releaseDate = movie.ReleaseDate.Value.ToUniversalTime();
                        remoteResult.PremiereDate = releaseDate;
                        remoteResult.ProductionYear = releaseDate.Year;
                    }

                    remoteResult.SetProviderId(MetadataProvider.Tmdb, movie.Id.ToString(CultureInfo.InvariantCulture));
                    remoteResult.TrySetProviderId(MetadataProvider.Imdb, movie.ImdbId);

                    return new[] { remoteResult };
                }
            }

            IReadOnlyList<SearchMovie>? movieResults = null;
            if (searchInfo.TryGetProviderId(MetadataProvider.Imdb, out id))
            {
                var result = await _tmdbClientManager.FindByExternalIdAsync(
                    id,
                    FindExternalSource.Imdb,
                    TmdbUtils.GetImageLanguagesParam(searchInfo.MetadataLanguage),
                    cancellationToken).ConfigureAwait(false);
                movieResults = result?.MovieResults;
            }

            if (movieResults is null && searchInfo.TryGetProviderId(MetadataProvider.Tvdb, out id))
            {
                var result = await _tmdbClientManager.FindByExternalIdAsync(
                    id,
                    FindExternalSource.TvDb,
                    TmdbUtils.GetImageLanguagesParam(searchInfo.MetadataLanguage),
                    cancellationToken).ConfigureAwait(false);
                movieResults = result?.MovieResults;
            }

            if (movieResults is null)
            {
                movieResults = await _tmdbClientManager
                    .SearchMovieAsync(searchInfo.Name, searchInfo.Year ?? 0, searchInfo.MetadataLanguage, cancellationToken)
                    .ConfigureAwait(false);
            }

            var len = movieResults.Count;
            var remoteSearchResults = new RemoteSearchResult[len];
            for (var i = 0; i < len; i++)
            {
                var movieResult = movieResults[i];
                var remoteSearchResult = new RemoteSearchResult
                {
                    Name = movieResult.Title ?? movieResult.OriginalTitle,
                    ImageUrl = _tmdbClientManager.GetPosterUrl(movieResult.PosterPath),
                    Overview = movieResult.Overview,
                    SearchProviderName = Name
                };

                var releaseDate = movieResult.ReleaseDate?.ToUniversalTime();
                remoteSearchResult.PremiereDate = releaseDate;
                remoteSearchResult.ProductionYear = releaseDate?.Year;

                remoteSearchResult.SetProviderId(MetadataProvider.Tmdb, movieResult.Id.ToString(CultureInfo.InvariantCulture));
                remoteSearchResults[i] = remoteSearchResult;
            }

            return remoteSearchResults;
        }

        /// <inheritdoc />
        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            var tmdbId = info.GetProviderId(MetadataProvider.Tmdb);
            var imdbId = info.GetProviderId(MetadataProvider.Imdb);

            if (string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(imdbId))
            {
                // ParseName is required here.
                // Caller provides the filename with extension stripped and NOT the parsed filename
                var parsedName = _libraryManager.ParseName(info.Name);
                var cleanedName = TmdbUtils.CleanName(parsedName.Name);
                var searchResults = await _tmdbClientManager.SearchMovieAsync(cleanedName, info.Year ?? parsedName.Year ?? 0, info.MetadataLanguage, cancellationToken).ConfigureAwait(false);

                if (searchResults.Count > 0)
                {
                    tmdbId = searchResults[0].Id.ToString(CultureInfo.InvariantCulture);
                }
            }

            if (string.IsNullOrEmpty(tmdbId) && !string.IsNullOrEmpty(imdbId))
            {
                var movieResultFromImdbId = await _tmdbClientManager.FindByExternalIdAsync(imdbId, FindExternalSource.Imdb, info.MetadataLanguage, cancellationToken).ConfigureAwait(false);
                if (movieResultFromImdbId?.MovieResults.Count > 0)
                {
                    tmdbId = movieResultFromImdbId.MovieResults[0].Id.ToString(CultureInfo.InvariantCulture);
                }
            }

            if (string.IsNullOrEmpty(tmdbId))
            {
                return new MetadataResult<Movie>();
            }

            // Define the list of languages to fetch
            var languagesToFetch = new[] { "en", "es" };
            // Adjust languagesToFetch based on info.MetadataLanguage to avoid redundant primary fetch if it's already "en" or "es"
            // and ensure the primary language is fetched first.
            if (info.MetadataLanguage.IsNormalizedEquals("es"))
            {
                languagesToFetch = new[] { "es", "en" };
            }
            else if (!info.MetadataLanguage.IsNormalizedEquals("en"))
            {
                // If primary is neither "en" nor "es", fetch it first, then "en", then "es".
                // Ensure no duplicates if info.MetadataLanguage is null or empty.
                var initialLang = string.IsNullOrEmpty(info.MetadataLanguage) ? null : info.MetadataLanguage;
                languagesToFetch = new[] { initialLang, "en", "es" }.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray();
            }


            var multilingualMovieData = await _tmdbClientManager
                .GetMovieMultilingualAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), languagesToFetch, TmdbUtils.GetImageLanguagesParam(info.MetadataLanguage), cancellationToken)
                .ConfigureAwait(false);

            if (multilingualMovieData.Count == 0)
            {
                return new MetadataResult<Movie>();
            }

            TMDbLib.Objects.Movies.Movie? primaryMovieData = null;
            string primaryLang = languagesToFetch[0]; // Default to the first requested language

            // Try to get the movie data in the primary requested language
            if (multilingualMovieData.TryGetValue(primaryLang, out var langMovieData))
            {
                primaryMovieData = langMovieData;
            }
            // Fallback logic: Try "en", then "es", then the first available if primary lang data wasn't found (e.g. primary was specific like "de" and not found)
            else if (multilingualMovieData.TryGetValue("en", out var enMovieData))
            {
                primaryMovieData = enMovieData;
                primaryLang = "en";
            }
            else if (multilingualMovieData.TryGetValue("es", out var esMovieData))
            {
                primaryMovieData = esMovieData;
                primaryLang = "es";
            }
            else
            {
                // Fallback to the first successfully fetched language's data
                var firstEntry = multilingualMovieData.FirstOrDefault();
                primaryMovieData = firstEntry.Value;
                primaryLang = firstEntry.Key;
            }

            if (primaryMovieData == null)
            {
                return new MetadataResult<Movie>();
            }

            var movie = new Movie
            {
                Name = primaryMovieData.Title ?? primaryMovieData.OriginalTitle,
                // Overview and Tagline will be set based on primary and secondary language data below
                ProductionLocations = primaryMovieData.ProductionCountries.Select(pc => pc.Name).ToArray()
            };
            var metadataResult = new MetadataResult<Movie>
            {
                HasMetadata = true,
                ResultLanguage = primaryLang, // Reflect the language of the primary data used
                Item = movie
            };

            // Set OriginalTitle based on language availability
            if (primaryLang.IsNormalizedEquals("en") && multilingualMovieData.TryGetValue("es", out var esMovieDataForTitle))
            {
                movie.OriginalTitle = esMovieDataForTitle.Title ?? esMovieDataForTitle.OriginalTitle;
            }
            else if (primaryLang.IsNormalizedEquals("es") && multilingualMovieData.TryGetValue("en", out var enMovieDataForTitle))
            {
                movie.OriginalTitle = enMovieDataForTitle.Title ?? enMovieDataForTitle.OriginalTitle;
            }
            else
            {
                // Fallback to the original title from the primary data if the other language is not available or primary is neither en/es
                movie.OriginalTitle = primaryMovieData.OriginalTitle;
            }


            // Overview Handling
            movie.Overview = primaryMovieData.Overview?.Replace("\n\n", "\n", StringComparison.InvariantCulture);
            TMDbLib.Objects.Movies.Movie? esMovieData = null;
            if (multilingualMovieData.TryGetValue("es", out var esDataFromDict) && esDataFromDict != primaryMovieData)
            {
                esMovieData = esDataFromDict;
            }

            if (esMovieData?.Overview != null && !string.IsNullOrEmpty(esMovieData.Overview))
            {
                var cleanedEsOverview = esMovieData.Overview.Replace("\n\n", "\n", StringComparison.InvariantCulture);
                if (!string.IsNullOrEmpty(movie.Overview))
                {
                    movie.Overview += "\n[JFLANG:ES]\n" + cleanedEsOverview;
                }
                else
                {
                    // If primary overview is empty, we can either set it directly to Spanish
                    // or prefix it to ensure DTO service parsing logic.
                    // For now, let's assume primary is preferred and DTO handles missing primary.
                    // For consistency of combined field, if primary is empty, Spanish effectively becomes primary if DTO logic is simple,
                    // or use the prefix to force it into the Spanish slot.
                    // Based on DtoService, if movie.Overview is just spanish, it will be dto.Overview.
                    // To ensure it goes to dto.OverviewEs, we need the prefix if primary is empty.
                    movie.Overview = "\n[JFLANG:ES]\n" + cleanedEsOverview;
                }
            }

            // Tagline Handling - REVISED as per specific instructions
            string englishTagline = multilingualMovieData.TryGetValue("en", out var enData) && enData != null ? enData.Tagline : null;
            string spanishTagline = multilingualMovieData.TryGetValue("es", out var esData) && esData != null ? esData.Tagline : null;

            if (!string.IsNullOrEmpty(englishTagline) && !string.IsNullOrEmpty(spanishTagline)) {
                movie.Tagline = englishTagline + "[JFLANG:ES]" + spanishTagline;
            } else if (!string.IsNullOrEmpty(spanishTagline)) {
                // English is empty, Spanish is present
                movie.Tagline = "[JFLANG:ES]" + spanishTagline;
            } else {
                // English is present (or both empty), Spanish is empty (or both empty)
                // This correctly assigns englishTagline if present, or null/empty if both are null/empty.
                movie.Tagline = englishTagline;
            }

            // metadataResult.AdditionalData is no longer used for esMovieData
            // Ensure metadataResult.AdditionalData dictionary is not created if not needed for other purposes later.
            // For now, removing all related AdditionalData logic for esMovieData.

            movie.SetProviderId(MetadataProvider.Tmdb, tmdbId);
            movie.TrySetProviderId(MetadataProvider.Imdb, primaryMovieData.ImdbId);
            if (primaryMovieData.BelongsToCollection is not null)
            {
                movie.SetProviderId(MetadataProvider.TmdbCollection, primaryMovieData.BelongsToCollection.Id.ToString(CultureInfo.InvariantCulture));
                movie.CollectionName = primaryMovieData.BelongsToCollection.Name;
            }

            movie.CommunityRating = Convert.ToSingle(primaryMovieData.VoteAverage);

            if (primaryMovieData.Releases?.Countries is not null)
            {
                var releases = primaryMovieData.Releases.Countries.Where(i => !string.IsNullOrWhiteSpace(i.Certification)).ToList();

                var ourRelease = releases.FirstOrDefault(c => string.Equals(c.Iso_3166_1, info.MetadataCountryCode, StringComparison.OrdinalIgnoreCase));
                // Use primaryLang for the country specific release if MetadataCountryCode is not available
                var langSpecificRelease = releases.FirstOrDefault(c => string.Equals(c.Iso_3166_1, primaryLang.ToUpperInvariant(), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(primaryLang));
                var usRelease = releases.FirstOrDefault(c => string.Equals(c.Iso_3166_1, "US", StringComparison.OrdinalIgnoreCase));


                if (ourRelease is not null)
                {
                    movie.OfficialRating = TmdbUtils.BuildParentalRating(ourRelease.Iso_3166_1, ourRelease.Certification);
                }
                else if (langSpecificRelease is not null) // Try to get rating for the primary language country
                {
                    movie.OfficialRating = TmdbUtils.BuildParentalRating(langSpecificRelease.Iso_3166_1, langSpecificRelease.Certification);
                }
                else if (usRelease is not null)
                {
                    movie.OfficialRating = usRelease.Certification;
                }
            }

            movie.PremiereDate = primaryMovieData.ReleaseDate;
            movie.ProductionYear = primaryMovieData.ReleaseDate?.Year;

            if (primaryMovieData.ProductionCompanies is not null)
            {
                movie.SetStudios(primaryMovieData.ProductionCompanies.Select(c => c.Name));
            }

            var genres = primaryMovieData.Genres;

            foreach (var genre in genres.Select(g => g.Name).Trimmed())
            {
                movie.AddGenre(genre);
            }

            if (primaryMovieData.Keywords?.Keywords is not null)
            {
                for (var i = 0; i < primaryMovieData.Keywords.Keywords.Count; i++)
                {
                    movie.AddTag(primaryMovieData.Keywords.Keywords[i].Name);
                }
            }

            if (primaryMovieData.Credits?.Cast is not null)
            {
                foreach (var actor in primaryMovieData.Credits.Cast.OrderBy(a => a.Order).Take(Plugin.Instance.Configuration.MaxCastMembers))
                {
                    var personInfo = new PersonInfo
                    {
                        Name = actor.Name.Trim(),
                        Role = actor.Character.Trim(),
                        Type = PersonKind.Actor,
                        SortOrder = actor.Order
                    };

                    if (!string.IsNullOrWhiteSpace(actor.ProfilePath))
                    {
                        personInfo.ImageUrl = _tmdbClientManager.GetProfileUrl(actor.ProfilePath);
                    }

                    if (actor.Id > 0)
                    {
                        personInfo.SetProviderId(MetadataProvider.Tmdb, actor.Id.ToString(CultureInfo.InvariantCulture));
                    }

                    metadataResult.AddPerson(personInfo);
                }
            }

            if (primaryMovieData.Credits?.Crew is not null)
            {
                foreach (var person in primaryMovieData.Credits.Crew)
                {
                    // Normalize this
                    var type = TmdbUtils.MapCrewToPersonType(person);

                    if (!TmdbUtils.WantedCrewKinds.Contains(type)
                        && !TmdbUtils.WantedCrewTypes.Contains(person.Job ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var personInfo = new PersonInfo
                    {
                        Name = person.Name.Trim(),
                        Role = person.Job?.Trim(),
                        Type = type
                    };

                    if (!string.IsNullOrWhiteSpace(person.ProfilePath))
                    {
                        personInfo.ImageUrl = _tmdbClientManager.GetProfileUrl(person.ProfilePath);
                    }

                    if (person.Id > 0)
                    {
                        personInfo.SetProviderId(MetadataProvider.Tmdb, person.Id.ToString(CultureInfo.InvariantCulture));
                    }

                    metadataResult.AddPerson(personInfo);
                }
            }

            if (primaryMovieData.Videos?.Results is not null)
            {
                var trailers = new List<MediaUrl>();
                for (var i = 0; i < primaryMovieData.Videos.Results.Count; i++)
                {
                    var video = primaryMovieData.Videos.Results[i];
                    if (!TmdbUtils.IsTrailerType(video))
                    {
                        continue;
                    }

                    trailers.Add(new MediaUrl
                    {
                        Url = string.Format(CultureInfo.InvariantCulture, "https://www.youtube.com/watch?v={0}", video.Key),
                        Name = video.Name
                    });
                }

                movie.RemoteTrailers = trailers;
            }

            return metadataResult;
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken);
        }
    }
}
