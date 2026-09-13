using CatClawMusic.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 扫码登录页（插件自有页面，替代原先"宿主 WebView 打开网页登录页"）。
/// <para>
/// 为什么必须换掉网页登录：网页版会话（web MUSIC_U）**无法被 <c>/eapi/login/token/refresh</c> 续期** ——
/// 实测用完全有效的 web cookie 调该接口仍返回 <c>code:301</c>（同一条 eapi 通道调歌曲详情却正常），
/// 因此旧实现的"静默续期"从未生效，登录态只能等会话自然过期（用户反馈"网页版登录容易过期"）。
/// 扫码拿到的是**应用态会话**，支持续期，配合定时续期可长期有效。
/// </para>
/// <para>
/// 状态码约定（网易云）：801 等待扫码 / 802 已扫码待确认 / 803 授权成功 / 800 二维码过期 /
/// 8821 等为环境风控（可退回网页登录）。
/// </para>
/// </summary>
internal sealed class NeteaseQrLoginPage : ContentPage
{
    /// <summary>二维码类型：与官方 login_qr_key / login_qr_check 保持一致</summary>
    private const int QrType = 3;

    private const int PollIntervalMs = 2000;

    /// <summary>本地有效期：到时只提示刷新，**不停止轮询**（服务端 key 往往仍有效）</summary>
    private const int QrTtlSeconds = 240;

    private readonly NetEaseMusicPlugin _plugin;
    private readonly IServiceProvider? _services;
    private readonly Func<Task>? _onLoggedIn;

    private readonly GraphicsView _qrView;
    private readonly QrDrawable _drawable = new();
    private readonly Label _statusLabel;
    private readonly Label _hintLabel;
    private readonly Border _refreshButton;
    private readonly Border _passwordLoginButton;
    private readonly Border _webLoginButton;

    private CancellationTokenSource? _pollCts;
    private string? _qrKey;
    private DateTime _qrCreatedAtUtc;
    private bool _finished;
    private bool _expiryHintShown;

