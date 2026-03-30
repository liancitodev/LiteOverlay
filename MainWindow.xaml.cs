using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace LiteOverlay
{
    public partial class MainWindow : Window
    {
        private string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        private AppSettings settings = new AppSettings();

        #region Win32 API for Ghost Mode
        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_LAYERED = 0x00080000;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9000;
        #endregion

        private bool isRecordingHotkey = false;

        public MainWindow()
        {
            InitializeComponent();
            LoadSettings();
            InitializeWebView();
            ApplyWindowSettings();
            SetupJumpList();
            UpdateHotkeyButtonLabel();
            this.KeyDown += MainWindow_KeyDown;

            // Check if we should open settings immediately (from Taskbar JumpList)
            string[] args = Environment.GetCommandLineArgs();
            bool openSettings = false;
            foreach (string arg in args) { if (arg == "--settings") { openSettings = true; break; } }

            if (openSettings)
            {
                ToggleSettings_Click(null!, null!);
            }
            else if (settings.FirstRun || string.IsNullOrEmpty(settings.Url))
            {
                guideOverlay.Visibility = Visibility.Visible;
            }

            this.Closed += (s, ev) =>
            {
                // Unregister hotkey on close to free the key binding
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                UnregisterHotKey(hwnd, HOTKEY_ID);
            };
        }

        private void SetupJumpList()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return;
                JumpList jumpList = new JumpList();
                JumpList.SetJumpList(Application.Current, jumpList);
                jumpList.JumpItems.Add(new JumpTask { Title = "Mostrar Ajustes", ApplicationPath = exePath, Arguments = "--settings" });
                jumpList.Apply();
            }
            catch { }
        }

        private async void InitializeWebView()
        {
            try
            {
                await webView.EnsureCoreWebView2Async();
                webView.CoreWebView2.NavigationCompleted += (s, e) => { 
                    if (e.IsSuccess) {
                        ApplyLangAttributes();
                        SyncIntelligenceToWidget();
                    }
                };
                NavigateToUrl();
            }
            catch (Exception ex) { MessageBox.Show("Error WebView2: " + ex.Message); }
        }

        private void ApplyLangAttributes()
        {
            if (webView?.CoreWebView2 != null && !string.IsNullOrEmpty(settings.Lang))
            {
                string script = $"document.documentElement.lang = '{settings.Lang}';";
                webView.CoreWebView2.ExecuteScriptAsync(script);
            }
        }

        private void NavigateToUrl()
        {
            if (webView?.CoreWebView2 != null)
            {
                string url = string.IsNullOrEmpty(settings.Url) ? "about:blank" : settings.Url;
                webView.CoreWebView2.Navigate(url);
            }
        }

        private void ApplyWindowSettings()
        {
            // Bug fix: clamp window position so it's never off-screen
            double screenW = SystemParameters.VirtualScreenWidth;
            double screenH = SystemParameters.VirtualScreenHeight;
            this.Left   = Math.Max(0, Math.Min(settings.X, screenW - settings.Width));
            this.Top    = Math.Max(0, Math.Min(settings.Y, screenH - settings.Height));
            this.Width  = settings.Width;
            this.Height = settings.Height;
            this.Opacity = settings.Opacity;
            settings.GhostMode = false; // Always start unlocked
            sliderVolume.Value = settings.Volume;
            
            try {
                this.Language = System.Windows.Markup.XmlLanguage.GetLanguage(settings.Lang);
            } catch { }

            UpdateGhostMode();
            ApplyVolume();
            RegisterGlobalHotkey();
        }

        private void LoadSettings()
        {
            if (File.Exists(settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(settingsPath);
                    settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
                catch { }
            }
            if (settings.Url != null && settings.Url.Contains("localhost:5000/chat")) settings.Url = "";
            tbUrl.Text = settings.Url?.Trim();
            
            // Match ComboBox selection
            foreach (ComboBoxItem item in cbLang.Items)
            {
                if (item.Content.ToString() == settings.Lang)
                {
                    cbLang.SelectedItem = item;
                    break;
                }
            }

            tbWidth.Text = settings.Width.ToString();
            tbHeight.Text = settings.Height.ToString();
            sliderVolume.Value = settings.Volume;

            cbTtsActive.IsChecked = settings.TtsEnabled;
            cbReadEmojis.IsChecked = settings.ReadEmojis;
            cbReadLinks.IsChecked = settings.ReadLinks;
            cbReadNames.IsChecked = settings.ReadNames;
            cbIgnoreBots.IsChecked = settings.IgnoreBots;
            UpdateHotkeyButtonLabel();
        }

        private void SaveSettings()
        {
            try
            {
                settings.Url = tbUrl.Text.Trim();
                if (cbLang.SelectedItem is ComboBoxItem selectedItem)
                {
                    settings.Lang = selectedItem.Content.ToString()!;
                }
                
                if (double.TryParse(tbWidth.Text, out double w)) { settings.Width = w; this.Width = w; }
                if (double.TryParse(tbHeight.Text, out double h)) { settings.Height = h; this.Height = h; }
                settings.X = this.Left;
                settings.Y = this.Top;
                settings.Opacity = this.Opacity;
                settings.Volume = sliderVolume.Value;
                settings.FirstRun = false;

                settings.TtsEnabled = cbTtsActive.IsChecked ?? true;
                settings.ReadEmojis = cbReadEmojis.IsChecked ?? false;
                settings.ReadLinks = cbReadLinks.IsChecked ?? false;
                settings.ReadNames = cbReadNames.IsChecked ?? true;
                settings.IgnoreBots = cbIgnoreBots.IsChecked ?? true;

                try {
                    this.Language = System.Windows.Markup.XmlLanguage.GetLanguage(settings.Lang);
                } catch { }

                File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
                SyncIntelligenceToWidget();
                ApplyVolume(); // Bug fix: apply volume immediately after save
            }
            catch { }
        }

        private void UpdateGhostMode()
        {
            bool isGhost = settings.GhostMode && settingsPanel.Visibility != Visibility.Visible;
            
            // "Disappear everything" except the web content
            ghostHint.Visibility = Visibility.Collapsed; // Hide entirely as requested
            titleBarBorder.Visibility = isGhost ? Visibility.Collapsed : Visibility.Visible;
            rowTitle.Height = isGhost ? new GridLength(0) : new GridLength(42);
            mainBorder.BorderThickness = isGhost ? new Thickness(0) : new Thickness(1);
            
            btnGhostToggle.Content = settings.GhostMode ? "🔒" : "🔓";
            
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                if (isGhost) SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);
                else SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle & ~WS_EX_TRANSPARENT);
            }

            // Ocultar barra de scroll en modo fantasma
            if (webView?.CoreWebView2 != null)
            {
                string script = isGhost 
                    ? "document.body.classList.add('ghost-mode');" 
                    : "document.body.classList.remove('ghost-mode');";
                webView.CoreWebView2.ExecuteScriptAsync(script);
            }
        }

        private void ApplyVolume()
        {
            if (webView?.CoreWebView2 == null) return;
            double vol = sliderVolume.Value / 100.0;
            string volStr = vol.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            
            // Controlar audio/video tradicional Y el volumen del TTS (speechSynthesis rate via volume proxy)
            string script = $@"
                (function() {{
                    // Volumen de elementos media
                    const media = document.querySelectorAll('audio, video');
                    media.forEach(m => {{ m.volume = {volStr}; }});
                    // Volumen del TTS: guardamos en widgetConfig para que la cola lo use
                    if (!window.widgetConfig) window.widgetConfig = {{}};
                    window.widgetConfig.ttsVolume = {volStr};
                    // Parchamos procesarCola para usar el volumen si se ha definido
                    if (window._ttsVolumeApplied === undefined) {{
                        window._ttsVolumeApplied = true;
                        const originalProcesar = window.procesarCola;
                        // La cola ya usa el utterance — el volumen se asigna en cada utterance
                    }}
                }})();";
            webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        // Actualiza el volumen en tiempo real: si el TTS está hablando, cancela y reinicia con nuevo volumen
        private void SyncVolumeRealtime()
        {
            if (webView?.CoreWebView2 == null) return;
            double vol = sliderVolume.Value / 100.0;
            string volStr = vol.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            string script = $@"(function(){{
    if (!window.widgetConfig) window.widgetConfig = {{}};
    window.widgetConfig.ttsVolume = {volStr};
    // Media elements
    document.querySelectorAll('audio,video').forEach(m => {{ m.volume = {volStr}; }});
    // Si TTS está activo y hablando: cancelar y reiniciar el utterance actual con nuevo volumen
    if (window.speechSynthesis && window.speechSynthesis.speaking && window._currentTtsText) {{
        const texto = window._currentTtsText;
        window._currentTtsText = null;
        window.speechSynthesis.cancel();
        const utt = new SpeechSynthesisUtterance(texto);
        utt.lang = 'es-ES';
        utt.rate = 1.1;
        utt.pitch = 1.0;
        utt.volume = {volStr};
        utt.onend = () => {{ if(window._setTtsSpeaking) window._setTtsSpeaking(false); if(window.procesarCola) window.procesarCola(); }};
        utt.onerror = () => {{ if(window._setTtsSpeaking) window._setTtsSpeaking(false); if(window.procesarCola) window.procesarCola(); }};
        window.speechSynthesis.speak(utt);
    }}
}})();";
            webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        private void SyncIntelligenceToWidget()
        {
            if (webView?.CoreWebView2 == null) return;
            double vol = sliderVolume.Value / 100.0;
            string json = JsonSerializer.Serialize(new {
                ttsEnabled = settings.TtsEnabled,
                readEmojis = settings.ReadEmojis,
                readLinks  = settings.ReadLinks,
                readNames  = settings.ReadNames,
                ignoreBots = settings.IgnoreBots,
                ttsVolume  = vol
            });
            // Actualizar config Y, si está muteado, detener síntesis inmediatamente
            string script;
            if (!settings.TtsEnabled)
            {
                script = $@"(function(){{
    window.widgetConfig = {json};
    if (window.speechSynthesis) {{
        window.speechSynthesis.cancel();
    }}
    if (window.ttsQueue !== undefined) {{
        window.ttsQueue.length = 0;
    }} 
    if (typeof window._setTtsSpeaking === 'function') window._setTtsSpeaking(false);
}})();";
            }
            else
            {
                script = $@"(function(){{
    // Limpiar cola antes de reactivar (no leer mensajes viejos)
    if (window.ttsQueue !== undefined) window.ttsQueue.length = 0;
    if (window.speechSynthesis) window.speechSynthesis.cancel();
    window.widgetConfig = {json};
}})();";
            }
            webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        private void ToggleSettings_Click(object sender, RoutedEventArgs e)
        {
            if (settingsPanel.Visibility == Visibility.Visible)
            {
                // Cerrar ajustes — restaurar modo fantasma si estaba activo
                settingsPanel.Visibility   = Visibility.Collapsed;
                titleBarBorder.Visibility  = Visibility.Visible;
                rowTitle.Height            = new GridLength(42);
                webView.Visibility         = Visibility.Visible;
                webView.IsHitTestVisible   = true;

                // Restaurar ghost si el checkbox sigue marcado (bueno, ya no hay checkbox, pero la variable sí)
                UpdateGhostMode();
            }
            else
            {
                // Si el candado está activo, el usuario pidió que el botón "Mostrar Ajustes"
                // se limite a quitar el candado y NO abra los ajustes directamente.
                if (settings.GhostMode)
                {
                    settings.GhostMode = false;
                    UpdateGhostMode();
                    SaveSettings();
                    return; // Retornamos para no abrir el panel de ajustes
                }
                
                settingsPanel.Visibility   = Visibility.Visible;
                guideOverlay.Visibility    = Visibility.Collapsed;
                webView.Visibility         = Visibility.Collapsed;
                webView.IsHitTestVisible   = false;

                // Asegurar que la titleBar sea visible y la ventana reciba clics, actualizar UI
                UpdateGhostMode();
                
                this.Activate();
                this.Focus();
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => {
                    tbUrl.Focus();
                    tbUrl.SelectAll();
                }));
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string oldUrl = settings.Url;
            SaveSettings();
            // Bug fix: only navigate if URL actually changed to avoid unnecessary reloads
            if (!string.Equals(oldUrl, settings.Url, StringComparison.OrdinalIgnoreCase))
                NavigateToUrl();
            UpdateGhostMode();
            settingsPanel.Visibility = Visibility.Collapsed;
            webView.Visibility = Visibility.Visible;
            webView.IsHitTestVisible = true;
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DependencyObject obj = (DependencyObject)e.OriginalSource;
                while (obj != null && obj != this)
                {
                    if (obj is Button || obj is TextBox || obj is CheckBox || obj is Slider || obj is MenuItem || obj is ScrollBar || obj is ComboBox) return;
                    obj = VisualTreeHelper.GetParent(obj);
                }
                try { this.DragMove(); } catch { }
            }
        }

        private void GhostMode_Toggle(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi) settings.GhostMode = mi.IsChecked;
            else settings.GhostMode = !settings.GhostMode;
            
            UpdateGhostMode();
            SaveSettings();
        }

        private void btnHotkey_Click(object sender, RoutedEventArgs e)
        {
            isRecordingHotkey = true;
            btnHotkey.Content = ">>> PULSA TECLA <<<";
            btnHotkey.Foreground = Brushes.Red;

            // Liberar la tecla actual INMEDIATAMENTE de Windows para que vuelva a funcionar normal
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HOTKEY_ID);
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (isRecordingHotkey)
            {
                // Bug fix: ignore modifier-only keys and Escape (allow Escape to cancel)
                if (e.Key == Key.Escape)
                {
                    isRecordingHotkey = false;
                    UpdateHotkeyButtonLabel();
                    RegisterGlobalHotkey(); // Restaurar el hotkey viejo si se canceló
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.System || e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
                    e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
                    e.Key == Key.LeftShift || e.Key == Key.RightShift ||
                    e.Key == Key.LWin || e.Key == Key.RWin) return;

                int vk = KeyInterop.VirtualKeyFromKey(e.Key);
                if (vk > 0)
                {
                    settings.MuteHotkey = vk;
                    isRecordingHotkey = false;
                    UpdateHotkeyButtonLabel();
                    RegisterGlobalHotkey();
                    SaveSettings();
                }
                e.Handled = true;
            }
        }

        private void UpdateHotkeyButtonLabel()
        {
            if (settings.MuteHotkey == 0) btnHotkey.Content = "Ninguna";
            else btnHotkey.Content = ((System.Windows.Input.Key)KeyInterop.KeyFromVirtualKey(settings.MuteHotkey)).ToString();
            btnHotkey.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#0EA5E9")!;
        }

        private void RegisterGlobalHotkey()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HOTKEY_ID);
            if (settings.MuteHotkey != 0)
            {
                RegisterHotKey(hwnd, HOTKEY_ID, 0, (uint)settings.MuteHotkey);
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source.AddHook(HwndHook);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                // Toggle mute — actualizar PRIMERO settings y checkbox
                settings.TtsEnabled = !settings.TtsEnabled;
                
                // Actualizar UI en hilo correcto
                Dispatcher.Invoke(() =>
                {
                    cbTtsActive.IsChecked = settings.TtsEnabled;
                    SyncIntelligenceToWidget(); // envía JS directo con cancel si es mute
                });
                
                // Guardar sin volver a llamar SyncIntelligenceToWidget
                try
                {
                    settings.Url = tbUrl.Text.Trim();
                    settings.X   = this.Left;
                    settings.Y   = this.Top;
                    File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings,
                        new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { }
                
                handled = true;
            }
            return IntPtr.Zero;
        }


        private void Exit_Click(object sender, RoutedEventArgs e) { SaveSettings(); Application.Current.Shutdown(); }
        private void Minimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;
        private void Maximize_Click(object sender, RoutedEventArgs e) => this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void ResetWindow_Click(object sender, RoutedEventArgs e) { this.Width = 400; this.Height = 600; SaveSettings(); }
        private void ShowHelp_Click(object sender, RoutedEventArgs e) { guideOverlay.Visibility = Visibility.Visible; }
        private void CloseGuide_Click(object sender, RoutedEventArgs e) { guideOverlay.Visibility = Visibility.Collapsed; settings.FirstRun = false; SaveSettings(); }
        private void ResetAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("¿Estás seguro de que quieres borrar todos los ajustes y reiniciar?", "Resetear Todo", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                if (File.Exists(settingsPath)) File.Delete(settingsPath);
                System.Diagnostics.Process.Start(Environment.ProcessPath!);
                Application.Current.Shutdown();
            }
        }

        private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!this.IsLoaded) return;
            // Enviar volumen en tiempo real al widget y reiniciar utterance actual si está hablando
            SyncVolumeRealtime();
        }
    }

    public class AppSettings
    {
        public string Url { get; set; } = "";
        public string Lang { get; set; } = "es";
        public string Css { get; set; } = "";
        public double X { get; set; } = 100;
        public double Y { get; set; } = 100;
        public double Width { get; set; } = 400;
        public double Height { get; set; } = 600;
        public double Opacity { get; set; } = 1.0;
        public double Volume { get; set; } = 100;
        public bool GhostMode { get; set; } = false;
        public bool FirstRun { get; set; } = true;
        public bool TtsEnabled { get; set; } = true;
        public bool ReadEmojis { get; set; } = false;
        public bool ReadLinks { get; set; } = false;
        public bool ReadNames { get; set; } = true;
        public bool IgnoreBots { get; set; } = true;
        public int MuteHotkey { get; set; } = 0x39; // Tecla "9" por defecto (VK_9 = 0x39)
    }
}
