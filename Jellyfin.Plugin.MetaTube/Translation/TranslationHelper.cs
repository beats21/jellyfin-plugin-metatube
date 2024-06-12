using System.Collections.Specialized;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Metadata;

namespace Jellyfin.Plugin.MetaTube.Translation;

public static class TranslationHelper
{
    private const string AutoLanguageCode = "auto";
    private const string JapaneseLanguageCode = "ja";

    private static readonly SemaphoreSlim Semaphore = new(1);

    private static PluginConfiguration Configuration => Plugin.Instance.Configuration;

    private static async Task<string> TranslateAsync(string q, string from, string to,
        CancellationToken cancellationToken)
    {
        int millisecondsDelay;
        var nv = new NameValueCollection();
        switch (Configuration.TranslationEngine)
        {
            case TranslationEngine.Baidu:
                millisecondsDelay = 1000; // Limit Baidu API request rate to 1 rps.
                nv.Add(new NameValueCollection
                {
                    { "baidu-app-id", Configuration.BaiduAppId },
                    { "baidu-app-key", Configuration.BaiduAppKey }
                });
                break;
            case TranslationEngine.Google:
                millisecondsDelay = 100; // Limit Google API request rate to 10 rps.
                nv.Add(new NameValueCollection
                {
                    { "google-api-key", Configuration.GoogleApiKey }
                });
                break;
            case TranslationEngine.GoogleFree:
                millisecondsDelay = 100;
                nv.Add(new NameValueCollection());
                break;
            case TranslationEngine.DeepL:
                millisecondsDelay = 100;
                nv.Add(new NameValueCollection
                {
                    { "deepl-api-key", Configuration.DeepLApiKey }
                });
                break;
            case TranslationEngine.OpenAi:
                millisecondsDelay = 1000;
                nv.Add(new NameValueCollection
                {
                    { "openai-api-key", Configuration.OpenAiApiKey }
                });
                break;
            default:
                throw new ArgumentException($"Invalid translation engine: {Configuration.TranslationEngine}");
        }

        await Semaphore.WaitAsync(cancellationToken);

        try
        {
            async Task<string> TranslateWithDelay()
            {
                await Task.Delay(millisecondsDelay, cancellationToken);
                return (await ApiClient
                    .TranslateAsync(q, from, to, Configuration.TranslationEngine.ToString(), nv, cancellationToken)
                    .ConfigureAwait(false)).TranslatedText;
            }

            return await RetryAsync(TranslateWithDelay, 5);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public static async Task TranslateAsync(MovieInfo m, string to, CancellationToken cancellationToken)
    {
        if (string.Equals(to, JapaneseLanguageCode, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"language not allowed: {to}");

        if (Configuration.TranslationMode.HasFlag(TranslationMode.Title) && !string.IsNullOrWhiteSpace(m.Title))
            m.Title = await TranslateAsync(m.Title, AutoLanguageCode, to, cancellationToken);

        if (Configuration.TranslationMode.HasFlag(TranslationMode.Summary) && !string.IsNullOrWhiteSpace(m.Summary))
            m.Summary = await TranslateAsync(m.Summary, AutoLanguageCode, to, cancellationToken);

        // custom translations
        if (!string.IsNullOrWhiteSpace(m.Director))
            m.Director = await TranslateAsync(m.Director, AutoLanguageCode, to, cancellationToken);

        if (m.Genres != null && m.Genres.Length > 0)
        {
            for (int i = 0; i < m.Genres.Length; i++)
            {
                m.Genres[i] = await TranslateAsync(m.Genres[i], AutoLanguageCode, to, cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(m.Maker))
            m.Maker = await TranslateAsync(m.Maker, AutoLanguageCode, to, cancellationToken);

        if (!string.IsNullOrWhiteSpace(m.Label))
            m.Label = await TranslateAsync(m.Label, AutoLanguageCode, to, cancellationToken);

        if (!string.IsNullOrWhiteSpace(m.Series))
            m.Series = await TranslateAsync(m.Series, AutoLanguageCode, to, cancellationToken);

        // if (m.Actors != null && m.Actors.Length > 0)
        // {
        //     for (int i = 0; i < m.Actors.Length; i++)
        //     {
        //         m.Actors[i] = await TranslateAsync(m.Actors[i], AutoLanguageCode, to, cancellationToken);
        //     }
        // }

    }

    public static async Task TranslateAsync(ActorName actorName, string to, CancellationToken cancellationToken)
    {
        if (string.Equals(to, JapaneseLanguageCode, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"language not allowed: {to}");
        if (!string.IsNullOrWhiteSpace(actorName.Name))
            actorName.Name = await TranslateAsync(actorName.Name, AutoLanguageCode, to, cancellationToken);
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> func, int retryCount)
    {
        while (true)
        {
            try
            {
                return await func();
            }
            catch when (--retryCount > 0)
            {
            }
        }
    }
}