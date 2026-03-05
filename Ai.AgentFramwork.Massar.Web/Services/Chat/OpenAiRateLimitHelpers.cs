using System;
using System.ClientModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

internal static class OpenAiRateLimitHelpers
{
    /// <summary>
    /// Retries an OpenAI call when a 429 is returned. Uses Retry-After header when available,
    /// otherwise exponential backoff with jitter.
    /// </summary>
    public static async Task<T> Retry429Async<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct,
        int maxAttempts = 6)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        if (maxAttempts < 1) maxAttempts = 1;

        var attempt = 0;
        var rnd = new Random();

        while (true)
        {
            attempt++;
            try
            {
                return await action(ct).ConfigureAwait(false);
            }
            catch (ClientResultException ex) when (ex.Status == 429 && attempt < maxAttempts)
            {
                // Default exponential backoff baseline
                var delay = TimeSpan.FromMilliseconds(800 * Math.Pow(2, attempt - 1));

                // Prefer Retry-After header when available
                try
                {
                    var raw = ex.GetRawResponse();
                    if (raw is not null &&
                        raw.Headers.TryGetValue("retry-after", out var ra) &&
                        int.TryParse(ra, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
                        seconds > 0)
                    {
                        delay = TimeSpan.FromSeconds(seconds);
                    }
                }
                catch
                {
                    // ignore header parse issues
                }

                // Add jitter
                delay += TimeSpan.FromMilliseconds(rnd.Next(0, 400));

                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }


    private static TimeSpan ComputeDelay(ClientResultException ex, int attempt, Random rnd, double maxDelaySeconds)
    {
        var delay = TimeSpan.FromMilliseconds(800 * Math.Pow(2, attempt - 1));

        try
        {
            var raw = ex.GetRawResponse();
            if (raw is not null &&
                raw.Headers.TryGetValue("retry-after", out var ra) &&
                !string.IsNullOrWhiteSpace(ra))
            {
                ra = ra.Trim();

                // seconds (int or decimal)
                if (double.TryParse(ra, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                {
                    delay = TimeSpan.FromSeconds(seconds);
                }
                else
                {
                    // HTTP-date
                    if (DateTimeOffset.TryParse(ra, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
                    {
                        var now = DateTimeOffset.UtcNow;
                        var d = when - now;
                        if (d > TimeSpan.Zero)
                            delay = d;
                    }
                }
            }
        }
        catch
        {
            // ignore header parse issues
        }

        delay += TimeSpan.FromMilliseconds(rnd.Next(0, 400));

        var cap = TimeSpan.FromSeconds(maxDelaySeconds);
        if (delay > cap) delay = cap;
        if (delay < TimeSpan.FromMilliseconds(250)) delay = TimeSpan.FromMilliseconds(250);

        return delay;
    }
}
