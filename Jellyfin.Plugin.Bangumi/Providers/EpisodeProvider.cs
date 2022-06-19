﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bangumi.Providers
{
    public class EpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
    {
        private static readonly Regex[] NonEpisodeFileNameRegex =
        {
            new(@"S\d{2,}"),
            new(@"\d{3,4}p"),
            new(@"(Hi)?10p"),
            new(@"(8|10)bit"),
            new(@"(x|h)(264|265)")
        };

        private static readonly Regex[] EpisodeFileNameRegex =
        {
            new(@"\[(\d{2,})\]"),
            new(@"- ?(\d{2,})"),
            new(@"EP?(\d{2,})"),
            new(@"\[(\d{2,})"),
            new(@"(\d{2,})")
        };

        private static readonly Regex[] SpecialEpisodeFileNameRegex = { new("Special"), new("OVA"), new("OAD") };
        private readonly BangumiApi _api;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<EpisodeProvider> _log;

        private readonly Plugin _plugin;

        public EpisodeProvider(Plugin plugin, BangumiApi api, ILogger<EpisodeProvider> log, ILibraryManager libraryManager)
        {
            _plugin = plugin;
            _api = api;
            _log = log;
            _libraryManager = libraryManager;
        }

        public int Order => -5;
        public string Name => Constants.ProviderName;

        public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Model.Episode? episode = null;
            var result = new MetadataResult<Episode> { ResultLanguage = Constants.Language };

            var fileName = Path.GetFileName(info.Path);
            if (string.IsNullOrEmpty(fileName))
                return result;

            var seriesId = info.SeriesProviderIds?.GetValueOrDefault(Constants.ProviderName);

            var parent = _libraryManager.FindByPath(Path.GetDirectoryName(info.Path), true);
            if (parent is Season)
            {
                var seasonId = parent.ProviderIds.GetValueOrDefault(Constants.ProviderName);
                if (!string.IsNullOrEmpty(seasonId))
                    seriesId = seasonId;
            }

            if (string.IsNullOrEmpty(seriesId))
                return result;

            var episodeId = info.ProviderIds?.GetValueOrDefault(Constants.ProviderName);
            if (!string.IsNullOrEmpty(episodeId))
            {
                episode = await _api.GetEpisode(episodeId, token);
                if (episode != null)
                    if (!SpecialEpisodeFileNameRegex.Any(x => x.IsMatch(info.Path)))
                        if ($"{episode.ParentId}" != seriesId)
                        {
                            _log.LogWarning("episode #{Episode} is not belong to series #{Series}, ignored", episodeId, seriesId);
                            episode = null;
                        }
            }

            var episodeIndex = info.IndexNumber;

            if (_plugin.Configuration.AlwaysReplaceEpisodeNumber)
            {
                episodeIndex = GuessEpisodeNumber(episodeIndex, fileName);
                if (episodeIndex != info.IndexNumber)
                    episode = null;
            }

            episodeIndex ??= GuessEpisodeNumber(episodeIndex, fileName);

            if (episode == null)
            {
                var episodeListData = await _api.GetSubjectEpisodeList(seriesId, episodeIndex.Value, token);
                if (episodeListData == null)
                    return result;
                episodeIndex = GuessEpisodeNumber(episodeIndex, fileName, episodeListData.Max(episode => episode.Order));
                episode = episodeListData.Find(x => (int)x.Order == episodeIndex);
            }

            if (episode == null)
                return result;

            result.Item = new Episode();
            result.HasMetadata = true;
            result.Item.ProviderIds.Add(Constants.ProviderName, $"{episode.Id}");
            if (!string.IsNullOrEmpty(episode.AirDate))
            {
                result.Item.PremiereDate = DateTime.Parse(episode.AirDate);
                result.Item.ProductionYear = DateTime.Parse(episode.AirDate).Year;
            }

            result.Item.Name = episode.GetName(_plugin.Configuration);
            result.Item.OriginalTitle = episode.OriginalName;
            result.Item.IndexNumber = (int)episode.Order;
            result.Item.Overview = episode.Description;

            if (parent is Season season)
            {
                result.Item.SeasonId = season.Id;
                result.Item.ParentIndexNumber = season.IndexNumber;
            }

            return result;
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken token)
        {
            return _plugin.GetHttpClient().GetAsync(url, token);
        }

        private int GuessEpisodeNumber(int? current, string fileName, double max = double.PositiveInfinity)
        {
            var tempName = fileName;
            var episodeIndex = current ?? 0;
            var episodeIndexFromFilename = episodeIndex;

            foreach (var regex in NonEpisodeFileNameRegex)
            {
                if (!regex.IsMatch(tempName))
                    continue;
                tempName = regex.Replace(tempName, "");
            }

            foreach (var regex in EpisodeFileNameRegex)
            {
                if (!regex.IsMatch(tempName))
                    continue;
                episodeIndexFromFilename = int.Parse(regex.Match(tempName).Groups[1].Value);
                break;
            }

            if (_plugin.Configuration.AlwaysReplaceEpisodeNumber && episodeIndexFromFilename != episodeIndex)
            {
                _log.LogWarning("use episode index {NewIndex} instead of {Index} for {FileName}",
                    episodeIndexFromFilename, episodeIndex, fileName);
                return episodeIndexFromFilename;
            }

            if (episodeIndex > max)
            {
                _log.LogWarning("file {FileName} has incorrect episode index {Index}, set to {NewIndex}",
                    fileName, episodeIndex, episodeIndexFromFilename);
                return episodeIndexFromFilename;
            }

            if (episodeIndexFromFilename > 0 && episodeIndex <= 0)
            {
                _log.LogWarning("file {FileName} may has incorrect episode index {Index}, should be {NewIndex}",
                    fileName, episodeIndex, episodeIndexFromFilename);
                return episodeIndexFromFilename;
            }

            _log.LogInformation("use exists episode number {Index} from file name {FileName}", episodeIndex, fileName);
            return episodeIndex;
        }
    }
}