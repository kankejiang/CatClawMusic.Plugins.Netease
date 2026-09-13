using CatClawMusic.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Shapes;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 账号登录页，两种方式：
/// <list type="bullet">
///   <item><b>验证码登录（默认）</b>：手机号 → 获取验证码 → 填码登录。国内 App 习惯做法，
///     也不需要用户把密码交给第三方客户端。接口：<c>/api/sms/captcha/sent</c> →
///     <c>/api/sms/captcha/verify</c> → <c>/api/w/login/cellphone</c>（均 weapi）。</item>
///   <item><b>密码登录</b>：手机号或邮箱 + 密码。手机号 <c>/api/w/login/cellphone</c>（weapi，type=1）、
///     邮箱 <c>/api/w/login</c>（eapi，type=0），密码为 MD5 小写十六进制。</item>
/// </list>
/// 另提供「改用扫码登录」与（扫码页的）「改用网页登录」作为风控场景的退路。
/// </summary>
internal sealed class NeteaseAccountLoginPage : ContentPage
{
    private enum LoginMode { Sms, Password }

    private const int ResendSeconds = 60;

    private readonly NetEaseMusicPlugin _plugin;
    private readonly IServiceProvider? _services;
    private readonly Func<Task>? _onLoggedIn;

    private readonly Border _smsChip;
    private readonly Border _pwdChip;
    private readonly Label _smsChipLabel;
    private readonly Label _pwdChipLabel;

    private readonly Entry _countryEntry;
    private readonly Entry _accountEntry;
    private readonly Entry _codeEntry;
    private readonly Entry _passwordEntry;

    private readonly HorizontalStackLayout _codeRow;
    private readonly Border _sendCodeButton;
    private readonly Label _sendCodeLabel;

    private readonly Label _statusLabel;
    private readonly Border _loginButton;

    private LoginMode _mode = LoginMode.Sms;
    private IDispatcherTimer? _countdown;
    private int _secondsLeft;
    private bool _busy;

