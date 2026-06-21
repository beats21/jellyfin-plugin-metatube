using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
#if __EMBY__
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;

#else
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube.Providers;

public class MovieImageProvider : BaseProvider, IRemoteImageProvider, IHasOrder
{
#if __EMBY__
    public MovieImageProvider(ILogManager logManager) : base(logManager.CreateLogger<MovieImageProvider>())
#else
    public MovieImageProvider(ILogger<MovieImageProvider> logger) : base(logger)
#endif
    {
    }

#if __EMBY__
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, LibraryOptions libraryOptions,
        CancellationToken cancellationToken)
#else
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
#endif
    {
        var pid = item.GetPid(Plugin.ProviderId);
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
            return Enumerable.Empty<RemoteImageInfo>();

        // Fetch direct CDN image URLs from the companion.
        Metadata.MovieInfoMod m;
        try
        {
            m = await ApiClient.GetLocalMovieInfoAsync(pid.Id, pid.Provider, pid.Id, cancellationToken);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Failed to get movie info for images: {0}", pid.ToString());
            return Enumerable.Empty<RemoteImageInfo>();
        }

        // The companion reports `metatube` as its source but serves DMM (FANZA)
        // covers. Route them through the upstream image proxy under the FANZA
        // crop ruleset (the proxy fetches the supplied `url` and the id is
        // ignored when a url is given) so the wide cover is cropped/face-centered
        // to the front poster — the same positioning the original provider used.
        const string cropProvider = "FANZA";
        var position = pid.Position ?? -1;

        var images = new List<RemoteImageInfo>();

        // Cover: primary is auto-cropped to the front poster; thumb/backdrop keep the full cover.
        if (!string.IsNullOrWhiteSpace(m.CoverUrl))
        {
            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Primary,
                Url = ApiClient.GetPrimaryImageApiUrl(cropProvider, pid.Id, m.CoverUrl, position, auto: true)
            });
            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Thumb,
                Url = ApiClient.GetThumbImageApiUrl(cropProvider, pid.Id, m.CoverUrl)
            });
            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Backdrop,
                Url = ApiClient.GetBackdropImageApiUrl(cropProvider, pid.Id, m.CoverUrl)
            });
        }

        // Sample/preview images as additional primary + backdrop candidates.
        foreach (var imageUrl in m.PreviewImages ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(imageUrl)) continue;

            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Primary,
                Url = ApiClient.GetPrimaryImageApiUrl(cropProvider, pid.Id, imageUrl, position, auto: true)
            });
            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Backdrop,
                Url = ApiClient.GetBackdropImageApiUrl(cropProvider, pid.Id, imageUrl)
            });
        }

        return images;
    }

    public bool Supports(BaseItem item)
    {
        return item is Movie;
    }

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return new List<ImageType>
        {
            ImageType.Primary,
            ImageType.Thumb,
            ImageType.Backdrop
        };
    }
}