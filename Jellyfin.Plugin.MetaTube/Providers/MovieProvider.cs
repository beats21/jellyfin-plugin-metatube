using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Metadata;
using Jellyfin.Plugin.MetaTube.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using MovieInfo = MediaBrowser.Controller.Providers.MovieInfo;
#if __EMBY__
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

#else
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube.Providers;

#if __EMBY__
public class MovieProvider : BaseProvider, IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder, IHasMetadataFeatures
#else
public class MovieProvider : BaseProvider, IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
#endif
{
    private const string AvBase = "AVBASE";
    private const string Gfriends = "Gfriends";
    private const string Rating = "JP-18+";

    private static readonly string[] AvBaseSupportedProviderNames = { "DUGA", "FANZA", "Getchu", "MGS" };

#if __EMBY__
    public MetadataFeatures[] Features => new[]
        { MetadataFeatures.Collections, MetadataFeatures.Adult, MetadataFeatures.RequiredSetup };

    public MovieProvider(ILogManager logManager) : base(logManager.CreateLogger<MovieProvider>())
#else
    public MovieProvider(ILogger<MovieProvider> logger) : base(logger)
#endif
    {
    }

    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info,
        CancellationToken cancellationToken)
    {
        var pid = info.GetPid(Plugin.ProviderId);

        // The companion is keyed by movie code. Prefer the stored provider id
        // (set on a prior scrape) and only fall back to the parsed file name on
        // initial identify — info.Name gets replaced by the full title after the
        // first successful scrape, which would no longer match a code. Capture it
        // before the search block below can overwrite pid.
        var code = !string.IsNullOrWhiteSpace(pid.Id) ? pid.Id : info.Name;

        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
        {
            // Search movies and pick the first result.
            var firstResult = (await GetSearchResults(info, cancellationToken)).FirstOrDefault();
            if (firstResult != null) pid = firstResult.GetPid(Plugin.ProviderId);
        }

        var m = await ApiClient.GetLocalMovieInfoAsync(code, pid.Provider, pid.Id, cancellationToken);

        // The companion prefixes the title with the code; strip it so the
        // "{number} {title}" template doesn't render the code twice.
        if (!string.IsNullOrWhiteSpace(m.Number) && !string.IsNullOrWhiteSpace(m.Title)
            && m.Title.StartsWith(m.Number, StringComparison.OrdinalIgnoreCase))
            m.Title = m.Title.Substring(m.Number.Length).TrimStart(' ', '-', ':', '\t');

        // Preserve original title (template rendering replaces Name below).
        var originalTitle = m.Title;

        // Convert to real actor names — fallback only.
        // The companion supplies actors via ActorsDict; only reach out to AVBASE when it is empty.
        if (Configuration.EnableRealActorNames && m.ActorsDict?.Any() != true)
            await ConvertToRealActorNames(m, cancellationToken);

        // Translate movie info (title/summary/director/genres/maker/label/series).
        if (Configuration.TranslationMode != TranslationMode.Disabled)
            await TranslateMovieInfo(m, info.MetadataLanguage, cancellationToken);

        // Distinct and clean blank list (genres only; actors handled via ActorsDict)
        m.Genres = m.Genres?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray() ?? Array.Empty<string>();

        // Actor source for templates: prefer companion English names, fall back to AVBASE list.
        var templateActors = m.ActorsDict?.Values.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (templateActors == null || templateActors.Length == 0)
            templateActors = m.Actors?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? Array.Empty<string>();

        // Build template parameters.
        var parameters = new Dictionary<string, string>
        {
            { @"{provider}", m.Provider },
            { @"{id}", m.Id },
            { @"{number}", m.Number },
            { @"{title}", m.Title },
            { @"{series}", m.Series },
            { @"{maker}", m.Maker },
            { @"{label}", m.Label },
            { @"{director}", m.Director },
            { @"{actors}", templateActors.Any() ? string.Join(' ', templateActors) : string.Empty },
            { @"{first_actor}", templateActors.FirstOrDefault() ?? string.Empty },
            { @"{year}", $"{m.ReleaseDate:yyyy}" },
            { @"{month}", $"{m.ReleaseDate:MM}" },
            { @"{date}", $"{m.ReleaseDate:yyyy-MM-dd}" }
        };

        var name = RenderTemplate(
            Configuration.EnableTemplate ? Configuration.NameTemplate : PluginConfiguration.DefaultNameTemplate,
            parameters);
        if (string.IsNullOrWhiteSpace(name)) name = m.Title;

        var tagline = RenderTemplate(
            Configuration.EnableTemplate ? Configuration.TaglineTemplate : PluginConfiguration.DefaultTaglineTemplate,
            parameters);

        var new_genres = m.Genres?.Any() == true ? m.Genres : Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(m.Maker))
            new_genres = new_genres.Append($"M- {m.Maker}").ToArray<string>();


        var result = new MetadataResult<Movie>
        {
            Item = new Movie
            {
                Name = name,
                Tagline = tagline,
                OriginalTitle = originalTitle,
                Overview = m.Summary,
                OfficialRating = m.Rating,
                PremiereDate = m.ReleaseDate.GetValidDateTime(),
                ProductionYear = m.ReleaseDate.GetValidYear(),
                Genres = new_genres
            },
            HasMetadata = true
        };

        // Set provider id.
        result.Item.SetPid(Name, m.Provider, m.Id, pid.Position);

        // Set trailer url.
        var trailerUrl = !string.IsNullOrWhiteSpace(m.PreviewVideoUrl)
            ? m.PreviewVideoUrl
            : m.PreviewVideoHlsUrl;
        if (!string.IsNullOrWhiteSpace(trailerUrl))
            result.Item.SetTrailerUrl(trailerUrl);

        // Set community rating.
        if (float.TryParse(m.Rating, out var ratingVal) && ratingVal > 0)
            result.Item.CommunityRating = ratingVal;

        // Add collection.
        foreach (var i in m.Genres)
        {
            result.Item.AddCollection(i);
            result.Item.AddTag(i);
        }

        // Add actors to collections.
        foreach (var item in m.ActorsDict?.Values ?? Enumerable.Empty<string>())
        {
            result.Item.AddCollection($"A- {item}");
        }

        // Add studio.
        if (!string.IsNullOrWhiteSpace(m.Maker))
        {
            result.Item.AddStudio($"M- {m.Maker}");
            result.Item.AddTag($"M- {m.Maker}");
            result.Item.AddCollection($"M- {m.Maker}");
        }

        // Add studio.
        if (!string.IsNullOrWhiteSpace(m.Label))
        {
            result.Item.AddTag($"L- {m.Label}");
            result.Item.AddCollection($"L- {m.Label}");
        }

        // Add studio.
        if (!string.IsNullOrWhiteSpace(m.Series))
        {
            result.Item.AddTag($"S- {m.Series}");
            result.Item.AddCollection($"S- {m.Series}"); ;
        }


        // Add director.
        if (!string.IsNullOrWhiteSpace(m.Director))
        {
            result.AddPerson(new PersonInfo
            {
                Name = m.Director,
                Type = PersonKind.Director
            });
        }

        // Add actors.
        var actorTasks = new List<Task>();
        if (m.ActorsDict?.Any() == true)
        {
            // Companion path: English name for display, Japanese key for image/API search.
            foreach (var item in m.ActorsDict.ToArray())
            {
                var actor = new PersonInfo
                {
                    Name = item.Value, // English name for display
                    Type = PersonKind.Actor,
                };
                // Store Japanese name in ProviderId for API search
                actor.SetPid(Name, item.Key, item.Key);

                // Create task for image fetching
                var actorTask = SetActorImageUrl(actor, item.Key, cancellationToken);
                actorTasks.Add(actorTask);

                result.AddPerson(actor);
            }
        }
        else
        {
            // Fallback path: no ActorsDict (e.g. AVBASE real names); search images by the name itself.
            foreach (var actorName in m.Actors ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(actorName)) continue;

                var actor = new PersonInfo
                {
                    Name = actorName,
                    Type = PersonKind.Actor,
                };

                var actorTask = SetActorImageUrl(actor, actorName, cancellationToken);
                actorTasks.Add(actorTask);

                result.AddPerson(actor);
            }
        }

        // Wait for all actor image tasks to complete
        await Task.WhenAll(actorTasks);

        return result;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo info,
        CancellationToken cancellationToken)
    {
        var pid = info.GetPid(Plugin.ProviderId);

        var searchResults = new List<MovieSearchResult>();
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
        {
            // Search movie by name.
            Logger.Info("Search for movie: {0}", info.Name);
            searchResults.AddRange(await ApiClient.SearchMovieAsync(info.Name, pid.Provider, cancellationToken));
        }
        else
        {
            // Exact search.
            Logger.Info("Search for movie: {0}", pid.ToString());
            searchResults.Add(await ApiClient.GetMovieInfoAsync(pid.Provider, pid.Id,
                pid.Update != true, cancellationToken));
        }

        if (Configuration.EnableMovieProviderFilter)
        {
            if (Configuration.GetMovieProviderFilter() is { } filter &&
                filter.Any()) // Apply only if filter is not empty.
            {
                // Filter out mismatched results.
                searchResults.RemoveAll(m => !filter.Contains(m.Provider, StringComparer.OrdinalIgnoreCase));
                // Reorder results by stable sort.
                searchResults = searchResults.OrderBy(m =>
                    filter.FindIndex(s => s.Equals(m.Provider, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            else
            {
                Logger.Warn("Movie provider filter enabled but never used");
            }
        }

        var results = new List<RemoteSearchResult>();
        if (!searchResults.Any())
        {
            Logger.Warn("Movie not found or has been filtered: {0}", pid.Id);
            return results;
        }

        foreach (var m in searchResults)
        {
            var result = new RemoteSearchResult
            {
                Name = $"[{m.Provider}] {m.Number} {m.Title}",
                SearchProviderName = Name,
                PremiereDate = m.ReleaseDate.GetValidDateTime(),
                ProductionYear = m.ReleaseDate.GetValidYear(),
                ImageUrl = ApiClient.GetPrimaryImageApiUrl(m.Provider, m.Id, m.ThumbUrl, 1.0, true)
            };
            result.SetPid(Name, m.Provider, m.Id, pid.Position);
            results.Add(result);
        }

        return results;
    }

    private async Task SetActorImageUrl(PersonInfo actor, string actor_name, CancellationToken cancellationToken)
    {
        try
        {
            var results = await ApiClient.SearchActorAsync(actor_name, cancellationToken);
            if (results?.Any() != true)
            {
                Logger.Warn("Actor not found: {0}: {1}", actor.Name, actor_name);
                return;
            }

            // Use the first result as the primary actor selection.
            var firstResult = results.First();
            if (firstResult.Images?.Any() == true)
            {
                actor.ImageUrl = ApiClient.GetPrimaryImageApiUrl(
                    firstResult.Provider, firstResult.Id, firstResult.Images.First(), 0.5, true);
                // Store under the plugin's provider key (not the search term) so
                // ActorProvider re-fetches with a valid provider:id, not the JP name.
                actor.SetPid(Name, firstResult.Provider, firstResult.Id);
            }

            // Use the Gfriends to update the actor profile image, if any.
            foreach (var result in results.Where(result => result.Provider == Gfriends && result.Images?.Any() == true))
            {
                actor.ImageUrl = ApiClient.GetPrimaryImageApiUrl(
                    result.Provider, result.Id, result.Images.First(), 0.5, true);
                actor.SetPid(Name, result.Provider, result.Id);
            }
        }
        catch (Exception e)
        {
            // Actor absent from the image source (404) is an expected data gap, not a fault.
            if (e.Message.Contains("404"))
                Logger.Warn("Actor image not found: {0} ({1})", actor.Name, actor_name);
            else
                Logger.Error("Get actor image error: {0} ({1})", actor.Name, e.Message);
        }
    }

    private async Task ConvertToRealActorNames(MovieSearchResult m, CancellationToken cancellationToken)
    {
        if (!AvBaseSupportedProviderNames.Contains(m.Provider, StringComparer.OrdinalIgnoreCase)) return;

        try
        {
            var searchResults = await ApiClient.SearchMovieAsync(m.Id, AvBase, cancellationToken);
            if (searchResults?.Any() != true)
            {
                Logger.Warn("Movie not found on AVBASE: {0}", m.Id);
                return;
            }

            foreach (var result in searchResults)
            {
                var similarity = CalculateTitleSimilarity(m, result);

                Logger.Info("Calculate movie title similarity for {0} ({1}) and {2} ({3}): {4:0.00%}",
                    m.Id, m.Provider, result.Id, result.Provider, similarity);

                if (similarity >= 0.8)
                {
                    if (result.Actors?.Any() == true)
                        m.Actors = result.Actors;
                    return;
                }
            }

            Logger.Warn("No matching movie found on AVBASE for {0}", m.Id);
        }
        catch (Exception e)
        {
            Logger.Error("Convert to real actor names error: {0} ({1})", m.Number, e.Message);
        }
    }

    private static double CalculateTitleSimilarity(MovieSearchResult source, MovieSearchResult target)
    {
        var sourceKey = Normalize(source.Number + source.Title);
        var targetKey = Normalize(target.Number + target.Title);

        if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(targetKey))
            return 0.0;

        var distance = Levenshtein.Distance(sourceKey, targetKey);
        var avgLength = (sourceKey.Length + targetKey.Length) / 2.0;
        var similarity = 1.0 - distance / avgLength;

        return Math.Clamp(similarity, 0.0, 1.0);

        string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return string.Empty;

            s = s.ToLowerInvariant();
            s = Regex.Replace(s, @"[\s\[\]\(\)【】（）]", "");
            return s.Trim();
        }
    }

    private async Task TranslateMovieInfo(Metadata.MovieInfo m, string language, CancellationToken cancellationToken)
    {
        try
        {
            Logger.Info("Translate movie info language: {0} => {1}", m.Number, language);
            await TranslationHelper.TranslateAsync(m, language, cancellationToken);
        }
        catch (Exception e)
        {
            Logger.Error("Translate error: {0}", e.Message);
        }
    }

    private static string RenderTemplate(string template, Dictionary<string, string> parameters)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        var sb = parameters.Where(kvp => template.Contains(kvp.Key))
            .Aggregate(new StringBuilder(template),
                (sb, kvp) => sb.Replace(kvp.Key, kvp.Value));

        return sb.ToString().Trim();
    }
}