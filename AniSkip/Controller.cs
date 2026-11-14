using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Attributes;
using Shared.Models.Templates;
using Shared.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;

namespace AniSkip;

public class AniSkipController : BaseController
{
    // ponytail: без лимита размера и без TTL, достаточно для личного сервера; ограничить, если вырастет
    static readonly ConcurrentDictionary<string, int> malCache = new();

    /// <summary>
    /// Метки пропуска в формате SegmentTpl ({"ad":[],"skip":[{"start","end"}]}).
    /// Аниме (title + year или mal_id): AniSkip, MAL ID через Shikimori (id Shikimori == MAL id).
    /// Любой сериал (tmdb_id + s): TheIntroDB; для аниме это запасной источник, если AniSkip пуст.
    /// Сбой источника без найденных меток отдаёт 503 (не кэшируется), «меток нет» отдаёт {} (кэшируется).
    /// </summary>
    [HttpGet]
    [Staticache(cacheMinutes: 720, always: true, queryKeys: new[] { "title", "year", "e", "mal_id", "tmdb_id", "s" })]
    [Route("aniskip")]
    async public Task<ActionResult> Index(string title, int year, int e, int mal_id, int tmdb_id, int s)
    {
        bool anime = mal_id > 0 || !string.IsNullOrWhiteSpace(title);

        if (e <= 0 || (!anime && (tmdb_id <= 0 || s <= 0)))
            return BadRequest();

        var segments = new SegmentTpl();
        bool failed = false;

        if (anime && !await FromAniSkip(segments, title, year, e, mal_id))
            failed = true;

        if (segments.IsEmpty && tmdb_id > 0 && s > 0 && !await FromIntroDb(segments, tmdb_id, s, e))
            failed = true;

        if (segments.IsEmpty && failed)
            return StatusCode(503);

        return ContentTo(JsonConvert.SerializeObject(segments.ToObject() ?? new Dictionary<string, IReadOnlyList<SegmentDto>>()), "application/json; charset=utf-8");
    }

    #region FromAniSkip
    // false: сбой сети/разбора; true: ответ получен (меток может не быть)
    async Task<bool> FromAniSkip(SegmentTpl segments, string title, int year, int e, int mal_id)
    {
        int mal = mal_id;

        if (mal <= 0)
        {
            string key = $"{title}:{year}";

            if (!malCache.TryGetValue(key, out mal))
            {
                string shiki = await Http.Get($"https://shikimori.io/api/animes?limit=5&search={HttpUtility.UrlEncode(title)}", timeoutSeconds: 8);
                if (shiki == null)
                    return false;

                try
                {
                    // только tv; при известном годе требуем совпадение года выхода
                    var anime = JArray.Parse(shiki).FirstOrDefault(a =>
                        (string)a["kind"] == "tv" &&
                        (year == 0 || ((string)a["aired_on"])?.StartsWith(year.ToString()) == true));

                    mal = anime == null ? 0 : (int)anime["id"];
                }
                catch (Exception)
                {
                    return false;
                }

                if (mal > 0)
                    malCache[key] = mal;
            }
        }

        if (mal <= 0)
            return true;

        // statusCodeOK: false, потому что «меток нет» AniSkip отдаёт как 404 с телом
        string json = await Http.Get($"https://api.aniskip.com/v2/skip-times/{mal}/{e}?types=op&types=ed&types=recap&episodeLength=0", timeoutSeconds: 8, statusCodeOK: false);
        if (json == null)
            return false;

        try
        {
            var results = JObject.Parse(json)["results"] as JArray;

            // episodeLength=0 возвращает метки от разных рипов; берём первую каждого типа
            foreach (string type in new[] { "op", "ed", "recap" })
            {
                var r = results?.FirstOrDefault(i => (string)i["skipType"] == type);
                if (r != null)
                    segments.skip((int)(double)r["interval"]["startTime"], (int)Math.Ceiling((double)r["interval"]["endTime"]));
            }
        }
        catch (Exception)
        {
            return false;
        }

        return true;
    }
    #endregion

    #region FromIntroDb
    // false: сбой сети/разбора/неожиданный ответ; true: ответ получен (меток может не быть)
    async Task<bool> FromIntroDb(SegmentTpl segments, int tmdb_id, int s, int e)
    {
        string json = await Http.Get($"https://api.theintrodb.org/v4/media?tmdb_id={tmdb_id}&season={s}&episode={e}", timeoutSeconds: 8, statusCodeOK: false);
        if (json == null)
            return false;

        try
        {
            var root = JObject.Parse(json);

            // у успешного ответа всегда есть type; иначе это ошибка (лимит, не найдено и т.п.), её не кэшируем
            if (root["type"] == null)
                return false;

            foreach (string type in new[] { "intro", "recap", "credits" })
            {
                if (root[type] is not JArray arr)
                    continue;

                foreach (var it in arr)
                {
                    long start = it.Value<long?>("start_ms") ?? 0;
                    long? end = it.Value<long?>("end_ms");

                    // ponytail: end_ms = null (титры до конца серии) -> 86400 с; проверить, как плеер Lampa пропускает за пределы длины
                    segments.skip((int)(start / 1000), end.HasValue ? (int)Math.Ceiling(end.Value / 1000.0) : 86400);
                }
            }
        }
        catch (Exception)
        {
            return false;
        }

        return true;
    }
    #endregion
}
