this.loadSegments = function (item, done) {
      // метки пропуска: аниме (жанр «Анимация», страна JP/CN/KR) через AniSkip по названию, сериалы TMDB через TheIntroDB по tmdb_id; у провайдера свои метки приоритетнее (json_call.segments)
      var m = object.movie || {};
      if (item.segments || !item.season || !item.episode) return done();
      var anime = (m.genres || []).some(function (g) {
        return g.id == 16;
      }) && (m.origin_country || []).some(function (c) {
        return ['JP', 'CN', 'KR'].indexOf(c) !== -1;
      });
      var tmdb = m.tmdb_id || ((m.source || 'tmdb') == 'tmdb' ? m.id : 0);
      var year = (m.first_air_date || '').slice(0, 4);
      if (anime && item.season > 1) {
        // сезон 2+: отдельная запись MAL, сервер выбирает её по году выхода этого сезона
        var sn = (m.seasons || []).filter(function (s) {
          return s.season_number == item.season;
        })[0];
        if (sn && sn.air_date) year = sn.air_date.slice(0, 4);
        else anime = false;
      }
      if (!anime && !tmdb) return done();
      var net = new Lampa.Reguest();
      net.timeout(4000);
      net.silent(
        account(
          Defined.localhost + 'aniskip?e=' + item.episode + '&s=' + item.season + (tmdb ? '&tmdb_id=' + tmdb : '') + (anime ? '&year=' + (year || 0) + '&title=' + encodeURIComponent(m.name || m.title || '') : '')
        ),
        function (json) {
          if (json && json.skip && json.skip.length) item.segments = json;
          done();
        },
        done
      );
    };
    this.loadPlaylistSegments = function (videos, playlist) {
      // метки для остальных серий плейлиста: по одной, в фоне, чтобы не задерживать старт; playlist[i] соответствует videos[i]
      var _this = this;
      var i = 0;
      (function next() {
        if (i >= videos.length) return;
        var idx = i++;
        _this.loadSegments(videos[idx], function () {
          if (playlist[idx] && videos[idx].segments) playlist[idx].segments = videos[idx].segments;
          next();
        });
      })();
    };
    $1$2
          _this5.loadSegments(item, function () {$3
          });$4