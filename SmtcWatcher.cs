using Windows.Media.Control;
using Windows.Storage.Streams;

namespace ClassIslandInjector;

/// <summary>
/// SMTC 媒体变化事件参数。
/// </summary>
internal sealed class SmtcMediaChangedEventArgs : EventArgs
{
    public SmtcMediaChangedEventArgs(AlbumAccentColors? colors, byte[]? thumbnailBytes, bool isPlaying, string? title, string? artist)
    {
        Colors = colors;
        ThumbnailBytes = thumbnailBytes;
        IsPlaying = isPlaying;
        Title = title ?? string.Empty;
        Artist = artist ?? string.Empty;
    }

    /// <summary>从专辑封面提取的 Material You 颜色；无缩略图时为 null。</summary>
    public AlbumAccentColors? Colors { get; }

    /// <summary>专辑封面缩略图原始字节；无缩略图时为 null。</summary>
    public byte[]? ThumbnailBytes { get; }

    /// <summary>当前焦点会话是否正在播放；暂停/停止时为 false。</summary>
    public bool IsPlaying { get; }

    /// <summary>当前媒体的标题（无媒体时为空字符串）。</summary>
    public string Title { get; }

    /// <summary>当前媒体的艺术家（无媒体时为空字符串）。</summary>
    public string Artist { get; }
}

/// <summary>
/// 事件驱动的 SMTC 会话监听器（借鉴 MediaIsland 的 WindowsMediaController 方案）：
/// 订阅 SessionManager 的会话增删/焦点变化，以及每个会话的媒体属性/播放/时间轴事件，
/// 在媒体变化时立即推送取色结果与缩略图字节，无需轮询。
/// 另保留一个低频兜底刷新（间隔可配置），应对个别应用事件不触发的情况。
/// </summary>
internal sealed class SmtcWatcher : IDisposable
{
    private readonly object _lock = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _sessions = [];
    private bool _isRunning;
    private bool _isRefreshing;
    private string _lastSnapshotKey = string.Empty;
    private string _focusedSessionId = string.Empty;
    private Timer? _fallbackTimer;

    /// <summary>
    /// 媒体变化事件。事件可能在非 UI 线程触发，调用方需自行调度到 UI 线程。
    /// </summary>
    public event EventHandler<SmtcMediaChangedEventArgs>? MediaChanged;

    public bool IsRunning => _isRunning;

    /// <summary>
    /// 兜底刷新间隔（秒）。事件驱动为主，此刷新仅用于兜底。
    /// </summary>
    public double RefreshIntervalSeconds { get; set; } = 4;

