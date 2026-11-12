using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Markdig;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DocViewer
{
    public class MainWindow : Window
    {
        private readonly WebView2 _webView;
        private readonly TextBlock _statusText;
        private string _currentFilePath = string.Empty;
        private string _currentHtml = string.Empty;
        private bool _isMarkdown = false;

        [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        public MainWindow(string initialFilePath)
        {
            Title = "轻阅 - PDF & Markdown 查看器";
            Width = 1150;
            Height = 820;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AllowDrop = true;

            var mainLayout = new DockPanel();

            // 顶部工具栏
            var toolbarBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 6, 10, 6)
            };

            var toolbarPanel = new DockPanel();
            var buttonStack = new StackPanel { Orientation = Orientation.Horizontal };

            var btnOpen = CreateToolButton("📂 打开 (Ctrl+O)", async (s, e) => await OpenFileDialogAsync());
            var btnSave = CreateToolButton("💾 另存 / 导出PDF (Ctrl+S)", async (s, e) => await SaveOrExportAsync());
            var btnPrint = CreateToolButton("🖨️ 打印预览 (Ctrl+P)", async (s, e) => await ShowPrintPreviewAsync());
            var btnAssoc = CreateToolButton("🔗 关联文件类型", (s, e) => AssociateFileTypes());

            buttonStack.Children.Add(btnOpen);
            buttonStack.Children.Add(btnSave);
            buttonStack.Children.Add(btnPrint);
            buttonStack.Children.Add(btnAssoc);

            _statusText = new TextBlock
            {
                Text = "就绪 — 支持直接拖入 .pdf 或 .md 文件",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                Margin = new Thickness(12, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            DockPanel.SetDock(buttonStack, Dock.Left);
            toolbarPanel.Children.Add(buttonStack);
            toolbarPanel.Children.Add(_statusText);
            toolbarBorder.Child = toolbarPanel;

            // WebView2 核心渲染区
            _webView = new WebView2();

            DockPanel.SetDock(toolbarBorder, Dock.Top);
            mainLayout.Children.Add(toolbarBorder);
            mainLayout.Children.Add(_webView);
            Content = mainLayout;

            // 事件绑定
            Loaded += async (s, e) => await InitializeViewerAsync(initialFilePath);
            PreviewKeyDown += OnWindowPreviewKeyDown;
            PreviewDragOver += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                }
            };
            Drop += async (s, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    await LoadDocumentAsync(files[0]);
                }
            };
        }

        private Button CreateToolButton(string text, RoutedEventHandler onClick)
        {
            var btn = new Button
            {
                Content = text,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
                Cursor = Cursors.Hand
            };
            btn.Click += onClick;
            return btn;
        }

        private async void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.O)
                {
                    e.Handled = true;
                    await OpenFileDialogAsync();
                }
                else if (e.Key == Key.S)
                {
                    e.Handled = true;
                    await SaveOrExportAsync();
                }
                else if (e.Key == Key.P)
                {
                    e.Handled = true;
                    await ShowPrintPreviewAsync();
                }
            }
        }

        private async Task InitializeViewerAsync(string initialFilePath)
        {
            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DocViewer_WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await _webView.EnsureCoreWebView2Async(env);

                if (!string.IsNullOrWhiteSpace(initialFilePath) && File.Exists(initialFilePath))
                {
                    await LoadDocumentAsync(initialFilePath);
                }
                else
                {
                    _webView.CoreWebView2.NavigateToString(GetWelcomeHtml());
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("初始化 WebView2 渲染引擎失败，请确保系统为 Windows 10/11 且已安装 Edge WebView2 运行时。\n\n详细信息: " + ex.Message,
                    "运行环境提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task OpenFileDialogAsync()
        {
            var ofd = new OpenFileDialog
            {
                Title = "打开 PDF 或 Markdown 文档",
                Filter = "支持的文档 (*.pdf;*.md;*.markdown)|*.pdf;*.md;*.markdown|PDF 文档 (*.pdf)|*.pdf|Markdown 文档 (*.md;*.markdown)|*.md;*.markdown|所有文件 (*.*)|*.*"
            };

            if (ofd.ShowDialog() == true)
            {
                await LoadDocumentAsync(ofd.FileName);
            }
        }

        private async Task LoadDocumentAsync(string filePath)
        {
            if (!File.Exists(filePath)) return;

            if (_webView.CoreWebView2 == null)
            {
                await _webView.EnsureCoreWebView2Async();
            }

            _currentFilePath = Path.GetFullPath(filePath);
            string ext = Path.GetExtension(_currentFilePath).ToLowerInvariant();
            Title = Path.GetFileName(_currentFilePath) + " - 轻阅";
            _statusText.Text = _currentFilePath;

            if (ext == ".pdf")
            {
                _isMarkdown = false;
                _currentHtml = string.Empty;
                _webView.CoreWebView2.Navigate(new Uri(_currentFilePath).AbsoluteUri);
            }
            else if (ext == ".md" || ext == ".markdown" || ext == ".txt")
            {
                _isMarkdown = true;
                string folder = Path.GetDirectoryName(_currentFilePath) ?? AppContext.BaseDirectory;
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "local.md.assets", folder, CoreWebView2HostResourceAccessKind.Allow);

                string mdText = ReadTextWithFallback(_currentFilePath);
                var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
                string htmlBody = Markdown.ToHtml(mdText, pipeline);
                _currentHtml = BuildMarkdownHtml(Path.GetFileName(_currentFilePath), htmlBody);
                _webView.CoreWebView2.NavigateToString(_currentHtml);
            }
            else
            {
                MessageBox.Show("暂不支持该文件格式，请选择 .pdf 或 .md 文件。", "格式提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private static string ReadTextWithFallback(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(936).GetString(bytes);
            }
        }

        private async Task SaveOrExportAsync()
        {
            if (string.IsNullOrEmpty(_currentFilePath) || !File.Exists(_currentFilePath))
            {
                MessageBox.Show("请先打开一个 PDF 或 Markdown 文档。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!_isMarkdown)
            {
                var sfd = new SaveFileDialog
                {
                    Title = "另存为 PDF 副本",
                    Filter = "PDF 文档 (*.pdf)|*.pdf",
                    FileName = Path.GetFileName(_currentFilePath)
                };
                if (sfd.ShowDialog() == true)
                {
                    if (!string.Equals(_currentFilePath, sfd.FileName, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(_currentFilePath, sfd.FileName, true);
                    }
                    _statusText.Text = "已成功另存至: " + sfd.FileName;
                }
            }
            else
            {
                var sfd = new SaveFileDialog
                {
                    Title = "导出或另存 Markdown 文档",
                    Filter = "导出为 PDF 文档 (*.pdf)|*.pdf|导出为独立网页 (*.html)|*.html|另存为 Markdown 源文件 (*.md)|*.md",
                    FileName = Path.GetFileNameWithoutExtension(_currentFilePath) + ".pdf"
                };

                if (sfd.ShowDialog() == true)
                {
                    string targetExt = Path.GetExtension(sfd.FileName).ToLowerInvariant();
                    if (targetExt == ".pdf")
                    {
                        bool ok = await _webView.CoreWebView2.PrintToPdfAsync(sfd.FileName);
                        _statusText.Text = ok ? "已导出 PDF: " + sfd.FileName : "导出 PDF 失败";
                    }
                    else if (targetExt == ".html")
                    {
                        File.WriteAllText(sfd.FileName, _currentHtml, Encoding.UTF8);
                        _statusText.Text = "已导出 HTML: " + sfd.FileName;
                    }
                    else
                    {
                        if (!string.Equals(_currentFilePath, sfd.FileName, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Copy(_currentFilePath, sfd.FileName, true);
                        }
                        _statusText.Text = "已另存 Markdown: " + sfd.FileName;
                    }
                }
            }
        }

        private async Task ShowPrintPreviewAsync()
        {
            if (string.IsNullOrEmpty(_currentFilePath) || _webView.CoreWebView2 == null)
            {
                MessageBox.Show("请先打开一个需要打印的文档。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                _webView.CoreWebView2.ShowPrintUI();
            }
            catch
            {
                await _webView.CoreWebView2.ExecuteScriptAsync("window.print();");
            }
        }

        private void AssociateFileTypes()
        {
            try
            {
                string exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    MessageBox.Show("无法获取当前程序路径。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                string progId = "DocViewer.Document";
                using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}"))
                {
                    progKey.SetValue("", "轻阅 PDF 与 Markdown 文档");
                    using (var iconKey = progKey.CreateSubKey("DefaultIcon"))
                    {
                        iconKey.SetValue("", $"\"{exePath}\",0");
                    }
                    using (var cmdKey = progKey.CreateSubKey(@"shell\open\command"))
                    {
                        cmdKey.SetValue("", $"\"{exePath}\" \"%1\"");
                    }
                }

                string[] extensions = new[] { ".md", ".markdown", ".pdf" };
                foreach (string ext in extensions)
                {
                    using (var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}"))
                    {
                        extKey.SetValue("", progId);
                        using (var openWith = extKey.CreateSubKey("OpenWithProgids"))
                        {
                            openWith.SetValue(progId, string.Empty);
                        }
                    }
                }

                SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);

                var result = MessageBox.Show(
                    "已成功将本程序注册为 .md 和 .pdf 的关联打开程序（免管理员权限）！\n\n" +
                    "是否立即打开 Windows【默认应用设置】面板，将本程序锁定为默认双击打开方式？",
                    "文件关联成功",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("文件关联失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string GetWelcomeHtml()
        {
            return @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<style>
  body { font-family: 'Segoe UI', 'Microsoft YaHei', sans-serif; background: #F9FAFB; color: #374151; display: flex; align-items: center; justify-content: center; height: 90vh; margin: 0; }
  .card { background: white; padding: 40px 50px; border-radius: 12px; box-shadow: 0 4px 20px rgba(0,0,0,0.06); text-align: center; max-width: 480px; }
  h2 { margin-top: 0; color: #111827; }
  p { line-height: 1.6; color: #6B7280; }
  .kbd { background: #F3F4F6; border: 1px solid #D1D5DB; border-radius: 4px; padding: 2px 6px; font-size: 0.9em; }
</style>
</head>
<body>
  <div class='card'>
    <h2>轻阅 PDF & Markdown 查看器</h2>
    <p>点击顶部 <b>打开</b> 按钮（或按 <span class='kbd'>Ctrl+O</span>），也可以直接将 <b>.pdf</b> 或 <b>.md</b> 文件拖拽到此窗口中阅读。</p>
  </div>
</body>
</html>";
        }

        private static string BuildMarkdownHtml(string title, string htmlBody)
        {
            return $@"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<base href='https://local.md.assets/' />
<title>{title}</title>
<style>
  body {{
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Microsoft YaHei', Helvetica, Arial, sans-serif;
    line-height: 1.7;
    color: #24292f;
    background-color: #ffffff;
    max-width: 880px;
    margin: 0 auto;
    padding: 36px 28px;
  }}
  h1, h2, h3, h4 {{ margin-top: 24px; margin-bottom: 16px; font-weight: 600; line-height: 1.25; }}
  h1 {{ padding-bottom: 0.3em; font-size: 2em; border-bottom: 1px solid #d0d7de; }}
  h2 {{ padding-bottom: 0.3em; font-size: 1.5em; border-bottom: 1px solid #d0d7de; }}
  code {{ background-color: #afb8c133; padding: 0.2em 0.4em; border-radius: 6px; font-family: Consolas, monospace; font-size: 85%; }}
  pre {{ background-color: #f6f8fa; padding: 16px; overflow: auto; border-radius: 6px; line-height: 1.45; }}
  pre code {{ background-color: transparent; padding: 0; }}
  blockquote {{ padding: 0 1em; color: #57606a; border-left: 0.25em solid #d0d7de; margin: 0 0 16px 0; }}
  table {{ border-spacing: 0; border-collapse: collapse; width: 100%; margin-bottom: 16px; }}
  table th, table td {{ padding: 8px 13px; border: 1px solid #d0d7de; }}
  table tr:nth-child(2n) {{ background-color: #f6f8fa; }}
  img {{ max-width: 100%; height: auto; }}
  @media print {{ body {{ max-width: 100%; padding: 0; }} }}
</style>
</head>
<body>
{htmlBody}
</body>
</html>";
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            string initialFile = (args != null && args.Length > 0) ? args[0] : string.Empty;
            var app = new Application();
            var win = new MainWindow(initialFile);
            app.Run(win);
        }
    }
}
