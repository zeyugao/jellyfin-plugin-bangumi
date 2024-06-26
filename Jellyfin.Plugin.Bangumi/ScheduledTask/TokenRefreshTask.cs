﻿
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bangumi.OAuth;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Tasks;
#if EMBY
using MediaBrowser.Model.Logging;
#else
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Entities;
#endif

namespace Jellyfin.Plugin.Bangumi.ScheduledTask;

public class TokenRefreshTask : IScheduledTask
{
    private readonly IActivityManager _activity;
    private readonly BangumiApi _api;
    private readonly OAuthStore _store;
    private readonly IUserManager _userManager;

    public TokenRefreshTask(IUserManager userManager, IActivityManager activity, BangumiApi api, OAuthStore store)
    {
        _userManager = userManager;
        _activity = activity;
        _api = api;
        _store = store;
    }

    public string Key => "OAuthTokenRefreshTask";
    public string Name => "OAuth 登录令牌刷新";
    public string Description => "OAuth 授权令牌到期前自动刷新";
    public string Category => "Bangumi";


    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromDays(1).Ticks,
                MaxRuntimeTicks = TimeSpan.FromMinutes(10).Ticks
            }
        };
    }

#if EMBY
    public Task Execute(CancellationToken token, IProgress<double> progress)
    {
        var task = Task.Run(async () => await ExecuteAsync(progress, token));
        task.Wait();
        return Task.CompletedTask;
    }
#endif

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken token)
    {
        _store.Load();
        var users = _store.GetUsers();
        var current = 0d;
        var total = users.Count;
        foreach (var (guid, user) in users)
        {
            var userId = Guid.Parse(guid);
            token.ThrowIfCancellationRequested();
            progress.Report(current / total);
            current++;
            if (user.Expired)
                continue;

#if EMBY
            var activity = new ActivityLogEntry
            {
                Name = "Bangumi 授权",
                Type = "Bangumi",
            };
            try
            {
                await user.Refresh(_api.GetHttpClient(), token);
                await user.GetProfile(_api, token);
                activity.ShortOverview = $"用户 #{user.UserId} 授权刷新成功";
                activity.Severity = LogSeverity.Info;
            }
            catch (Exception e)
            {
                activity.ShortOverview = $"用户 #{user.UserId} 授权刷新失败: {e.Message}";
                activity.Severity = LogSeverity.Warn;
            }

            _activity.Create(activity);
#else
            var activity = new ActivityLog("Bangumi 授权", "Bangumi", userId);
            try
            {
                await user.Refresh(_api.GetHttpClient(), token);
                await user.GetProfile(_api, token);
                activity.ShortOverview = $"用户 #{user.UserId} 授权刷新成功";
                activity.LogSeverity = LogLevel.Information;
            }
            catch (Exception e)
            {
                activity.ShortOverview = $"用户 #{user.UserId} 授权刷新失败: {e.Message}";
                activity.LogSeverity = LogLevel.Warning;
            }

            await _activity.CreateAsync(activity);
#endif
        }

        _store.Save();
    }
}