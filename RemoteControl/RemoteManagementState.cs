using System.Text.Json;

namespace ClassIslandInjector.RemoteControl;

/// <summary>
/// 远程管理状态（配置目录\remote-management.json）：是否锁定本机编辑、最近一次远程修改、本机是否不连接 RemoteCI。
/// <para>
/// 刻意不放进 <see cref="InjectorSettings"/>：预设套用 / 恢复默认 / 自动化「恢复」都会整体 CopyFrom，
/// 放在里面会被它们悄悄解锁。
/// </para>
/// </summary>
internal static class RemoteManagementState
{
    private const string FileName = "remote-management.json";
    private static readonly object Sync = new();
    private static StateModel _state = new();
    private static bool _loaded;

    /// <summary>锁定状态或最近修改信息变化时触发（可能在任意线程）。</summary>
    public static event EventHandler? Changed;

    /// <summary>本机样式设置是否被学校锁定（设置页只读）。</summary>
    public static bool LockLocalEditing
    {
        get
        {
            EnsureLoaded();
            return _state.LockLocalEditing;
        }
    }

    /// <summary>最近一次远程修改的时间（本地时间）；从未修改过为 null。</summary>
    public static DateTime? LastRemoteChange
    {
        get
        {
            EnsureLoaded();
            return _state.LastRemoteChange;
        }
    }

    /// <summary>最近一次远程修改的发起人显示名。</summary>
    public static string? LastRemoteBy
    {
        get
        {
            EnsureLoaded();
            return _state.LastRemoteBy;
        }
    }

    /// <summary>
    /// 本机选择「不连接 RemoteCI」：开启后不向 RemoteCI 注册，样式注入器不受远程管理。
    /// 只在本机设置页「连接器」分区修改，不开放给远程（否则学校可以远程把它关掉）。
    /// </summary>
    public static bool DisconnectRemoteCi
    {
        get
        {
            EnsureLoaded();
            return _state.DisconnectRemoteCi;
        }
    }

    public static void SetDisconnectRemoteCi(bool disconnect)
    {
        lock (Sync)
        {
            EnsureLoaded();
            _state.DisconnectRemoteCi = disconnect;
            Save();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void SetLock(bool locked)
    {
        lock (Sync)
        {
            EnsureLoaded();
            _state.LockLocalEditing = locked;
            Save();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void RecordRemoteChange(string? requestedBy)
    {
        lock (Sync)
        {
            EnsureLoaded();
            _state.LastRemoteChange = DateTime.Now;
            _state.LastRemoteBy = string.IsNullOrWhiteSpace(requestedBy) ? null : requestedBy.Trim();
            Save();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>「删除所有数据」后丢弃内存状态（文件随配置目录一起删除）。</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            _state = new StateModel();
            _loaded = true;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static string? FilePath =>
        string.IsNullOrEmpty(InjectorRuntime.ConfigDirectory) ? null : Path.Combine(InjectorRuntime.ConfigDirectory, FileName);

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (Sync)
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                var path = FilePath;
                if (path != null && File.Exists(path))
                {
                    _state = JsonSerializer.Deserialize<StateModel>(File.ReadAllText(path)) ?? new StateModel();
                }
            }
            catch
            {
                // 损坏的状态文件按「未锁定」处理：锁定失效好过把用户永久锁死。
                _state = new StateModel();
            }

            _loaded = true;
        }
    }

    private static void Save()
    {
        try
        {
            var path = FilePath;
            if (path == null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 写盘失败只影响重启后的持久化，不影响本次生效。
        }
    }

    private sealed class StateModel
    {
        public bool LockLocalEditing { get; set; }

        public DateTime? LastRemoteChange { get; set; }

        public string? LastRemoteBy { get; set; }

        public bool DisconnectRemoteCi { get; set; }
    }
}
