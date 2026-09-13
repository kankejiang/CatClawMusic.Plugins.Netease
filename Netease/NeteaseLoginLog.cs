using System.Text;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 网易云登录/续期诊断日志（独立小文件，便于现场排查"登录又过期了"）。
/// <para>
/// 为什么不走宿主 <c>Log</c>：宿主日志的文件输出受设置页「诊断日志」开关控制，默认关闭时
/// 整条登录链路（全是 try/catch + 静默返回）完全无痕——"到底续期成没成功"无从判断。
/// 本文件无条件落盘，与 Cookie 同目录，超过 256KB 自动裁剪保留最近约 800 行。
/// </para>
/// </summary>
internal static class NeteaseLoginLog
{
    private static readonly object Gate = new();

    /// <summary>%LOCALAPPDATA%\CatClawMusic.Maui\netease_login.log（与 netease_cookie.txt 同目录）</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CatClawMusic.Maui", "netease_login.log");

    private const long MaxBytes = 256 * 1024;
    private const int KeepLines = 800;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n", Encoding.UTF8);
                TrimIfNeeded();
            }
        }
        catch { /* 日志失败绝不影响登录 */ }
        System.Diagnostics.Debug.WriteLine($"[NeteaseLogin] {message}");
    }

    private static void TrimIfNeeded()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length <= MaxBytes) return;
            var lines = File.ReadAllLines(FilePath);
            var tail = lines.Length > KeepLines ? lines[^KeepLines..] : lines;
            File.WriteAllLines(FilePath, tail, Encoding.UTF8);
        }
        catch { }
    }
}