    public NeteaseQrLoginPage(NetEaseMusicPlugin plugin, IServiceProvider? services, Func<Task>? onLoggedIn = null)
    {
        _plugin = plugin;
        _services = services;
        _onLoggedIn = onLoggedIn;

        Title = "扫码登录";
        BackgroundColor = NeteaseUiKit.PageBackground;

        // ── 顶部：返回 + 标题 ──
        var back = CreateBackButton();
        var title = new Label
        {
            Text = "网易云扫码登录",
            FontSize = 17,
            FontFamily = "OpenSansSemibold",
            VerticalOptions = LayoutOptions.Center,
        };
        title.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        var header = new Grid
        {
            Padding = new Thickness(16, 12, 16, 8),
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star } },
            ColumnSpacing = 12,
            Children = { back, title },
        };
        Grid.SetColumn(title, 1);

        // ── 二维码卡片（浅底深码，保证可扫）──
        _qrView = new GraphicsView
        {
            Drawable = _drawable,
            WidthRequest = 248,
            HeightRequest = 248,
            HorizontalOptions = LayoutOptions.Center,
        };
        var qrCard = new Border
        {
            Padding = new Thickness(10),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            BackgroundColor = Colors.White,
            HorizontalOptions = LayoutOptions.Center,
            Content = _qrView,
        };

        _statusLabel = new Label
        {
            Text = "正在获取二维码…",
            FontSize = 14,
            FontFamily = "OpenSansSemibold",
            HorizontalTextAlignment = TextAlignment.Center,
        };
        _statusLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");

        _hintLabel = new Label
        {
            Text = "打开手机「网易云音乐」App → 左上角扫一扫 → 扫描上方二维码并确认授权",
            FontSize = 11,
            HorizontalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(24, 0),
        };
        _hintLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");

        _refreshButton = CreateActionButton("刷新二维码", primary: true, OnRefreshTapped);
        _passwordLoginButton = CreateActionButton("手机号登录", primary: false, OnPasswordLoginTapped);
        _webLoginButton = CreateActionButton("改用网页登录", primary: false, OnWebLoginTapped);
        _refreshButton.IsVisible = false;

        var buttons = new HorizontalStackLayout
        {
            Spacing = 10,
            HorizontalOptions = LayoutOptions.Center,
            Children = { _refreshButton, _passwordLoginButton, _webLoginButton },
        };

        var center = new VerticalStackLayout
        {
            Spacing = 14,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Children = { qrCard, _statusLabel, _hintLabel, buttons },
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitionCollection { new() { Height = GridLength.Auto }, new() { Height = GridLength.Star } },
            Children = { header, center },
        };
        Grid.SetRow(center, 1);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = StartQrAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopPolling();
    }

    // ── 二维码获取与轮询 ──

    private async Task StartQrAsync()
    {
        StopPolling();
        _finished = false;
        _expiryHintShown = false;
        _refreshButton.IsVisible = false;
        SetStatus("正在获取二维码…");

        string? key;
        try { key = await _plugin.ApiClient.GetQrKeyAsync(QrType); }
        catch (Exception ex) { key = null; NeteaseLoginLog.Write($"扫码页获取 key 异常：{ex.Message}"); }

        if (string.IsNullOrWhiteSpace(key))
        {
            _drawable.Clear();
            _qrView.Invalidate();
            SetStatus("获取二维码失败，请点「刷新二维码」重试");
            _refreshButton.IsVisible = true;
            return;
        }

        _qrKey = key;
        _qrCreatedAtUtc = DateTime.UtcNow;

        var content = $"https://music.163.com/login?codekey={key}";
        try
        {
            var qr = QrEncoder.Encode(content, QrEncoder.Ecc.M);
            _drawable.SetCode(qr);
            _qrView.Invalidate();
            SetStatus("请用「网易云音乐」App 扫描二维码");
            NeteaseLoginLog.Write($"已生成扫码二维码（版本 {qr.Version}，{qr.Size}×{qr.Size}，掩码 {qr.Mask}）");
        }
        catch (Exception ex)
        {
            NeteaseLoginLog.Write($"二维码生成失败：{ex.Message}");
            SetStatus("二维码生成失败，请重试");
            _refreshButton.IsVisible = true;
            return;
        }

        var cts = new CancellationTokenSource();
        _pollCts = cts;
        _ = PollLoopAsync(key, cts.Token);
    }

    private async Task PollLoopAsync(string key, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_finished)
            {
                await Task.Delay(PollIntervalMs, ct);
                if (!_expiryHintShown && (DateTime.UtcNow - _qrCreatedAtUtc).TotalSeconds > QrTtlSeconds)
                {
                    // 只提示、不停止轮询：服务端 key 常常仍然有效，此时扫码也应能完成登录。
                    // 真正的过期由服务端返回 800 判定（见下面 case 800）。
                    _expiryHintShown = true;
                    SetStatus("二维码可能已过期，可点「刷新二维码」重新获取");
                    _refreshButton.IsVisible = true;
                }

                var (code, cookie) = await _plugin.ApiClient.CheckQrLoginAsync(key, QrType);
                switch (code)
                {
                    case 801:
                        break;
                    case 802:
                        SetStatus("已扫码，请在手机上点击确认");
                        break;
                    case 803:
                        await OnAuthorizedAsync(cookie);
                        return;
                    case 800:
                        SetStatus("二维码已过期，请点「刷新二维码」");
                        _refreshButton.IsVisible = true;
                        return;
                    case 8821:
                        SetStatus("当前网络环境被网易云风控限制，可改用网页登录");
                        _refreshButton.IsVisible = true;
                        return;
                    default:
                        SetStatus($"登录状态异常（code={code}），可刷新重试");
                        break;
                }
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            NeteaseLoginLog.Write($"扫码轮询异常：{ex.Message}");
            SetStatus($"轮询异常：{ex.Message}");
        }
    }

    private async Task OnAuthorizedAsync(string? cookie)
    {
        _finished = true;
        StopPolling();

        if (string.IsNullOrWhiteSpace(cookie))
        {
            SetStatus("已授权但未取到会话 Cookie，请刷新重试");
            _refreshButton.IsVisible = true;
            return;
        }

        try
        {
            // 复用既有登录落盘链路：校验 MUSIC_U → 规范化 → 写 netease_cookie.txt → 起定时续期
            await _plugin.SetLoginCookieAsync(cookie);
            var nickname = await _plugin.GetAccountNameAsync();
            SetStatus(string.IsNullOrWhiteSpace(nickname) ? "登录成功" : $"登录成功：{nickname}");
            NeteaseLoginLog.Write("扫码登录完成，已切换到应用态会话");

            if (_onLoggedIn != null)
            {
                try { await _onLoggedIn(); } catch { }
            }

            await Task.Delay(700);
            await NeteaseNav.PopAsync(this, _services);
        }
        catch (Exception ex)
        {
            NeteaseLoginLog.Write($"扫码登录落盘失败：{ex.Message}");
            SetStatus($"登录保存失败：{ex.Message}");
            _refreshButton.IsVisible = true;
        }
    }

    private void StopPolling()
    {
        try { _pollCts?.Cancel(); } catch { }
        _pollCts?.Dispose();
        _pollCts = null;
    }

    // ── 交互 ──

    private void OnRefreshTapped(object? sender, EventArgs e) => _ = StartQrAsync();

    /// <summary>扫码不方便时（如手机上无法"自己扫自己"）走手机号验证码 / 密码登录。
    /// 用「替换」而非叠加，保证登录页始终只有一层（见 NeteaseNav.ReplaceAsync 注释）。</summary>
    private async void OnPasswordLoginTapped(object? sender, EventArgs e)
    {
        try
        {
            StopPolling();
            var page = new NeteaseAccountLoginPage(_plugin, _services, _onLoggedIn);
            await NeteaseNav.ReplaceAsync(this, page, _services);
        }
        catch (Exception ex)
        {
            NeteaseLoginLog.Write($"打开账号密码登录页失败：{ex.Message}");
            SetStatus("打开账号密码登录失败");
        }
    }

    /// <summary>风控等场景的退路：仍可走宿主 WebView 网页登录（会话不持久，但能登录）</summary>
    private async void OnWebLoginTapped(object? sender, EventArgs e)
    {
        StopPolling();
        try
        {
            var nav = _services?.GetService<INavigationService>();
            if (nav != null)
            {
                await nav.NavigateToAsync("webviewlogin?platform=netease");
            }
            else
            {
                SetStatus("当前界面不支持网页登录");
            }
        }
        catch (Exception ex)
        {
            NeteaseLoginLog.Write($"打开网页登录失败：{ex.Message}");
            SetStatus("打开网页登录失败");
        }
    }

    private void SetStatus(string text)
    {
        _statusLabel.Text = text;
    }

    private Border CreateBackButton()
    {
        var border = new Border
        {
            Padding = new Thickness(12, 7),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = new Label { Text = "‹", FontSize = 26, Margin = new Thickness(0, -4, 0, 0), HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
        };
        border.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        ((Label)border.Content!).SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => { try { await NeteaseNav.PopAsync(this, _services); } catch { } };
        border.GestureRecognizers.Add(tap);
        return border;
    }

    private static Border CreateActionButton(string text, bool primary, EventHandler onTapped)
    {
        var label = new Label { Text = text, FontSize = 12, FontFamily = "OpenSansSemibold", VerticalOptions = LayoutOptions.Center };
        var border = new Border
        {
            Padding = new Thickness(16, 9),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Content = label,
        };
        if (primary)
        {
            border.BackgroundColor = Color.FromArgb("#FF8FB8");   // 与宿主主题色一致
            label.TextColor = Colors.White;
        }
        else
        {
            border.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
            label.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        }
        var tap = new TapGestureRecognizer();
        tap.Tapped += (s, e) => onTapped(s, e);
        border.GestureRecognizers.Add(tap);
        return border;
    }

    /// <summary>二维码绘制：白底 + 4 模块静区 + 黑色模块（按可用尺寸自适应缩放）</summary>
    private sealed class QrDrawable : IDrawable
    {
        private bool[,]? _dark;
        private int _size;

        public void SetCode(QrEncoder.QrCode qr)
        {
            _dark = qr.Dark;
            _size = qr.Size;
        }

        public void Clear()
        {
            _dark = null;
            _size = 0;
        }

        public void Draw(ICanvas canvas, RectF rect)
        {
            canvas.FillColor = Colors.White;
            canvas.FillRectangle(rect);
            if (_dark == null || _size <= 0) return;

            const int quiet = 4;                       // 静区：4 模块（扫码必需）
            var total = _size + quiet * 2;
            var module = Math.Min(rect.Width, rect.Height) / total;
            var left = rect.X + (rect.Width - module * total) / 2f;
            var top = rect.Y + (rect.Height - module * total) / 2f;

            canvas.FillColor = Colors.Black;
            // 多画 0.5px 消除相邻模块间的白缝（抗锯齿导致的"网格线"会影响识别）
            var side = module + 0.5f;
            for (var r = 0; r < _size; r++)
                for (var c = 0; c < _size; c++)
                    if (_dark[r, c])
                        canvas.FillRectangle(left + (c + quiet) * module, top + (r + quiet) * module, side, side);
        }
    }
}
