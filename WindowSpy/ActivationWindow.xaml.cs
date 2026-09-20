using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace WindowSpy;

public partial class ActivationWindow : Window
{
    private readonly string _code;
    private readonly LicenseError _error;

    public ActivationWindow(LicenseError error, string? reason = null)
    {
        InitializeComponent();
        _error = error;
        _code = LicenseManager.GetRequestCode();
        TxtCode.Text = _code;

        TxtReason.Text = reason ?? (error == LicenseError.None ? "需要授权才能进入" : "授权验证失败");
        TxtDetail.Text = reason != null ? "申请码已自动复制到剪贴板，并发给了软件目录下的 授权申请码.txt" : LicenseManager.ErrorText(error);
        TxtActivateResult.Text = "";

        try { Clipboard.SetText(_code); } catch { }
        try
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "授权申请码.txt"),
                $"申请码: {_code}\r\n时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n站长邮箱: {LicenseManager.ContactEmail}\r\n说明: 把申请码发给站长换取 license.json");
        }
        catch { }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    private void BtnExit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void BtnCopyCode_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_code); BtnCopyCode.Content = "已复制"; }
        catch { MessageBox.Show("复制失败，请手动选中复制。", "提示"); }
    }

    private void BtnCopyMail_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LicenseManager.ContactEmail); BtnCopyMail.Content = "已复制"; }
        catch { }
    }

    private void BtnMail_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                $"mailto:{LicenseManager.ContactEmail}?subject=" + Uri.EscapeDataString("雷霆量化系统 授权申请") +
                "&body=" + Uri.EscapeDataString($"站长大人好，我的申请码是：\r\n{_code}\r\n\r\n请帮忙激活，谢谢！"))
            { UseShellExecute = true });
        }
        catch { MessageBox.Show("无法打开邮件客户端，请手动复制邮箱发信。", "提示"); }
    }

    private void BtnPick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择站长发给你的 license.json",
            Filter = "授权文件|license.json;*.json|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        // 先校验选中的文件（不落地，防止把坏文件复制进去）
        var err = LicenseManager.Validate(dlg.FileName);
        if (err != LicenseError.None)
        {
            TxtActivateResult.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            TxtActivateResult.Text = "✗ 该文件无法激活：" + LicenseManager.ErrorText(err);
            return;
        }

        // 通过后复制到软件目录
        try
        {
            var dest = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "license.json");
            File.Copy(dlg.FileName, dest, true);
        }
        catch (Exception ex)
        {
            TxtActivateResult.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            TxtActivateResult.Text = "✗ 写入软件目录失败：" + ex.Message + "（可手动把文件放到 exe 同目录后重启）";
            return;
        }

        TxtActivateResult.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        TxtActivateResult.Text = $"✓ 激活成功！授权编号 {LicenseManager.LicenseId}，有效期至 {LicenseManager.ExpiresAtText}。即将重启软件…";

        Dispatcher.BeginInvoke(new Action(() =>
        {
            System.Threading.Thread.Sleep(1200);
            RestartApp();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RestartApp()
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (exe != null && !exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
            catch { }
        }
        Application.Current.Shutdown();
    }
}
