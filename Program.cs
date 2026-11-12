using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using Markdig;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DocViewer
{
    public class MainWindow : Window
    {
        private readonly WebView2 _webView;
        private readonly TextEditor _editor;
        private readonly TextBlock _statusText;
        private readonly ColumnDefinition _editorColumn;
        private readonly ColumnDefinition _splitterColumn;
        private readonly GridSplitter _splitter;
        private readonly Button _btnEditToggle;
        private readonly DispatcherTimer _debounceTimer;

        private string _currentFilePath = string.Empty;
        private string _currentHtml = string.Empty;
        private bool _isMarkdown = false;
        private bool _isEditMode = false;
        private bool _isDirty = false;
        private bool _suppressTextChanged = false;
        private bool _markdownShellLoaded = false;

        [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        public MainWindow(string initialFilePath)
        {
            Title = "轻阅 - PDF & Markdown 查看/编辑器";
            Width = 1280;
            Height = 850;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AllowDrop = true;

            var mainLayout = new DockPanel();

            // 1. 顶部工具栏
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
            var btnNewMd = CreateToolButton("📄 新建 MD (Ctrl+N)", async (s, e) => await CreateNewMarkdownAsync());
            _btnEditToggle = CreateToolButton("✏️ 编辑模式 (Ctrl+E)", (s, e) => ToggleEditMode());
            var btnSave = CreateToolButton("💾 保存 (Ctrl+S)", async (s, e) => await SaveDocumentAsync());
            var btnExport = CreateToolButton("📤 导出 PDF/HTML", async (s, e) => await ExportDocumentAsync());
            var btnPrint = CreateToolButton("🖨️ 打印预览 (Ctrl+P)", async (s, e) => await ShowPrintPreviewAsync());
            var btnAssoc = CreateToolButton("🔗 关联文件", (s, e) => AssociateFileTypes());

            buttonStack.Children.Add(btnOpen);
            buttonStack.Children.Add(btnNewMd);
            buttonStack.Children.Add(_btnEditToggle);
            buttonStack.Children.Add(btnSave);
            buttonStack.Children.Add(btnExport);
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

            // 2. 主体双栏区域（左侧 AvalonEdit 代码编辑器 + 中间分隔条 + 右侧 WebView2 预览）
            var contentGrid = new Grid();
            _editorColumn = new ColumnDefinition { Width = new GridLength(0) };
            _splitterColumn = new ColumnDefinition { Width = new GridLength(0) };
            var previewColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };

            contentGrid.ColumnDefinitions.Add(_editorColumn);
            contentGrid.ColumnDefinitions.Add(_splitterColumn);
            contentGrid.ColumnDefinitions.Add(previewColumn);

            // 配置带行号与语法高亮的 AvalonEdit 编辑器
            _editor = new TextEditor
            {
                ShowLineNumbers = true,
                WordWrap = true,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 14,
                Padding = new Thickness(8),
                Background = new SolidColorBrush(Color.FromRgb(250, 250, 250)),
                Foreground = new SolidColorBrush(Color.FromRgb(36, 41, 47)),
                LineNumbersForeground = new SolidColorBrush(Color.FromRgb(140, 149, 159)),
                SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("MarkDown")
                                     ?? HighlightingManager.Instance.GetDefinitionByExtension(".md"),
                Visibility = Visibility.Collapsed
            };
            _editor.Options.ConvertTabsToSpaces = true;
            _editor.Options.IndentationSize = 4;
            _editor.Options.EnableHyperlinks = false;
            _editor.TextChanged += OnEditorTextChanged;

            _splitter = new GridSplitter
            {
                Width = 5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
                ShowsPreview = true,
                Visibility = Visibility.Collapsed
            };

            _webView = new WebView2();

            Grid.SetColumn(_editor, 0);
            Grid.SetColumn(_splitter, 1);
            Grid.SetColumn(_webView, 2);

            contentGrid.Children.Add(_editor);
            contentGrid.Children.Add(_splitter);
            contentGrid.Children.Add(_webView);

            DockPanel.SetDock(toolbarBorder, Dock.Top);
            mainLayout.Children.Add(toolbarBorder);
            mainLayout.Children.Add(contentGrid);
            Content = mainLayout;

            // 3. 实时预览防抖定时器（打字停顿 220ms 后静默刷新右侧 DOM）
            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _debounceTimer.Tick += async (s, e) =>
            {
                _debounceTimer.Stop();
                await UpdateLivePreviewAsync();
            };

            // 4. 事件绑定
            Loaded += async (s, e) => await InitializeViewerAsync(initialFilePath);
            PreviewKeyDown += OnWindowPreviewKeyDown;
            Closing += OnWindowClosing;

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
                    if (ConfirmDiscardChanges())
                    {
                        await LoadDocumentAsync(files[0]);
                    }
                }
            };
        }

        private Button CreateToolButton(string text, RoutedEventHandler onClick)
        {
            var btn = new Button
            {
                Content = text,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 0),
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
                else if (e.Key == Key.N)
                {
                    e.Handled = true;
                    await CreateNewMarkdownAsync();
                }
                else if (e.Key == Key.E)
                {
                    e.Handled = true;
                    ToggleEditMode();
                }
                else if (e.Key == Key.S)
                {
                    e.Handled = true;
                    await SaveDocumentAsync();
                }
                else if (e.Key == Key.P)
                {
                    e.Handled = true;
                    await ShowPrintPreviewAsync();
                }
            }
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (!ConfirmDiscardChanges())
            {
                e.Cancel = true;
            }
        }

        private bool ConfirmDiscardChanges()
        {
            if (!_isDirty) return true;

            var result = MessageBox.Show(
                "当前 Markdown 文档有尚未保存的修改，是否在继续前保存？",
                "未保存提醒",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel) return false;
            if (result == MessageBoxResult.Yes)
            {
                return SaveMarkdownToFileSync();
            }
            return true;
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
                MessageBox.Show("初始化 WebView2 渲染引擎失败，请确保系统为 Windows 10/11。\n\n详细信息: " + ex.Message,
                    "运行环境提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task OpenFileDialogAsync()
        {
            if (!ConfirmDiscardChanges()) return;

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

        private async Task CreateNewMarkdownAsync()
        {
            if (!ConfirmDiscardChanges()) return;

            if (_webView.CoreWebView2 == null)
            {
                await _webView.EnsureCoreWebView2Async();
            }

            _currentFilePath = string.Empty;
            _isMarkdown = true;
            _isDirty = false;

            string starterMd = "# 未命名文档\n\n在此输入 **Markdown** 内容，右侧将实时渲染预览。\n\n```csharp\nConsole.WriteLine(\"Hello Markdown!\");\n```\n";
            _suppressTextChanged = true;
            _editor.Text = starterMd;
            _suppressTextChanged = false;

            string htmlBody = RenderMarkdownToHtmlFragment(starterMd);
            _currentHtml = BuildMarkdownHtml("未命名.md", htmlBody);
            _markdownShellLoaded = true;
            _webView.CoreWebView2.NavigateToString(_currentHtml);

            SetEditMode(true);
            UpdateWindowTitle();
            _statusText.Text = "新建 Markdown 文档 — 按 Ctrl+S 保存";
            _editor.Focus();
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
            _isDirty = false;

            if (ext == ".pdf")
            {
                _isMarkdown = false;
                _markdownShellLoaded = false;
                _currentHtml = string.Empty;
                SetEditMode(false);
                _webView.CoreWebView2.Navigate(new Uri(_currentFilePath).AbsoluteUri);
                UpdateWindowTitle();
                _statusText.Text = _currentFilePath;
            }
            else if (ext == ".md" || ext == ".markdown" || ext == ".txt")
            {
                _isMarkdown = true;
                string folder = Path.GetDirectoryName(_currentFilePath) ?? AppContext.BaseDirectory;
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "local.md.assets", folder, CoreWebView2HostResourceAccessKind.Allow);

                string mdText = ReadTextWithFallback(_currentFilePath);
                _suppressTextChanged = true;
                _editor.Text = mdText;
                _suppressTextChanged = false;

                string htmlBody = RenderMarkdownToHtmlFragment(mdText);
                _currentHtml = BuildMarkdownHtml(Path.GetFileName(_currentFilePath), htmlBody);
                _markdownShellLoaded = true;
                _webView.CoreWebView2.NavigateToString(_currentHtml);

                UpdateWindowTitle();
                _statusText.Text = _currentFilePath + " （按 Ctrl+E 可开启分屏编辑）";
            }
            else
            {
                MessageBox.Show("暂不支持该文件格式，请选择 .pdf 或 .md 文件。", "格式提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ToggleEditMode()
        {
            if (!_isMarkdown)
            {
                var res = MessageBox.Show("当前未打开 Markdown 文档（PDF 为只读格式）。\n是否立即新建一个空白 Markdown 文档并开始编辑？",
                    "编辑模式提示", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes)
                {
                    _ = CreateNewMarkdownAsync();
                }
                return;
            }

            SetEditMode(!_isEditMode);
        }

        private void SetEditMode(bool enable)
        {
            _isEditMode = enable && _isMarkdown;

            if (_isEditMode)
            {
                _editor.Visibility = Visibility.Visible;
                _splitter.Visibility = Visibility.Visible;
                _editorColumn.Width = new GridLength(1, GridUnitType.Star);
                _splitterColumn.Width = new GridLength(5);
                _btnEditToggle.Content = "👁️ 退出编辑 (Ctrl+E)";
                _btnEditToggle.Background = new SolidColorBrush(Color.FromRgb(219, 234, 254));
                _editor.Focus();
            }
            else
            {
                _editor.Visibility = Visibility.Collapsed;
                _splitter.Visibility = Visibility.Collapsed;
                _editorColumn.Width = new GridLength(0);
                _splitterColumn.Width = new GridLength(0);
                _btnEditToggle.Content = "✏️ 编辑模式 (Ctrl+E)";
                _btnEditToggle.Background = Brushes.White;
            }
        }

        private void OnEditorTextChanged(object sender, EventArgs e)
        {
            if (_suppressTextChanged || !_isMarkdown) return;

            if (!_isDirty)
            {
                _isDirty = true;
                UpdateWindowTitle();
            }

            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private async Task UpdateLivePreviewAsync()
        {
            if (!_isMarkdown || _webView.CoreWebView2 == null) return;

            string mdText = _editor.Text;
            string htmlBody = RenderMarkdownToHtmlFragment(mdText);
            string docTitle = string.IsNullOrEmpty(_currentFilePath) ? "未命名.md" : Path.GetFileName(_currentFilePath);
            _currentHtml = BuildMarkdownHtml(docTitle, htmlBody);

            if (_markdownShellLoaded)
            {
                try
                {
                    // 仅替换内部 DOM 节点，保留右侧滚动条位置且不闪屏
                    string jsonHtml = JsonSerializer.Serialize(htmlBody);
                    await _webView.CoreWebView2.ExecuteScriptAsync(
                        $"var c = document.getElementById('md-root'); if(c) c.innerHTML = {jsonHtml};");
                    return;
                }
                catch
                {
                    // 回退到完整加载
                }
            }

            _markdownShellLoaded = true;
            _webView.CoreWebView2.NavigateToString(_currentHtml);
        }

        private static string RenderMarkdownToHtmlFragment(string mdText)
        {
            var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
            return Markdown.ToHtml(mdText ?? string.Empty, pipeline);
        }

        private void UpdateWindowTitle()
        {
            string name = string.IsNullOrEmpty(_currentFilePath)
                ? (_isMarkdown ? "未命名.md" : "轻阅")
                : Path.GetFileName(_currentFilePath);

            string dirtyMark = _isDirty ? "* " : "";
            Title = $"{dirtyMark}{name} - 轻阅 PDF & Markdown 查看/编辑器";
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

        private async Task SaveDocumentAsync()
        {
            if (_isMarkdown)
            {
                SaveMarkdownToFileSync();
                await Task.CompletedTask;
            }
            else if (!string.IsNullOrEmpty(_currentFilePath) && File.Exists(_currentFilePath))
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
                    _statusText.Text = "已成功另存 PDF 至: " + sfd.FileName;
                }
            }
            else
            {
                MessageBox.Show("当前没有可保存的文档。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private bool SaveMarkdownToFileSync()
        {
            if (string.IsNullOrEmpty(_currentFilePath))
            {
                var sfd = new SaveFileDialog
                {
                    Title = "保存 Markdown 文档",
                    Filter = "Markdown 文档 (*.md)|*.md|所有文件 (*.*)|*.*",
                    FileName = "未命名.md"
                };
                if (sfd.ShowDialog() != true) return false;
                _currentFilePath = sfd.FileName;

                if (_webView.CoreWebView2 != null)
                {
                    string folder = Path.GetDirectoryName(_currentFilePath) ?? AppContext.BaseDirectory;
                    _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "local.md.assets", folder, CoreWebView2HostResourceAccessKind.Allow);
                }
            }

            File.WriteAllText(_currentFilePath, _editor.Text, new UTF8Encoding(false));
            _isDirty = false;
            UpdateWindowTitle();
            _statusText.Text = "已保存修改 (" + DateTime.Now.ToString("HH:mm:ss") + "): " + _currentFilePath;
            return true;
        }

        private async Task ExportDocumentAsync()
        {
            if (!_isMarkdown && string.IsNullOrEmpty(_currentFilePath))
            {
                MessageBox.Show("请先打开或新建一个文档。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!_isMarkdown)
            {
                await SaveDocumentAsync();
                return;
            }

            string baseName = string.IsNullOrEmpty(_currentFilePath)
                ? "未命名"
                : Path.GetFileNameWithoutExtension(_currentFilePath);

            var sfd = new SaveFileDialog
            {
                Title = "导出 Markdown 为 PDF 或 HTML",
                Filter = "PDF 文档 (*.pdf)|*.pdf|独立 HTML 网页 (*.html)|*.html|另存 Markdown 副本 (*.md)|*.md",
                FileName = baseName + ".pdf"
            };

            if (sfd.ShowDialog() == true)
            {
                string targetExt = Path.GetExtension(sfd.FileName).ToLowerInvariant();
                if (targetExt == ".pdf")
                {
                    await UpdateLivePreviewAsync();
                    bool ok = await _webView.CoreWebView2.PrintToPdfAsync(sfd.FileName);
                    _statusText.Text = ok ? "已成功导出 PDF: " + sfd.FileName : "导出 PDF 失败";
                }
                else if (targetExt == ".html")
                {
                    await UpdateLivePreviewAsync();
                    File.WriteAllText(sfd.FileName, _currentHtml, Encoding.UTF8);
                    _statusText.Text = "已成功导出 HTML: " + sfd.FileName;
                }
                else
                {
                    File.WriteAllText(sfd.FileName, _editor.Text, new UTF8Encoding(false));
                    _statusText.Text = "已另存 Markdown 副本: " + sfd.FileName;
                }
            }
        }

        private async Task ShowPrintPreviewAsync()
        {
            if ((string.IsNullOrEmpty(_currentFilePath) && !_isMarkdown) || _webView.CoreWebView2 == null)
            {
                MessageBox.Show("请先打开或新建一个需要打印的文档。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
  .card { background: white; padding: 40px 50px; border-radius: 12px; box-shadow: 0 4px 20px rgba(0,0,0,0.06); text-align: center; max-width: 520px; }
  h2 { margin-top: 0; color: #111827; }
  p { line-height: 1.7; color: #6B7280; }
  .kbd { background: #F3F4F6; border: 1px solid #D1D5DB; border-radius: 4px; padding: 2px 6px; font-size: 0.9em; color: #111827; }
</style>
</head>
<body>
  <div class='card'>
    <h2>轻阅 PDF & Markdown 查看/编辑器</h2>
    <p>
      • 点击 <b>打开</b> (<span class='kbd'>Ctrl+O</span>) 或直接将 <b>.pdf / .md</b> 拖入窗口阅读<br/>
      • 点击 <b>新建 MD</b> (<span class='kbd'>Ctrl+N</span>) 或 <b>编辑模式</b> (<span class='kbd'>Ctrl+E</span>) 开启带行号与语法高亮的双栏实时编辑
    </p>
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
<div id='md-root'>
{htmlBody}
</div>
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