    public NeteaseAccountLoginPage(NetEaseMusicPlugin plugin, IServiceProvider? services, Func<Task>? onLoggedIn = null)
    {
        _plugin = plugin;
        _services = services;
        _onLoggedIn = onLoggedIn;

        Title = "账号登录";
        BackgroundColor = NeteaseUiKit.PageBackground;

        // ── 顶栏 ──
        var back = CreateBackButton();
        var title = new Label
        {
            Text = "网易云账号登录",
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

        // ── 方式切换 chips ──
        _smsChipLabel = new Label { Text = "验证码登录", FontSize = 12, VerticalOptions = LayoutOptions.Center };
        _smsChip = CreateChip(_smsChipLabel, () => SetMode(LoginMode.Sms));
        _pwdChipLabel = new Label { Text = "密码登录", FontSize = 12, VerticalOptions = LayoutOptions.Center };
        _pwdChip = CreateChip(_pwdChipLabel, () => SetMode(LoginMode.Password));
        var chips = new HorizontalStackLayout
        {
            Spacing = 8,
            HorizontalOptions = LayoutOptions.Center,
            Children = { _smsChip, _pwdChip },
        };

        // ── 输入区 ──
        _countryEntry = CreateEntry("86", "区号", Keyboard.Numeric, 68);
        _accountEntry = CreateEntry("", "手机号", Keyboard.Telephone, 216);
        var accountRow = new HorizontalStackLayout
        {
            Spacing = 8,
            HorizontalOptions = LayoutOptions.Center,
            Children = { _countryEntry, _accountEntry },
        };

        _codeEntry = CreateEntry("", "短信验证码", Keyboard.Numeric, 150);
        _sendCodeLabel = new Label { Text = "获取验证码", FontSize = 12, FontFamily = "OpenSansSemibold", VerticalOptions = LayoutOptions.Center };
        _sendCodeButton = new Border
        {
            Padding = new Thickness(14, 9),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = _sendCodeLabel,
        };
        _sendCodeButton.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        _sendCodeLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        var sendTap = new TapGestureRecognizer();
        sendTap.Tapped += OnSendCodeTapped;
        _sendCodeButton.GestureRecognizers.Add(sendTap);
        _codeRow = new HorizontalStackLayout
        {
            Spacing = 8,
            HorizontalOptions = LayoutOptions.Center,
            Children = { _codeEntry, _sendCodeButton },
        };

        _passwordEntry = CreateEntry("", "密码", Keyboard.Text, 274, isPassword: true);

        _statusLabel = new Label
        {
            Text = "",
            FontSize = 11,
            HorizontalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(20, 2, 20, 0),
        };
        _statusLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");

        _loginButton = CreateActionButton("登 录", primary: true, OnLoginTapped);
        var qrButton = CreateActionButton("改用扫码登录", primary: false, OnQrTapped);
        var buttons = new HorizontalStackLayout
        {
            Spacing = 10,
            HorizontalOptions = LayoutOptions.Center,
            Children = { _loginButton, qrButton },
        };

        var center = new VerticalStackLayout
        {
            Spacing = 12,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Children = { chips, accountRow, _codeRow, _passwordEntry, _statusLabel, buttons },
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitionCollection { new() { Height = GridLength.Auto }, new() { Height = GridLength.Star } },
            Children = { header, center },
        };
        Grid.SetRow(center, 1);

        SetMode(LoginMode.Sms);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopCountdown();
    }

    // ── 模式 ──

    private void SetMode(LoginMode mode)
    {
        _mode = mode;
        var sms = mode == LoginMode.Sms;
        _codeRow.IsVisible = sms;
        _passwordEntry.IsVisible = !sms;
        _accountEntry.Placeholder = sms ? "手机号" : "手机号或邮箱";
        _accountEntry.Keyboard = sms ? Keyboard.Telephone : Keyboard.Text;

        _smsChip.BackgroundColor = sms ? Color.FromArgb("#FF8FB8") : (Color)(Application.Current?.Resources.TryGetValue("SurfaceColor", out var c) == true ? c : Colors.Gray);
        _smsChipLabel.TextColor = sms ? Colors.White : (Color)(Application.Current?.Resources.TryGetValue("TextPrimaryColor", out var t) == true ? t : Colors.White);
        _pwdChip.BackgroundColor = sms ? (Color)(Application.Current?.Resources.TryGetValue("SurfaceColor", out var c2) == true ? c2 : Colors.Gray) : Color.FromArgb("#FF8FB8");
        _pwdChipLabel.TextColor = sms ? (Color)(Application.Current?.Resources.TryGetValue("TextPrimaryColor", out var t2) == true ? t2 : Colors.White) : Colors.White;

        SetStatus(sms
            ? "填手机号 → 获取验证码 → 填码登录。"
            : "手机号或邮箱 + 密码；密码仅用于本次请求（MD5），不会保存在本机。");
    }

    // ── 发码 ──

    private async void OnSendCodeTapped(object? sender, EventArgs e)
    {
        if (_secondsLeft > 0) return;
        var phone = _accountEntry.Text?.Trim() ?? "";
        if (phone.Length < 6 || !phone.All(char.IsDigit))
        {
            SetStatus("请先填写正确的手机号", error: true);
            return;
        }

        SetStatus("正在发送验证码…");
        try
        {
            var (code, message) = await _plugin.ApiClient.SendSmsCodeAsync(phone, _countryEntry.Text?.Trim() ?? "86");
            if (code == 200)
            {
                SetStatus("验证码已发送，请查看短信");
                StartCountdown();
            }
            else
            {
                SetStatus(Explain(code, message), error: true);
            }
        }
        catch (Exception ex)
        {
            NeteaseLoginLog.Write($"发送验证码异常：{ex.Message}");
            SetStatus($"发送失败：{ex.Message}", error: true);
        }
    }

    private void StartCountdown()
    {
        StopCountdown();
        _secondsLeft = ResendSeconds;
        _sendCodeButton.Opacity = 0.6;
        _sendCodeLabel.Text = $"{_secondsLeft}s";
        _countdown = Dispatcher.CreateTimer();
        _countdown.Interval = TimeSpan.FromSeconds(1);
        _countdown.IsRepeating = true;
        _countdown.Tick += OnCountdownTick;
        _countdown.Start();
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _secondsLeft--;
        if (_secondsLeft <= 0)
        {
            StopCountdown();
            _sendCodeLabel.Text = "获取验证码";
            _sendCodeButton.Opacity = 1;
            return;
        }
        _sendCodeLabel.Text = $"{_secondsLeft}s";
    }

    private void StopCountdown()
    {
        if (_countdown == null) return;
        _countdown.Stop();
        _countdown.Tick -= OnCountdownTick;
        _countdown = null;
        _secondsLeft = 0;
    }

    // ── 登录 ──

    private async void OnLoginTapped(object? sender, EventArgs e)
    {
        if (_busy) return;
        var account = _accountEntry.Text?.Trim() ?? "";
        var cc = _countryEntry.Text?.Trim() ?? "86";

        _busy = true;
        _loginButton.Opacity = 0.6;
        SetStatus("正在登录…");
        try
        {
            (int Code, string? Cookie, string? Message) result;
            if (_mode == LoginMode.Sms)
            {
                var captcha = _codeEntry.Text?.Trim() ?? "";
                if (captcha.Length == 0) { SetStatus("请填写短信验证码", error: true); return; }
                result = await _plugin.ApiClient.LoginWithSmsAsync(account, captcha, cc);
            }
            else
            {
                var password = _passwordEntry.Text ?? "";
                if (password.Length == 0) { SetStatus("请填写密码", error: true); return; }
                result = await _plugin.ApiClient.LoginWithPasswordAsync(account, password, cc);
            }

            if (result.Code == 200 && !string.IsNullOrWhiteSpace(result.Cookie)
                && result.Cookie.Contains("MUSIC_U=", StringComparison.OrdinalIgnoreCase))
            {
                await _plugin.SetLoginCookieAsync(result.Cookie);
                var nickname = await _plugin.GetAccountNameAsync();
                SetStatus(string.IsNullOrWhiteSpace(nickname) ? "登录成功" : $"登录成功：{nickname}");
                NeteaseLoginLog.Write("账号登录完成（登录页）");
                if (_onLoggedIn != null) { try { await _onLoggedIn(); } catch { } }
                await Task.Delay(700);
                await NeteaseNav.PopAsync(this, _services);
                return;
            }

            SetStatus(Explain(result.Code, result.Message), error: true);
        }
        catch (Exception ex)
        {
            NeteaseLoginLog.Write($"登录页异常：{ex.Message}");
            SetStatus($"登录失败：{ex.Message}", error: true);
        }
        finally
        {
            _busy = false;
            _loginButton.Opacity = 1;
        }
    }

    /// <summary>把接口错误码翻成人话</summary>
    private static string Explain(int code, string? message) => code switch
    {
        501 => "账号不存在，请检查手机号/邮箱",
        502 => "验证码或密码错误",
        503 => "操作过于频繁，请稍后再试",
        400 => "请求被拒绝（手机号格式或参数不符）",
        8810 => "需要图形验证码校验，建议改用扫码或网页登录",
        8821 => "当前网络环境被风控限制，建议改用扫码或网页登录",
        -1 => string.IsNullOrWhiteSpace(message) ? "网络异常，请重试" : message!,
        200 => "已通过校验但未取到会话，请重试",
        _ => $"登录失败（code={code}）{(string.IsNullOrWhiteSpace(message) ? "" : "：" + message)}",
    };

    private async void OnQrTapped(object? sender, EventArgs e)
    {
        try
        {
            StopCountdown();
            // 同样用「替换」：登录页之间不叠栈（否则扫码成功后退一层又回到本页）
            var qr = new NeteaseQrLoginPage(_plugin, _services, _onLoggedIn);
            await NeteaseNav.ReplaceAsync(this, qr, _services);
        }
        catch (Exception ex)
        {
            NeteaseLoginLog.Write($"切换到扫码页失败：{ex.Message}");
            SetStatus("切换扫码登录失败", error: true);
        }
    }

    private void SetStatus(string text, bool error = false)
    {
        _statusLabel.Text = text;
        _statusLabel.TextColor = error
            ? Color.FromArgb("#FF6B81")
            : (Color)(Application.Current?.Resources.TryGetValue("TextHintColor", out var c) == true ? c : Colors.Gray);
    }

    // ── UI 构件 ──

    private static Entry CreateEntry(string text, string placeholder, Keyboard keyboard, double width, bool isPassword = false)
    {
        var entry = new Entry
        {
            Text = text,
            Placeholder = placeholder,
            Keyboard = keyboard,
            IsPassword = isPassword,
            WidthRequest = width,
            FontSize = 14,
            BackgroundColor = Colors.Transparent,
        };
        entry.SetDynamicResource(Entry.TextColorProperty, "TextPrimaryColor");
        entry.SetDynamicResource(Entry.PlaceholderColorProperty, "TextHintColor");
        return entry;
    }

    private static Border CreateChip(Label label, Action onTap)
    {
        var border = new Border
        {
            Padding = new Thickness(14, 7),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = label,
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onTap();
        border.GestureRecognizers.Add(tap);
        return border;
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
            border.BackgroundColor = Color.FromArgb("#FF8FB8");
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
}
