using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace WeCrack;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // When maximized, WindowChrome's default resize borders make the window overhang the
        // work area by ResizeBorderThickness. Compensate by tightening the margin dynamically.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
            {
                RootBorder.Margin = new Thickness(SystemParameters.WindowResizeBorderThickness.Left);
                UpdateMaxIcon(true);
            }
            else
            {
                RootBorder.Margin = new Thickness(0);
                UpdateMaxIcon(false);
            }
        };

        Loaded += (_, _) =>
        {
            AppendLog(new LogLine(LogLevel.Info, "WeCrack ready. hit VERIFY to inspect the current install."));
            RunVerify();
        };
    }

    // ─── window chrome ──────────────────────────────────────────────────────────

    private void OnMinimize(object sender, RoutedEventArgs e)        => WindowState = WindowState.Minimized;
    private void OnClose(object sender, RoutedEventArgs e)           => Close();

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    // Swap the maximize glyph between "one square" (will maximize) and "two overlapping squares" (will restore).
    private void UpdateMaxIcon(bool isMaximized)
    {
        // Icon lives inside BtnMaximize.Content as a Path named MaxIcon.
        if (BtnMaximize.Content is Path p)
        {
            p.Data = isMaximized
                ? Geometry.Parse("M2,0 L10,0 10,8 M0,2 L8,2 8,10 0,10 Z") // two-square restore glyph
                : Geometry.Parse("M0,0 L10,0 10,10 0,10 Z");              // single square
        }
        BtnMaximize.ToolTip = isMaximized ? "Restore" : "Maximize";
    }

    // ─── action buttons ─────────────────────────────────────────────────────────

    private void OnVerify(object sender, RoutedEventArgs e)            => RunVerify();
    private async void OnInstall(object sender, RoutedEventArgs e)     => await RunActionAsync("INSTALL",   CrackService.Install);
    private async void OnUninstall(object sender, RoutedEventArgs e)   => await RunActionAsync("UNINSTALL", CrackService.Uninstall);

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        LogPanel.Children.Clear();
        AppendLog(new LogLine(LogLevel.Info, "log cleared"));
    }

    // ─── orchestration ──────────────────────────────────────────────────────────

    private async void RunVerify()
    {
        SetBusy(true, "verifying");
        try
        {
            var report = await Task.Run(CrackService.Verify);
            ApplyReport(report);
            foreach (var line in report.Log) AppendLog(line);
        }
        finally { SetBusy(false); }
    }

    private async Task RunActionAsync(string label, Func<ActionReport> action)
    {
        SetBusy(true, label.ToLowerInvariant());
        AppendLog(new LogLine(LogLevel.Info, $">>> {label}"));
        try
        {
            var result = await Task.Run(action);
            foreach (var line in result.Log) AppendLog(line);
            AppendLog(new LogLine(result.Success ? LogLevel.Ok : LogLevel.Error,
                                  $"<<< {label} {(result.Success ? "OK" : "FAILED")}"));
            var refreshed = await Task.Run(CrackService.Verify);
            ApplyReport(refreshed);
        }
        finally { SetBusy(false); }
    }

    // ─── UI updates ─────────────────────────────────────────────────────────────

    private void ApplyReport(VerifyReport r)
    {
        LblRoot.Text    = string.IsNullOrEmpty(r.InstallRoot) ? "—" : r.InstallRoot;
        LblVersion.Text = string.IsNullOrEmpty(r.VersionDir)  ? "—" : System.IO.Path.GetFileName(r.VersionDir);

        SetFile(LblWandState,   LblWandSize,   LblWandSha,   r.WandExe);
        SetFile(LblAsarState,   LblAsarSize,   LblAsarSha,   r.AppAsar);
        SetFile(LblWandBkState, LblWandBkSize, LblWandBkSha, r.WandExeBackup);
        SetFile(LblAsarBkState, LblAsarBkSize, LblAsarBkSha, r.AppAsarBackup);

        LblFuseOffset.Text = r.FuseOffset == 0 ? "—" : $"0x{r.FuseOffset:X}  (byte 0x{r.FuseValue:X2})";
        LblFuseState.Text  = r.FuseState;
        LblFuseState.Foreground = r.FuseState switch
        {
            "ENABLED"  => (Brush)FindResource("Brush.Fg.Amber"),
            "DISABLED" => (Brush)FindResource("Brush.Fg.Green"),
            _          => (Brush)FindResource("Brush.Fg.Red"),
        };

        (string label, string hint, Brush brush) = r.State switch
        {
            InstallState.NotFound => ("NOT FOUND", "Couldn't find WeMod / Wand automatically. Click Browse below and point at your WeMod folder.", (Brush)FindResource("Brush.Fg.Red")),
            InstallState.Clean    => ("CLEAN",     "Never patched. Press [ 1 ] INSTALL to unlock Pro.",              (Brush)FindResource("Brush.Fg.Cyan")),
            InstallState.Ready    => ("READY",     "Backups exist and match live. Press [ 1 ] INSTALL to apply.",    (Brush)FindResource("Brush.Fg.Cyan")),
            InstallState.Patched  => ("PATCHED",   "Pro unlocked. Press [ 2 ] UNINSTALL to revert.",                 (Brush)FindResource("Brush.Fg.Green")),
            _                     => ("MIXED",     "State is genuinely inconsistent — inspect files above.",         (Brush)FindResource("Brush.Fg.Red")),
        };
        LblStatus.Text = label;
        LblStatus.Foreground = brush;
        LblStatusHint.Text = hint;

        // Browse button only appears when we couldn't find WeMod on our own.
        BtnBrowse.Visibility = r.State == InstallState.NotFound ? Visibility.Visible : Visibility.Collapsed;

        // Also disable INSTALL/UNINSTALL in NotFound state — they'd just throw the same error.
        if (r.State == InstallState.NotFound)
        {
            BtnInstall.IsEnabled = false;
            BtnUninstall.IsEnabled = false;
        }
        else
        {
            BtnInstall.IsEnabled = true;
            BtnUninstall.IsEnabled = true;
        }

        LblTime.Text = $"last check {DateTime.Now:HH:mm:ss}";
    }

    // ─── manual install-folder browse (fallback for non-standard installs) ─────

    private void OnBrowseInstall(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title              = "Point WeCrack at your WeMod install folder",
            Multiselect        = false,
            ValidateNames      = true,
            InitialDirectory   = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wand"),
        };
        if (dlg.ShowDialog(this) != true) return;

        var picked = dlg.FolderName;
        AppendLog(new LogLine(LogLevel.Info, $"user selected: {picked}"));

        // Test-load first so we can give an immediate thumbs-up/down instead of persisting a bad path.
        try
        {
            _ = WandInstall.Discover(picked);
        }
        catch (WandInstallNotFoundException nf)
        {
            AppendLog(new LogLine(LogLevel.Error, "that folder doesn't look like a WeMod install."));
            foreach (var a in nf.Attempts) AppendLog(new LogLine(LogLevel.Info, $"tried  {a}"));
            AppendLog(new LogLine(LogLevel.Info, "expected either the install root (contains 'app-XX.YY.Z' folders) or a specific 'app-XX.YY.Z' folder itself."));
            return;
        }

        // Persist and refresh.
        var cfg = Config.Load();
        cfg.OverrideInstallRoot = picked;
        cfg.Save();
        AppendLog(new LogLine(LogLevel.Ok, "override saved. re-verifying..."));
        RunVerify();
    }

    private static void SetFile(TextBlock state, TextBlock size, TextBlock sha, FileStat stat)
    {
        if (!stat.Exists)
        {
            state.Text = "MISSING";
            state.Foreground = (Brush)Application.Current.FindResource("Brush.Fg.Dim");
            size.Text  = "—";
            sha.Text   = "—";
            return;
        }
        state.Text = "OK";
        state.Foreground = (Brush)Application.Current.FindResource("Brush.Fg.Green");
        size.Text = stat.Size.ToString("N0");
        sha.Text  = stat.Sha256Short ?? "—";
    }

    private void SetBusy(bool busy, string? label = null)
    {
        BtnInstall.IsEnabled   = !busy;
        BtnUninstall.IsEnabled = !busy;
        BtnVerify.IsEnabled    = !busy;
        BusySpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusySpinner.Text = busy ? $" WORKING… {label}" : "";
    }

    // ─── log rendering (StackPanel of TextBlocks — no wrap surprises) ──────────

    private void AppendLog(LogLine line)
    {
        var (levelColor, tag) = line.Level switch
        {
            LogLevel.Ok    => ((Brush)FindResource("Brush.Fg.Green"), "  ok  "),
            LogLevel.Warn  => ((Brush)FindResource("Brush.Fg.Amber"), " warn "),
            LogLevel.Error => ((Brush)FindResource("Brush.Fg.Red"),   "error "),
            _              => ((Brush)FindResource("Brush.Fg.Text"),  " info "),
        };

        var tb = new TextBlock
        {
            FontFamily   = (FontFamily)FindResource("Font.Mono"),
            FontSize     = 12,
            TextWrapping = TextWrapping.NoWrap,          // horizontal scroll instead of wrap — terminal-correct
            Margin       = new Thickness(0, 0, 0, 2),
        };
        tb.Inlines.Add(new Run($"{DateTime.Now:HH:mm:ss} ") { Foreground = (Brush)FindResource("Brush.Fg.Dim")   });
        tb.Inlines.Add(new Run($"[{tag}] ")                 { Foreground = levelColor                            });
        tb.Inlines.Add(new Run(line.Text)                   { Foreground = (Brush)FindResource("Brush.Fg.Text")  });

        LogPanel.Children.Add(tb);

        // Trim to the last 500 lines so long sessions don't slowly balloon the panel.
        const int MaxLines = 500;
        while (LogPanel.Children.Count > MaxLines) LogPanel.Children.RemoveAt(0);

        // Snap to the newest line. Dispatcher.BeginInvoke defers past the layout pass so ScrollToBottom
        // actually goes to the newly-measured extent, not the previous one.
        Dispatcher.BeginInvoke(new Action(() => LogScroller.ScrollToBottom()),
                               System.Windows.Threading.DispatcherPriority.Background);
    }
}