    public async Task StartAsync()
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;
        }

        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_lock)
            {
                if (!_isRunning)
                {
                    // 启动等待期间已被 Stop()：释放本次获取的 manager 并退出。
                    manager.SessionsChanged -= OnSessionsChanged;
                    manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                    return;
                }

                _manager = manager;
            }

            manager.SessionsChanged += OnSessionsChanged;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            EnumerateSessions();
            await RefreshAsync();
            UpdateFallbackTimer();
        }
        catch (Exception ex)
        {
            SmtcAlbumColorPicker.LogDiagnostic($"SmtcWatcher 启动失败: {ex.Message}");
            lock (_lock)
            {
                _isRunning = false;
            }

            throw;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _isRunning = false;
            _fallbackTimer?.Dispose();
            _fallbackTimer = null;
            if (_manager != null)
            {
                _manager.SessionsChanged -= OnSessionsChanged;
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            }

            foreach (var session in _sessions.Values)
            {
                UnsubscribeSession(session);
            }

            _sessions.Clear();
            _manager = null;
            _lastSnapshotKey = string.Empty;
        }
    }

    /// <summary>
    /// 兜底刷新间隔变化时调用，重建定时器。
    /// </summary>
    public void UpdateFallbackTimer()
    {
        lock (_lock)
        {
            _fallbackTimer?.Dispose();
            _fallbackTimer = null;
            if (!_isRunning)
            {
                return;
            }

            var seconds = Math.Max(0.5, RefreshIntervalSeconds);
            _fallbackTimer = new Timer(
                _ => _ = RefreshAsync(),
                null,
                TimeSpan.FromSeconds(seconds),
                TimeSpan.FromSeconds(seconds));
        }
    }

    public void Dispose() => Stop();

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        SmtcAlbumColorPicker.LogDiagnostic("SMTC 事件: SessionsChanged");
        EnumerateSessions();
        _ = RefreshAsync();
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        SmtcAlbumColorPicker.LogDiagnostic("SMTC 事件: CurrentSessionChanged");
        _ = RefreshAsync();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        if (sender.SourceAppUserModelId == _focusedSessionId)
        {
            SmtcAlbumColorPicker.LogDiagnostic($"SMTC 事件: MediaPropertiesChanged 会话={sender.SourceAppUserModelId}（焦点，触发刷新）");
            _ = RefreshAsync();
        }
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        if (sender.SourceAppUserModelId == _focusedSessionId)
        {
            _ = RefreshAsync();
        }
    }

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        if (sender.SourceAppUserModelId == _focusedSessionId)
        {
            _ = RefreshAsync();
        }
    }

    private void EnumerateSessions()
    {
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> list;
        lock (_lock)
        {
            if (!_isRunning || _manager == null)
            {
                return;
            }

            try
            {
                list = _manager.GetSessions();
            }
            catch
            {
                return;
            }
        }

        foreach (var session in list)
        {
            lock (_lock)
            {
                if (!_sessions.ContainsKey(session.SourceAppUserModelId))
                {
                    _sessions[session.SourceAppUserModelId] = session;
                    session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                    session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                }
            }
        }

        // 清理已消失的会话（某些应用如 Spotify 不会触发关闭事件）。
        var currentIds = list.Select(x => x.SourceAppUserModelId).ToHashSet();
        List<GlobalSystemMediaTransportControlsSession> toRemove;
        lock (_lock)
        {
            toRemove = _sessions.Values.Where(x => !currentIds.Contains(x.SourceAppUserModelId)).ToList();
            foreach (var session in toRemove)
            {
                UnsubscribeSession(session);
                _sessions.Remove(session.SourceAppUserModelId);
            }
        }
    }

    private async Task RefreshAsync()
    {
        lock (_lock)
        {
            if (!_isRunning || _manager == null || _isRefreshing)
            {
                return;
            }

            _isRefreshing = true;
        }

        try
        {
            GlobalSystemMediaTransportControlsSession session;
            lock (_lock)
            {
                if (!_isRunning || _manager == null)
                {
                    return;
                }

                try
                {
                    session = _manager.GetCurrentSession();
                }
                catch
                {
                    return;
                }
            }

            if (session == null)
            {
                // 会话消失（媒体应用关闭/停止播放）→ 推送“停止”事件，让注入器恢复原色并清空 SMTC 底图。
                lock (_lock)
                {
                    if (_focusedSessionId.Length == 0)
                    {
                        return;
                    }

                    _focusedSessionId = string.Empty;
                    _lastSnapshotKey = string.Empty;
                }

                SmtcAlbumColorPicker.LogDiagnostic("SMTC 媒体停止: 无当前会话，通知恢复原色");
                MediaChanged?.Invoke(this, new SmtcMediaChangedEventArgs(null, null, false, string.Empty, string.Empty));
                return;
            }

            lock (_lock)
            {
                _focusedSessionId = session.SourceAppUserModelId;
            }

            var isPlaying = IsSessionPlaying(session);
            var mediaProperties = await TryGetMediaPropertiesAsync(session);
            if (mediaProperties == null)
            {
                return;
            }

            // 以播放状态 + 标题/歌手/专辑 + 缩略图字节数作为快照指纹，
            // 避免重复触发，同时保证暂停/恢复切换也能触发（供恢复原色）。
            var key = $"{isPlaying}|{mediaProperties.Title}|{mediaProperties.Artist}|{mediaProperties.AlbumTitle}";
            byte[]? bytes = null;
            if (mediaProperties.Thumbnail != null)
            {
                bytes = await TryReadThumbnailBytesAsync(mediaProperties.Thumbnail);
                key += $"|{bytes?.Length ?? 0}";
            }

            lock (_lock)
            {
                if (key == _lastSnapshotKey)
                {
                    return;
                }

                _lastSnapshotKey = key;
            }

            var colors = bytes == null ? null : SmtcAlbumColorPicker.ExtractAccentColors(bytes);
            SmtcAlbumColorPicker.LogDiagnostic(
                $"SMTC 媒体变化: 会话={session.SourceAppUserModelId}, 播放={isPlaying}, 标题={mediaProperties.Title}, " +
                $"缩略图={bytes?.Length ?? 0} 字节, 颜色={(colors is null ? "null" : $"{colors.Background}")}");
            MediaChanged?.Invoke(this, new SmtcMediaChangedEventArgs(colors, bytes, isPlaying,
                mediaProperties.Title, mediaProperties.Artist));
        }
        catch (Exception ex)
        {
            SmtcAlbumColorPicker.LogDiagnostic($"SmtcWatcher 刷新异常: {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _isRefreshing = false;
            }
        }
    }

    private static bool IsSessionPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<GlobalSystemMediaTransportControlsSessionMediaProperties?> TryGetMediaPropertiesAsync(
        GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return await session.TryGetMediaPropertiesAsync();
        }
        catch (Exception ex) when (IsIgnorableException(ex))
        {
            SmtcAlbumColorPicker.LogDiagnostic($"忽略 SMTC 媒体属性读取错误 (0x{ex.HResult:X8}): {session.SourceAppUserModelId}");
            return null;
        }
    }

    private static async Task<byte[]?> TryReadThumbnailBytesAsync(IRandomAccessStreamReference thumbnail)
    {
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0)
            {
                return null;
            }

            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[(int)stream.Size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex) when (IsIgnorableException(ex))
        {
            SmtcAlbumColorPicker.LogDiagnostic($"忽略 SMTC 缩略图读取错误 (0x{ex.HResult:X8})");
            return null;
        }
    }

    private static bool IsIgnorableException(Exception exception) =>
        exception.HResult is unchecked((int)0x800706BA) or unchecked((int)0x80070015);

    private void UnsubscribeSession(GlobalSystemMediaTransportControlsSession session)
    {
        session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
    }
}
