using Windows.Media.Control;
using Windows.Storage.Streams;

// 独立探针：验证 SMTC 在当前机器上是否可用。
// 运行前请先播放媒体（如 Edge/Chrome/Windows 媒体播放器/Spotify）。
Console.OutputEncoding = System.Text.Encoding.UTF8;

try
{
    Console.WriteLine("[1] 调用 GlobalSystemMediaTransportControlsSessionManager.RequestAsync() ...");
    var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
    Console.WriteLine("[1] 成功获取 SMTC 管理器");

    Console.WriteLine("[2] 获取当前会话 ...");
    var session = manager.GetCurrentSession();
    if (session == null)
    {
        Console.WriteLine("[2] GetCurrentSession() 返回 null —— 系统里没有活动的媒体会话。");
        Console.WriteLine("    请先播放媒体（且该应用需支持 SMTC，如浏览器/媒体播放器）。");
        return;
    }

    Console.WriteLine($"[2] 会话来源: {session.SourceAppUserModelId}");

    Console.WriteLine("[3] 获取媒体属性 ...");
    var props = await session.TryGetMediaPropertiesAsync();
    Console.WriteLine($"[3] 标题: {props.Title} | 歌手: {props.Artist} | 专辑: {props.AlbumTitle}");
    Console.WriteLine($"[3] 缩略图: {(props.Thumbnail == null ? "null" : "存在")}");

    if (props.Thumbnail == null)
    {
        Console.WriteLine("[3] 媒体会话存在但没有缩略图 —— 该播放器未通过 SMTC 暴露专辑封面。");
        return;
    }

    using var stream = await props.Thumbnail.OpenReadAsync();
    Console.WriteLine($"[4] 缩略图流大小: {stream.Size} 字节");
    using var reader = new DataReader(stream);
    await reader.LoadAsync((uint)stream.Size);
    var bytes = new byte[(int)stream.Size];
    reader.ReadBytes(bytes);
    Console.WriteLine($"[4] 读取完成: {bytes.Length} 字节");
    Console.WriteLine("[4] 取色链路全部打通 ✓");
}
catch (Exception ex)
{
    Console.WriteLine($"出错: {ex}");
}
