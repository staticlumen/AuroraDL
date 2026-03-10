using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WTelegram;
using TL;

namespace auroradl;

internal sealed class MainForm : Form
{
    private static readonly Color TransparentKeyColor = Color.Magenta;
    private readonly Panel _curvePanel = new() { Dock = DockStyle.Fill, BackColor = Color.Magenta };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _captureRetryTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _visibilityTimer = new() { Interval = 350 };
    private readonly System.Windows.Forms.Timer _recognitionTimer = new() { Interval = 18000 };
    private readonly AudioLoopbackEngine _engine = new();
    private AcrCloudClient? _acrClient;
    private bool _acrInitFailedLogged;
    private WTelegram.Client? _telegram;
    private readonly object _lock = new();
    private readonly List<float> _queue = [];
    private readonly List<float> _acrBuffer = [];

    private TrackMetadata? _currentTrack;
    private SpectralAnalyzer _analyzer;
    private int _fftSize = 4096;
    private int _hopSize = 1024;
    private int _sampleRate = 48000;
    private double[]? _latestMags;

    private const int CurvePoints = 96;
    private readonly double[] _curveDb = new double[CurvePoints];
    private readonly double[] _curveDbSmoothed = new double[CurvePoints];
    private double _minHz = 30.0;
    private double _maxHz = 24000.0;
    private double _lastPeakDb = -140.0;
    private DateTime _lastAudioActivity = DateTime.UtcNow;
    private bool _overlayVisible;
    private DateTime _showUntilUtc = DateTime.MinValue;
    private string _nowPlaying = "";
    private Rectangle _downloadRect = Rectangle.Empty;
    private string _downloadStatus = "";
    private bool _recognitionBusy;
    private bool _detectionEnabled = true;
    private bool _downloadInProgress;
    private bool _followMeModeEnabled;
    private TrackMetadata? _lastPopupTrack;
    private bool _downloadsBlockedBySetup;
    private bool _appExitRequested;
    private readonly float[] _hudSpectrum = new float[48];
    private DateTime _lastHudSpectrumPushUtc = DateTime.MinValue;
    private int _activeDownloadId = -1;
    private int _nextDownloadId;
    private readonly object _downloadQueueLock = new();
    private readonly Queue<DownloadRequest> _downloadQueue = [];
    private static readonly Regex PercentRegex = new(@"(?<pct>\d{1,3}(?:\.\d+)?)%", RegexOptions.Compiled);
    private readonly DownloadHudForm _hud = new();
    private readonly SongDetectedPopup _detectedPopup = new();
    private readonly string _toolsDirectory;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    public MainForm()
    {
        EnvSettingsStore.LoadAndApply();
        _followMeModeEnabled = UiPreferencesStore.Load().FollowMeMode;
        _acrClient = AcrCloudClient.FromEnvironment();
        _toolsDirectory = ResolveToolsDirectory();

        AttachConsole(ATTACH_PARENT_PROCESS);
        Text = "Music Detector";
        ShowIcon = true;
        Icon = AppIconLoader.GetIconOrDefault();
        FormBorderStyle = FormBorderStyle.None;
        TopMost = false;
        ShowInTaskbar = false;
        Width = 1;
        Height = 1;
        StartPosition = FormStartPosition.Manual;
        BackColor = TransparentKeyColor;
        TransparencyKey = TransparentKeyColor;
        Opacity = 0.0;

        Controls.Add(_curvePanel);
        _curvePanel.Paint += (_, e) => DrawCurve(e.Graphics, _curvePanel.ClientSize);
        _curvePanel.MouseClick += OnPanelClick;

        _analyzer = new SpectralAnalyzer(_fftSize);
        InitializeCurve();

        _engine.SamplesAvailable += OnSamples;
        _engine.Error += _ => { };
        _uiTimer.Tick += (_, _) => RenderLatest();
        _captureRetryTimer.Tick += (_, _) => EnsureCaptureRunning();
        _visibilityTimer.Tick += (_, _) => UpdateOverlayVisibility();
        _recognitionTimer.Tick += async (_, _) => await TryRecognizeAsync();
        Shown += (_, _) =>
        {
            EnsureCaptureRunning();
            _captureRetryTimer.Start();
            _recognitionTimer.Start();
            Console.WriteLine($"[tools] Using tools directory: {_toolsDirectory}");
            if (_acrClient is not null) Log("ACRCloud client initialized. Recognition timer started.");
            else Log("ACRCloud client is null. Add vars in Controls to enable recognition.");
            EnsureStartupSetup();
            Hide();
        };
        Resize += (_, _) => { };
        FormClosed += (_, _) =>
        {
            _uiTimer.Stop();
            _captureRetryTimer.Stop();
            _visibilityTimer.Stop();
            _recognitionTimer.Stop();
            _engine.Dispose();
            _acrClient?.Dispose();
            _telegram?.Dispose();
            _hud.Dispose();
            _detectedPopup.Dispose();
        };

        ReconfigureBands();
        _uiTimer.Start();
        _hud.DownloadRequested += (_, _) => StartDownload(fromControlPanel: true);
        _hud.ManualDownloadRequested += track => StartDownload(fromControlPanel: true, trackOverride: track);
        _hud.DetectionToggled += enabled => SetDetectionEnabled(enabled);
        _hud.FormClosing += OnHudFormClosing;
        _hud.SetDetectionEnabled(_detectionEnabled);
        _hud.SetDetectedSong(null, false);
        _downloadsBlockedBySetup = !EnvSettingsStore.HasRequiredCredentialsInFile();
        _hud.SetDownloadsEnabled(!_downloadsBlockedBySetup);
        Hide();
    }

    private void OnHudFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_appExitRequested) return;

        e.Cancel = true;
        int pendingCount = GetPendingDownloadCount();
        if (pendingCount > 0)
        {
            var dr = MessageBox.Show(
                _hud,
                $"There are {pendingCount} pending/running download(s). Close the application anyway?",
                "Confirm Exit",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes) return;
        }

        _appExitRequested = true;
        Close();
    }

    private int GetPendingDownloadCount()
    {
        lock (_downloadQueueLock)
        {
            int queued = _downloadQueue.Count;
            int running = _downloadInProgress ? 1 : 0;
            return queued + running;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
            return cp;
        }
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            if (!_downloadRect.IsEmpty)
            {
                short x = (short)((int)m.LParam & 0xFFFF);
                short y = (short)(((int)m.LParam >> 16) & 0xFFFF);
                Point pt = PointToClient(new Point(x, y));
                if (_downloadRect.Contains(pt))
                {
                    m.Result = (IntPtr)1; // HTCLIENT
                    return;
                }
            }

            m.Result = (IntPtr)HTTRANSPARENT;
            return;
        }
        base.WndProc(ref m);
    }

    private void DockAboveTaskbar()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Width = wa.Width;
        Height = 60;
        Left = wa.Left;
        Top = wa.Bottom - Height;
        TopMost = true;
    }

    private void EnsureCaptureRunning()
    {
        if (_engine.IsRunning) return;
        try
        {
            _engine.Start();
            if (_engine.SampleRate > 0)
            {
                _sampleRate = _engine.SampleRate;
                ReconfigureBands();
            }
        }
        catch
        {
            // Retry on next tick.
        }
    }

    private void InitializeCurve()
    {
        for (int i = 0; i < CurvePoints; i++)
        {
            _curveDb[i] = -110;
            _curveDbSmoothed[i] = -110;
        }
    }

    private void ReconfigureBands()
    {
        _hopSize = _fftSize / 4;
        _analyzer = new SpectralAnalyzer(_fftSize);
        _minHz = Math.Max(30.0, _sampleRate / (double)_fftSize);
        _maxHz = _sampleRate / 2.0;
    }

    private void OnSamples(float[] chunk)
    {
        lock (_lock)
        {
            _queue.AddRange(chunk);
            _acrBuffer.AddRange(chunk);
            int maxAcrSamples = Math.Max(1, _sampleRate * 14);
            if (_acrBuffer.Count > maxAcrSamples)
            {
                _acrBuffer.RemoveRange(0, _acrBuffer.Count - maxAcrSamples);
            }
            while (_queue.Count >= _fftSize)
            {
                var frame = _queue.GetRange(0, _fftSize).ToArray();
                _queue.RemoveRange(0, _hopSize);
                var (magsDb, _, _) = _analyzer.Analyze(frame);
                _latestMags = magsDb;
            }
        }
    }

    private void RenderLatest()
    {
        var mags = _latestMags;
        if (mags is null) return;
        UpdateCurveFromSpectrum(mags);
        PushHudSpectrum();

        if (_lastPeakDb > -80.0)
        {
            _lastAudioActivity = DateTime.UtcNow;
        }
        else if ((DateTime.UtcNow - _lastAudioActivity).TotalSeconds > 4.0)
        {
            if (!string.IsNullOrEmpty(_nowPlaying))
            {
                _nowPlaying = "";
                _currentTrack = null;
                _lastPopupTrack = null;
                _hud.SetDetectedSong(null, false);
                lock (_lock) _acrBuffer.Clear();
            }
        }

        if (_overlayVisible) _curvePanel.Invalidate();
    }

    private void PushHudSpectrum()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastHudSpectrumPushUtc).TotalMilliseconds < 66) return;
        _lastHudSpectrumPushUtc = now;

        const double minDb = -100.0;
        const double maxDb = -20.0;
        double span = maxDb - minDb;
        int bars = _hudSpectrum.Length;
        for (int i = 0; i < bars; i++)
        {
            int start = i * CurvePoints / bars;
            int end = ((i + 1) * CurvePoints / bars) - 1;
            if (end < start) end = start;

            double band = -120.0;
            for (int k = start; k <= end && k < _curveDbSmoothed.Length; k++)
            {
                if (_curveDbSmoothed[k] > band) band = _curveDbSmoothed[k];
            }

            double normalized = Math.Clamp((band - minDb) / span, 0.0, 1.0);
            _hudSpectrum[i] = (float)normalized;
        }

        _hud.SetDetectionSpectrum(_hudSpectrum);
    }

    private void UpdateCurveFromSpectrum(double[] magsDb)
    {
        int bins = _fftSize / 2 + 1;
        double binHz = _sampleRate / (double)_fftSize;

        for (int i = 0; i < CurvePoints; i++)
        {
            double t = i / (double)(CurvePoints - 1);
            double hz = Math.Pow(10, Math.Log10(_minHz) + t * (Math.Log10(_maxHz) - Math.Log10(_minHz)));
            int center = Math.Clamp((int)Math.Round(hz / binHz), 1, bins - 1);
            int radius = Math.Max(1, (int)(center * 0.03));
            int start = Math.Max(1, center - radius);
            int end = Math.Min(bins - 1, center + radius);

            double band = -110;
            for (int k = start; k <= end; k++) band = Math.Max(band, magsDb[k]);
            _curveDb[i] = band;
        }

        for (int i = 0; i < CurvePoints; i++)
        {
            double prev = _curveDbSmoothed[i];
            double now = _curveDb[i];
            _curveDbSmoothed[i] = (now > prev)
                ? (0.72 * now + 0.28 * prev)
                : Math.Max(now, prev - 1.2);
        }

        _lastPeakDb = -140.0;
        for (int i = 0; i < CurvePoints; i++)
        {
            if (_curveDbSmoothed[i] > _lastPeakDb) _lastPeakDb = _curveDbSmoothed[i];
        }
    }

    private void DrawCurve(Graphics g, Size size)
    {
        g.Clear(TransparentKeyColor);
        if (size.Width <= 4 || size.Height <= 4) return;

        int minDb = -110;
        int maxDb = -20;
        double span = Math.Max(1e-9, maxDb - minDb);
        int w = size.Width;
        int h = size.Height;
        int titleH = string.IsNullOrWhiteSpace(_nowPlaying) ? 0 : 12;
        int bottomLabel = 16;
        int plotH = Math.Max(20, h - bottomLabel - titleH);

        if (!string.IsNullOrWhiteSpace(_nowPlaying))
        {
            using var f = new Font("Segoe UI", 7.2f, FontStyle.Bold, GraphicsUnit.Point);
            string txt = _nowPlaying;
            var sz = g.MeasureString(txt, f);
            float tx = Math.Max(0, (w - sz.Width) / 2);
            g.DrawString(txt, f, Brushes.Black, tx + 1, 1);
            g.DrawString(txt, f, Brushes.White, tx, 0);

            float btnX = tx + sz.Width + 8;
            _downloadRect = new Rectangle((int)btnX, 0, 24, 14);
            using (var brush = new SolidBrush(Color.FromArgb(40, 40, 40)))
            {
                g.FillRectangle(brush, _downloadRect);
            }
            g.DrawRectangle(Pens.Gray, _downloadRect);
            // Draw arrow
            using (var arrowPen = new Pen(Color.White, 1.5f))
            {
                int cx = _downloadRect.X + _downloadRect.Width / 2;
                int cy = _downloadRect.Y + _downloadRect.Height / 2;
                g.DrawLine(arrowPen, cx, cy - 3, cx, cy + 2);
                g.DrawLine(arrowPen, cx - 3, cy, cx, cy + 2);
                g.DrawLine(arrowPen, cx + 3, cy, cx, cy + 2);
            }

            if (!string.IsNullOrEmpty(_downloadStatus))
            {
                g.DrawString(_downloadStatus, f, Brushes.LightGreen, _downloadRect.Right + 6, 0);
            }
        }

        for (int i = 0; i < CurvePoints; i++)
        {
            double norm = Math.Clamp((_curveDbSmoothed[i] - minDb) / span, 0, 1);
            int x = (int)Math.Round(i * (w - 1.0) / (CurvePoints - 1.0));
            int top = titleH + (int)Math.Round((1.0 - norm) * (plotH - 1));
            DrawDottedBar(g, x, top, plotH + titleH);
        }

        DrawFrequencyScale(g, w, plotH + titleH, h);
    }

    private static void DrawDottedBar(Graphics g, int x, int top, int bottom)
    {
        const int dotW = 2;
        const int dotH = 2;
        const int step = 4;
        for (int y = bottom - dotH; y >= top; y -= step)
        {
            double t = 1.0 - (y / (double)Math.Max(1, bottom));
            Color c = DottedColor(t);
            using var b = new SolidBrush(c);
            g.FillRectangle(b, x - (dotW / 2), y, dotW, dotH);
        }
    }

    private static Color DottedColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        // Bottom to top: green -> yellow -> red
        if (t < 0.5)
        {
            double q = t / 0.5;
            int r = (int)(30 + (225 * q));
            int g = 255;
            return Color.FromArgb(220, r, g, 40);
        }
        double p = (t - 0.5) / 0.5;
        int gg = (int)(255 - (255 * p));
        return Color.FromArgb(225, 255, gg, 30);
    }

    private void DrawFrequencyScale(Graphics g, int width, int plotHeight, int totalHeight)
    {
        g.DrawLine(Pens.DimGray, 0, plotHeight, width, plotHeight);
        double[] refs = [31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
        using var f = new Font("Segoe UI", 7.0f, FontStyle.Regular, GraphicsUnit.Point);
        using var b = new SolidBrush(Color.LightSteelBlue);
        using var p = new Pen(Color.FromArgb(70, 130, 180));

        foreach (double hz in refs)
        {
            int x = (int)Math.Round(FrequencyToX(hz, width - 1));
            x = Math.Clamp(x, 0, Math.Max(0, width - 1));
            g.DrawLine(p, x, plotHeight - 5, x, plotHeight + 2);
            string txt = hz >= 1000 ? $"{hz / 1000:0.#}k" : $"{hz:0}";
            var sz = g.MeasureString(txt, f);
            float tx = Math.Clamp(x - (sz.Width / 2), 0, Math.Max(0, width - sz.Width));
            float ty = totalHeight - sz.Height;
            g.DrawString(txt, f, Brushes.Black, tx + 1, ty + 1);
            g.DrawString(txt, f, b, tx, ty);
        }
    }

    private double FrequencyToX(double hz, int width)
    {
        double lo = Math.Max(1.0, _minHz);
        double hi = Math.Max(lo * 1.2, _maxHz);
        double t = (Math.Log10(hz) - Math.Log10(lo)) / (Math.Log10(hi) - Math.Log10(lo));
        return Math.Clamp(t, 0, 1) * width;
    }

    private void UpdateOverlayVisibility()
    {
        if (_overlayVisible)
        {
            _overlayVisible = false;
            Hide();
        }
    }

    private async Task TryRecognizeAsync()
    {
        if (!_detectionEnabled) return;

        if (_downloadsBlockedBySetup)
        {
            _hud.SetSetupRequiredState(true);
            return;
        }

        if (_recognitionBusy) return;
        if (_acrClient is null)
        {
            _acrClient = AcrCloudClient.FromEnvironment();
            if (_acrClient is null)
            {
                if (!_acrInitFailedLogged)
                {
                    Log("ACRCloud variables not configured yet.");
                    _acrInitFailedLogged = true;
                }
                return;
            }
            _acrInitFailedLogged = false;
            Log("ACRCloud client initialized from saved settings.");
        }
        if (_lastPeakDb < -78.0) return;

        float[] clip;
        int sr = _sampleRate;
        lock (_lock)
        {
            int desired = Math.Max(1, sr * 10);
            if (_acrBuffer.Count < desired) return;
            int start = _acrBuffer.Count - desired;
            clip = _acrBuffer.GetRange(start, desired).ToArray();
        }

        _recognitionBusy = true;
        try
        {
            var found = await _acrClient.TryIdentifyAsync(clip, sr);
            if (found is not null)
            {
                _currentTrack = found;
                Log($"Recognized: {found}");
                _nowPlaying = found.ToString();
                _hud.SetSetupRequiredState(false);
                _hud.SetDetectedSong(found, true);
                if (HasPopupTrackChanged(found))
                {
                    _detectedPopup.ShowSong(found, selected => StartDownload(fromControlPanel: false, trackOverride: selected));
                    _lastPopupTrack = found;
                }
                if (_overlayVisible) _curvePanel.Invalidate();
            }
        }
        catch (Exception ex)
        {
            // Ignore recognition failures.
            Log($"Recognition failed: {ex.Message}");
        }
        finally
        {
            _recognitionBusy = false;
        }
    }

    private void SetDetectionEnabled(bool enabled)
    {
        _detectionEnabled = enabled;
        if (enabled) _recognitionTimer.Start();
        else _recognitionTimer.Stop();
        _hud.AddStatusMessage(enabled ? "Song detection is ON." : "Song detection is OFF.", false);
    }

    private bool HasPopupTrackChanged(TrackMetadata found)
    {
        if (_lastPopupTrack is null) return true;
        return !string.Equals(_lastPopupTrack.Title, found.Title, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_lastPopupTrack.Artist, found.Artist, StringComparison.OrdinalIgnoreCase);
    }

    private void OnPanelClick(object? sender, MouseEventArgs e)
    {
        if (!_downloadRect.IsEmpty && _downloadRect.Contains(e.Location))
        {
            StartDownload(fromControlPanel: false, trackOverride: null);
        }
    }

    private void StartDownload(bool fromControlPanel, TrackMetadata? trackOverride = null)
    {
        if (_downloadsBlockedBySetup)
        {
            _hud.AddStatusMessage("Setup required. Open Controls and add Telegram + ACR credentials.", true);
            EnsureStartupSetup();
            return;
        }

        TrackMetadata? selectedTrack = trackOverride ?? _currentTrack;
        if (selectedTrack is null) return;

        if (!TryChooseDownloadFolders(
                fromControlPanel,
                out string audioFolder,
                out string videoFolder,
                out bool downloadAudio,
                out bool downloadVideo))
        {
            _hud.AddStatusMessage("Download canceled before queueing.", true);
            return;
        }

        int id = Interlocked.Increment(ref _nextDownloadId);
        var request = new DownloadRequest(id, selectedTrack, audioFolder, videoFolder, downloadAudio, downloadVideo);
        int queueDepthAfterEnqueue;
        lock (_downloadQueueLock)
        {
            _downloadQueue.Enqueue(request);
            queueDepthAfterEnqueue = _downloadQueue.Count;
        }

        _hud.QueueDownload(request.Id, request.Track.ToString(), queueDepthAfterEnqueue, request.DownloadAudio, request.DownloadVideo);
        _hud.AddDownloadLog(
            request.Id,
            $"Queued with audio={(request.DownloadAudio ? audioFolder : "(skipped)")}, video={(request.DownloadVideo ? videoFolder : "(skipped)")}",
            false);
        _downloadStatus = _downloadInProgress ? $"Queued #{request.Id}" : $"Starting #{request.Id}";
        _curvePanel.Invalidate();

        bool shouldStartWorker = false;
        lock (_downloadQueueLock)
        {
            if (!_downloadInProgress)
            {
                _downloadInProgress = true;
                shouldStartWorker = true;
            }
        }

        if (shouldStartWorker) _ = Task.Run(ProcessDownloadQueueAsync);
    }

    private void EnsureStartupSetup()
    {
        if (!_downloadsBlockedBySetup) return;

        _hud.SetSetupRequiredState(true);
        var current = EnvSettingsStore.Load();
        using var setup = new SetupRequiredForm(current);
        var result = setup.ShowDialog(_hud);
        if (result != DialogResult.OK)
        {
            _hud.SetDownloadsEnabled(false);
            _hud.SetSetupRequiredState(true);
            _hud.AddStatusMessage("Setup required before downloads can run.", true);
            return;
        }

        var values = setup.GetValues();
        EnvSettingsStore.Save(values);
        EnvSettingsStore.ApplyToProcess(values);
        _acrClient?.Dispose();
        _acrClient = AcrCloudClient.FromEnvironment();
        _acrInitFailedLogged = false;
        _downloadsBlockedBySetup = !EnvSettingsStore.HasRequiredCredentialsInFile();
        _hud.SetDownloadsEnabled(!_downloadsBlockedBySetup);
        _hud.SetSetupRequiredState(_downloadsBlockedBySetup);
        _hud.AddStatusMessage(_downloadsBlockedBySetup
            ? "Setup is still incomplete."
            : "Setup completed. Downloads enabled.",
            _downloadsBlockedBySetup);
    }

    private async Task ProcessDownloadQueueAsync()
    {
        while (true)
        {
            DownloadRequest? request = null;
            int queuedRemaining = 0;
            lock (_downloadQueueLock)
            {
                if (_downloadQueue.Count > 0)
                {
                    request = _downloadQueue.Dequeue();
                    queuedRemaining = _downloadQueue.Count;
                }
                else
                {
                    _downloadInProgress = false;
                    _activeDownloadId = -1;
                    UpdateStatus("");
                    return;
                }
            }

            _activeDownloadId = request.Id;
            _hud.StartDownload(
                request.Id,
                request.Track.ToString(),
                request.AudioFolder,
                request.VideoFolder,
                queuedRemaining,
                request.DownloadAudio,
                request.DownloadVideo);
            SetSourceProgress("TG", request.DownloadAudio ? 0 : 100, request.DownloadAudio ? "Pending" : "Skipped");
            SetSourceProgress("YT", request.DownloadVideo ? 0 : 100, request.DownloadVideo ? "Pending" : "Skipped");
            UpdateStatus($"DL #{request.Id} Running");

            DownloadStepResult tgResult = request.DownloadAudio
                ? await DownloadTelegram(request.Track, request.AudioFolder)
                : DownloadStepResult.Skipped();
            bool tgSuccess = tgResult.Success;
            if (!tgSuccess) NotifyDownloadFailure("Telegram download failed.");
            else if (!request.DownloadAudio) _hud.AddDownloadLog(request.Id, "Audio download skipped by selection.", false);

            DownloadStepResult ytResult = request.DownloadVideo
                ? DownloadYoutube(request.Track, request.VideoFolder)
                : DownloadStepResult.Skipped();
            bool ytSuccess = ytResult.Success;
            if (!ytSuccess) NotifyDownloadFailure("YouTube download failed.");
            else if (!request.DownloadVideo) _hud.AddDownloadLog(request.Id, "Video download skipped by selection.", false);

            bool success = tgSuccess && ytSuccess;
            if (success)
            {
                UpdateStatus($"DL #{request.Id} Success");
                _hud.AddDownloadLog(request.Id, "All download sources succeeded.", false);
            }
            else
            {
                UpdateStatus($"DL #{request.Id} Failed");
                _hud.AddDownloadLog(request.Id, "One or more download sources failed.", true);
                _hud.ShowFailureWarning(request.Id, tgSuccess, ytSuccess);
            }

            _hud.CompleteDownload(
                request.Id,
                tgSuccess,
                ytSuccess,
                request.AudioFolder,
                request.VideoFolder,
                request.DownloadAudio,
                request.DownloadVideo,
                tgResult.FilePath,
                ytResult.FilePath);
        }
    }

    private bool TryChooseDownloadFolders(
        bool fromControlPanel,
        out string audioFolder,
        out string videoFolder,
        out bool downloadAudio,
        out bool downloadVideo)
    {
        audioFolder = "";
        videoFolder = "";
        downloadAudio = true;
        downloadVideo = true;
        string defaultAudioFolder = GetDefaultAudioFolder();
        string defaultVideoFolder = GetDefaultVideoFolder();
        using var dialog = new DownloadDestinationDialog(defaultAudioFolder, defaultVideoFolder, centerOnOwner: fromControlPanel);
        DialogResult result = fromControlPanel ? dialog.ShowDialog(_hud) : dialog.ShowDialog();
        if (result != DialogResult.OK) return false;

        downloadAudio = dialog.DownloadAudio;
        downloadVideo = dialog.DownloadVideo;
        if (!downloadAudio && !downloadVideo) return false;

        if (downloadAudio && string.IsNullOrWhiteSpace(dialog.AudioPath)) return false;
        if (downloadVideo && string.IsNullOrWhiteSpace(dialog.VideoPath)) return false;
        audioFolder = dialog.AudioPath;
        videoFolder = dialog.VideoPath;
        return true;
    }

    private static string GetDefaultAudioFolder()
    {
        return @"D:\Media\Library\Afrobeats";
    }

    private static string GetDefaultVideoFolder()
    {
        return @"D:\Media\Videos\Afrobeats";
    }

    private async Task<DownloadStepResult> DownloadTelegram(TrackMetadata track, string audioFolder)
    {
        try
        {
            SetSourceProgress("TG", 8, "Init");
            if (_telegram is null)
            {
                _telegram = CreateTelegramClient();
                var user = await _telegram.LoginUserIfNeeded();
                AppendActivity($"[TG] Logged in as {user}");
            }
            WTelegram.Client client = _telegram!;

            SetSourceProgress("TG", 18, "Resolve bot");
            const string botName = "deezload2bot";
            var resolved = await client.Contacts_ResolveUsername(botName);
            if (resolved.User is not User botUser) throw new Exception("Bot not found");

            // We'll use the bot in inline mode in our own Saved Messages chat (InputPeer.Self)
            // so we don't depend on how the bot behaves in its private dialog.
            InputPeer peer = InputPeer.Self;

            // Remember last message ID in Saved Messages so we can detect the new inline result.
            int lastId = 0;
            try
            {
                var before = await client.Messages_GetHistory(peer, limit: 1);
                if (before.Messages.Length > 0)
                    lastId = before.Messages[0].ID;
            }
            catch
            {
                // Ignore history probe failures; we'll just match on newest messages.
            }

            SetSourceProgress("TG", 32, "Send query");
            string query = $"{track.Artist} - {track.Title}";
            AppendActivity($"[TG] Query inline @{botName}: {query}");

            var botResults = await client.Messages_GetInlineBotResults(botUser, peer, query, string.Empty);
            if (botResults.results is null || botResults.results.Length == 0)
            {
                AppendActivity("[TG] Inline bot returned no results", true);
                SetSourceProgress("TG", 100, "No results");
                return DownloadStepResult.Failed();
            }

            var first = botResults.results[0];
            string resultId = first.ID;

            await client.Messages_SendInlineBotResult(
                peer,
                WTelegram.Helpers.RandomLong(),
                botResults.query_id,
                resultId,
                reply_to: null,
                schedule_date: null,
                send_as: null,
                quick_reply_shortcut: null,
                allow_paid_stars: null,
                silent: false,
                background: false,
                clear_draft: false,
                hide_via: false);

            Directory.CreateDirectory(audioFolder);
            string safeTitle = $"{track.Artist} - {track.Title}";
            foreach (char c in Path.GetInvalidFileNameChars()) safeTitle = safeTitle.Replace(c, '_');

            SetSourceProgress("TG", 45, "Waiting media");
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                int elapsed = (int)(45 - (deadline - DateTime.UtcNow).TotalSeconds);
                int pct = Math.Clamp(45 + (elapsed * 45 / 45), 45, 90);
                SetSourceProgress("TG", pct, "Waiting media");

                var history = await client.Messages_GetHistory(peer, limit: 10);
                foreach (var msgBase in history.Messages)
                {
                    if (msgBase is not TL.Message msg) continue;
                    if (msg.ID <= lastId) continue;

                    if (msg.media is MessageMediaDocument doc && doc.document is Document d)
                    {
                        string mime = d.mime_type ?? "";
                        bool isAudio = mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                                       mime.Contains("flac", StringComparison.OrdinalIgnoreCase);
                        if (!isAudio) continue;

                        AppendActivity("[TG] Downloading audio file...");
                        string ext = mime.Contains("flac", StringComparison.OrdinalIgnoreCase) ? "flac" : "mp3";
                        string filename = Path.Combine(audioFolder, $"{safeTitle}.{ext}");

                        SetSourceProgress("TG", 92, "Downloading");
                        using var fs = File.Create(filename);
                        await client.DownloadFileAsync(d, fs);
                        SetSourceProgress("TG", 100, "Done");
                        AppendActivity($"[TG] Saved: {filename}");
                        return DownloadStepResult.Done(filename);
                    }
                }

                await Task.Delay(1500);
            }

            AppendActivity("[TG] Timed out waiting for audio reply.", true);
            SetSourceProgress("TG", 100, "Timeout");
            return DownloadStepResult.Failed();
        }
        catch (Exception ex)
        {
            AppendActivity($"[TG] Error: {ex.Message}", true);
            SetSourceProgress("TG", 100, "Failed");
            return DownloadStepResult.Failed();
        }
    }

    private WTelegram.Client CreateTelegramClient()
    {
        try
        {
            string sessionPath = GetTelegramSessionPath();
            string? dir = Path.GetDirectoryName(sessionPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var stream = new FileStream(sessionPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new WTelegram.Client(Config, stream);
        }
        catch (Exception ex)
        {
            ReportSilentToolError($"Telegram explicit session stream failed, fallback to config path: {ex.Message}");
            return new WTelegram.Client(Config);
        }
    }

    private static string GetTelegramSessionPath()
    {
        string dir = AppPaths.LocalDataDir;

        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "WTelegram.session");
    }


    private string? Config(string what)
    {
        switch (what)
        {
            case "api_id": return Environment.GetEnvironmentVariable("TG_API_ID");
            case "api_hash": return Environment.GetEnvironmentVariable("TG_API_HASH");
            case "phone_number": return Environment.GetEnvironmentVariable("TG_PHONE");
            case "session_pathname":
                return GetTelegramSessionPath();
            case "verification_code":
                return PromptTelegramInput(
                    "Telegram Verification",
                    "Enter the code sent to your Telegram account.",
                    secret: false);
            case "password":
                return PromptTelegramInput(
                    "Telegram 2FA Password",
                    "Enter your Telegram two-step verification password.",
                    secret: true);
            default: return null;
        }
    }

    private string? PromptTelegramInput(string title, string prompt, bool secret)
    {
        try
        {
            Func<string?> show = () =>
            {
                using var dialog = new SimpleInputDialog(title, prompt, secret);
                var owner = _hud.IsHandleCreated ? _hud : null;
                return owner is null
                    ? (dialog.ShowDialog() == DialogResult.OK ? dialog.Value : null)
                    : (dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Value : null);
            };

            if (_hud.IsHandleCreated)
            {
                if (_hud.InvokeRequired) return (string?)_hud.Invoke(show);
                return show();
            }

            if (InvokeRequired) return (string?)Invoke(show);
            return show();
        }
        catch
        {
            return null;
        }
    }

    private DownloadStepResult DownloadYoutube(TrackMetadata track, string videoFolder)
    {
        try
        {
            if (!TryGetToolPath("yt-dlp.exe", out string ytDlpPath))
            {
                SetSourceProgress("YT", 100, "Failed");
                AppendActivity("[YT] Missing tool: yt-dlp.exe (place it in tools folder).", true);
                return DownloadStepResult.Failed();
            }
            if (!TryGetToolPath("ffmpeg.exe", out string ffmpegPath))
            {
                SetSourceProgress("YT", 100, "Failed");
                AppendActivity("[YT] Missing tool: ffmpeg.exe (place it in tools folder).", true);
                return DownloadStepResult.Failed();
            }

            SetSourceProgress("YT", 5, "Init");
            Directory.CreateDirectory(videoFolder);
            string safeTitle = SanitizeFileName(track.ToString());
            string outputPattern = $"{safeTitle}.%(ext)s";
            AppendActivity($"[YT] Searching and downloading: {track}");
            bool vegasReady = UiPreferencesStore.GetVegasReadyVideo();
            if (vegasReady)
            {
                AppendActivity("[YT] Vegas mode enabled: preferring AVC/H.264 video with AAC audio.");
            }

            var before = new HashSet<string>(Directory.GetFiles(videoFolder, "*", SearchOption.TopDirectoryOnly), StringComparer.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                Arguments = BuildYtDlpArguments(track, videoFolder, outputPattern, vegasReady, ffmpegPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => HandleYtOutput(e.Data);
            p.ErrorDataReceived += (_, e) => HandleYtOutput(e.Data);

            if (!p.Start())
            {
                ReportSilentToolError("yt-dlp.exe failed to start.");
                AppendActivity("[YT] Failed to start yt-dlp process.", true);
                throw new InvalidOperationException("Failed to start yt-dlp process.");
            }

            SetSourceProgress("YT", 12, "Running");
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                ReportSilentToolError($"yt-dlp.exe exited with code {p.ExitCode}.");
                throw new Exception($"yt-dlp exited with code {p.ExitCode}");
            }
            SetSourceProgress("YT", 100, "Done");
            string? file = ResolveDownloadedVideoFile(videoFolder, safeTitle, before);
            AppendActivity(file is null ? "[YT] Video saved." : $"[YT] Video saved: {file}");
            return DownloadStepResult.Done(file);
        }
        catch (Exception ex)
        {
            ReportSilentToolError($"YouTube download execution error: {ex.Message}");
            SetSourceProgress("YT", 100, "Failed");
            AppendActivity($"[YT] Download failed: {ex.Message}", true);
            return DownloadStepResult.Failed();
        }
    }

    private string ResolveToolsDirectory()
    {
        string cwdTools = Path.Combine(Environment.CurrentDirectory, "tools");
        string appTools = Path.Combine(AppContext.BaseDirectory, "tools");
        string chosen = Directory.Exists(cwdTools) ? cwdTools : appTools;
        Directory.CreateDirectory(chosen);
        return chosen;
    }

    private bool TryGetToolPath(string toolFileName, out string fullPath)
    {
        fullPath = Path.Combine(_toolsDirectory, toolFileName);
        if (File.Exists(fullPath)) return true;

        ReportSilentToolError($"Missing required tool: {toolFileName}. Expected at: {fullPath}");
        return false;
    }

    private static void ReportSilentToolError(string message)
    {
        try
        {
            Console.Error.WriteLine($"[tools] {message}");
        }
        catch
        {
            // Keep silent in UI; command-line report is best-effort.
        }
    }

    private static string SanitizeFileName(string name)
    {
        string s = name;
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private static string? ResolveDownloadedVideoFile(string videoFolder, string safeTitle, HashSet<string> before)
    {
        var after = Directory.GetFiles(videoFolder, "*", SearchOption.TopDirectoryOnly);
        string? discovered = null;
        DateTime latest = DateTime.MinValue;
        foreach (string file in after)
        {
            if (before.Contains(file)) continue;
            string stem = Path.GetFileNameWithoutExtension(file);
            if (!stem.StartsWith(safeTitle, StringComparison.OrdinalIgnoreCase)) continue;
            DateTime t = File.GetLastWriteTimeUtc(file);
            if (t > latest)
            {
                latest = t;
                discovered = file;
            }
        }

        if (!string.IsNullOrWhiteSpace(discovered)) return discovered;

        string[] fallback = Directory.GetFiles(videoFolder, $"{safeTitle}.*", SearchOption.TopDirectoryOnly);
        if (fallback.Length == 0) return null;
        Array.Sort(fallback, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
        return fallback[0];
    }

    private static string BuildYtDlpArguments(TrackMetadata track, string videoFolder, string outputPattern, bool vegasReady, string ffmpegPath)
    {
        string format = vegasReady
            ? "bv*[vcodec^=avc1][height<=1080]+ba[acodec^=mp4a]/b[vcodec^=avc1][acodec^=mp4a][height<=1080]/b[ext=mp4][height<=1080]"
            : "bv*[height=1080]+bestaudio/bv*+bestaudio/b";

        string sort = vegasReady
            ? "-S \"res:1080,vcodec:avc1,acodec:mp4a\""
            : "-S \"res:1080,abr\"";

        return $"\"ytsearch1:{track}\" -f \"{format}\" --ffmpeg-location \"{ffmpegPath}\" --merge-output-format mp4 {sort} -o \"{videoFolder}\\{outputPattern}\" --no-playlist --newline";
    }

    private void HandleYtOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var m = PercentRegex.Match(line);
        if (m.Success && double.TryParse(m.Groups["pct"].Value, out double pct))
        {
            int normalized = Math.Clamp((int)Math.Round(pct), 0, 100);
            SetSourceProgress("YT", normalized, "Downloading");
            return;
        }

        if (line.Contains("Destination:", StringComparison.OrdinalIgnoreCase))
        {
            AppendActivity($"[YT] {line.Trim()}");
            SetSourceProgress("YT", Math.Max(_hud.GetProgress("YT"), 20), "Saving");
            return;
        }

        if (line.Contains("Merging", StringComparison.OrdinalIgnoreCase))
        {
            AppendActivity($"[YT] {line.Trim()}");
            SetSourceProgress("YT", Math.Max(_hud.GetProgress("YT"), 88), "Merging");
        }
    }

    private void NotifyDownloadFailure(string message)
    {
        AppendActivity(message, true);
    }

    private void UpdateStatus(string status)
    {
        Invoke(() =>
        {
            if (IsDisposed) return;
            _downloadStatus = status;
            _curvePanel.Invalidate();

            if (status.StartsWith("Saved", StringComparison.OrdinalIgnoreCase) || status.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                Task.Delay(3000).ContinueWith(_ => Invoke(() =>
                {
                    if (!IsDisposed)
                    {
                        _downloadStatus = "";
                        _curvePanel.Invalidate();
                    }
                }));
            }
        });
    }

    private void SetSourceProgress(string source, int percent, string text)
    {
        _hud.SetProgress(source, percent, text);
    }

    private void AppendActivity(string message, bool isError = false)
    {
        if (_activeDownloadId > 0) _hud.AddDownloadLog(_activeDownloadId, message, isError);
        else _hud.AddStatusMessage(message, isError);
        string prefix = isError ? "[ERR]" : "[OK ]";
        Log($"{prefix} {message}");
    }

    private bool IsForegroundFullscreenWindow()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == Handle) return false;
        if (!IsWindowVisible(hwnd)) return false;
        if (!GetWindowRect(hwnd, out RECT r)) return false;
        if (IsIconic(hwnd)) return false;

        Rectangle rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        if (rect.Width < 200 || rect.Height < 200) return false;

        var screen = Screen.FromRectangle(rect);
        Rectangle bounds = screen.Bounds;
        int dx = Math.Abs(rect.Left - bounds.Left) + Math.Abs(rect.Right - bounds.Right);
        int dy = Math.Abs(rect.Top - bounds.Top) + Math.Abs(rect.Bottom - bounds.Bottom);
        bool exactFullscreen = dx <= 2 && dy <= 2;
        if (!exactFullscreen) return false;

        // Reject common maximized app windows with visible title/caption bar.
        nint stylePtr = GetWindowLongPtr(hwnd, GWL_STYLE);
        long style = stylePtr.ToInt64();
        bool hasCaption = (style & WS_CAPTION) != 0;
        return !hasCaption;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    private static void Log(string msg)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
        try
        {
            string dir = AppPaths.LocalDataDir;
            System.IO.Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, "widget-debug.log");
            System.IO.File.AppendAllText(file, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

internal sealed record DownloadRequest(
    int Id,
    TrackMetadata Track,
    string AudioFolder,
    string VideoFolder,
    bool DownloadAudio,
    bool DownloadVideo);
internal sealed record DownloadStepResult(bool Success, string? FilePath)
{
    public static DownloadStepResult Done(string? path) => new(true, path);
    public static DownloadStepResult Skipped() => new(true, null);
    public static DownloadStepResult Failed() => new(false, null);
}

internal sealed class DownloadHudForm : Form
{
    public event EventHandler? DownloadRequested;
    public event Action<TrackMetadata>? ManualDownloadRequested;
    public event Action<bool>? DetectionToggled;

    private readonly SidebarNavButton _songDetectionNav = new("Song Detection");
    private readonly SidebarNavButton _downloadsNav = new("Downloads");
    private readonly SidebarNavButton _controlsNav = new("Controls");
    private readonly Panel _contentHost = new() { Dock = DockStyle.Fill, Padding = new Padding(16) };
    private readonly Panel _songDetectionPage = new() { Dock = DockStyle.Fill };
    private readonly Panel _downloadsPage = new() { Dock = DockStyle.Fill };
    private readonly Panel _controlsPage = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly DownloadProgressCard _audioCard = new("Audio", "AU", DarkTheme.ProgressBarTelegram);
    private readonly DownloadProgressCard _videoCard = new("Video", "VD", DarkTheme.ProgressBarYouTube);
    private readonly Label _songTitleLabel = new() { AutoSize = true, ForeColor = DarkTheme.Text, Font = new Font("Segoe UI Semibold", 12f, FontStyle.Regular, GraphicsUnit.Point) };
    private readonly Label _songHintLabel = new() { AutoSize = true, ForeColor = DarkTheme.TextMuted, Font = new Font("Segoe UI", 9.4f, FontStyle.Regular, GraphicsUnit.Point) };
    private readonly RoundedButton _songDownloadButton = new() { Text = "Download", AutoSize = true };
    private readonly CheckBox _detectionToggle = new() { AutoSize = false, Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter };
    private readonly TextBox _manualArtistInput = new() { Width = 220, BorderStyle = BorderStyle.FixedSingle, BackColor = DarkTheme.PanelAlt, ForeColor = DarkTheme.Text };
    private readonly TextBox _manualTitleInput = new() { Width = 220, BorderStyle = BorderStyle.FixedSingle, BackColor = DarkTheme.PanelAlt, ForeColor = DarkTheme.Text };
    private readonly RoundedButton _manualDownloadButton = new() { Text = "Download Manual Entry", AutoSize = true };
    private readonly SpectrumBarsControl _songSpectrum = new() { Dock = DockStyle.Fill, BackColor = DarkTheme.PanelAlt };
    private readonly Label _queueLabel = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly RoundedPanel _gridCard = new() { Dock = DockStyle.Fill, Padding = new Padding(12) };
    private readonly RoundedPanel _logsCard = new() { Dock = DockStyle.Fill, Padding = new Padding(12), Visible = false };
    private readonly RoundedPanel _reviewCard = new() { Dock = DockStyle.Fill, Padding = new Padding(14) };
    private readonly DataGridView _downloadGrid = new();
    private readonly ListBox _logList = new();
    private readonly RoundedButton _toggleLogsButton = new() { Text = "Show Logs", AutoSize = true };
    private readonly Label _reviewTrackTitle = new() { Dock = DockStyle.Top, Height = 42, ForeColor = DarkTheme.Text, Font = new Font("Segoe UI Semibold", 11f, FontStyle.Regular, GraphicsUnit.Point), AutoEllipsis = true };
    private readonly Label _reviewHint = new() { Dock = DockStyle.Top, Height = 24, ForeColor = DarkTheme.TextMuted, Text = "Select a download row to review audio/video output." };
    private readonly Label _reviewAudioStatus = new() { AutoSize = true, ForeColor = DarkTheme.TextMuted };
    private readonly TextBox _reviewAudioPath = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.FixedSingle, BackColor = DarkTheme.PanelAlt, ForeColor = DarkTheme.Text };
    private readonly RoundedButton _reviewAudioButton = new() { Text = "Open Audio", AutoSize = true };
    private readonly RoundedButton _deleteAudioButton = new() { Text = "Delete Audio", AutoSize = true };
    private readonly Label _reviewVideoStatus = new() { AutoSize = true, ForeColor = DarkTheme.TextMuted };
    private readonly TextBox _reviewVideoPath = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.FixedSingle, BackColor = DarkTheme.PanelAlt, ForeColor = DarkTheme.Text };
    private readonly RoundedButton _reviewVideoButton = new() { Text = "Open Video", AutoSize = true };
    private readonly RoundedButton _deleteVideoButton = new() { Text = "Delete Video", AutoSize = true };
    private readonly Dictionary<string, TextBox> _envInputs = [];
    private readonly Label _controlsSaveStatus = new() { AutoSize = true, ForeColor = DarkTheme.TextMuted };
    private readonly ToolStripStatusLabel _statusText = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _overallProgress = new() { Minimum = 0, Maximum = 100, Value = 0, Width = 140 };
    private readonly StatusStrip _statusStrip = new();
    private readonly Dictionary<int, DownloadItemState> _downloads = [];
    private int _currentDownloadId = -1;
    private int _reviewDownloadId = -1;
    private int _queuedPending;
    private bool _logsVisible;
    private bool _downloadsEnabled = true;
    private bool _canDownloadCurrentSong;
    private bool _setupRequiredState;
    private bool _suppressDetectionToggleEvent;

    public DownloadHudForm()
    {
        Text = "Download Status & Control Panel";
        ShowIcon = true;
        Icon = AppIconLoader.GetIconOrDefault();
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        Width = 980;
        Height = 620;
        MinimumSize = new Size(860, 520);
        Font = new Font("Segoe UI", 9.2f, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = DarkTheme.Background;
        ForeColor = DarkTheme.Text;

        BuildLayout();
        PositionTopRight();
        Show();
    }

    public void QueueDownload(int id, string track, int queueDepth, bool downloadAudio, bool downloadVideo)
    {
        RunOnUi(() =>
        {
            EnsureDownloadRow(id, track);
            SetRowStatuses(id, downloadAudio ? "Pending" : "Skipped", downloadVideo ? "Pending" : "Skipped");
            _queuedPending = Math.Max(0, queueDepth);
            UpdateQueueLabel();
            _statusText.Text = $"Queued #{id}";
            EnsureVisibleWindow();
        });
    }

    public void StartDownload(
        int id,
        string track,
        string audioFolder,
        string videoFolder,
        int queuedRemaining,
        bool downloadAudio,
        bool downloadVideo)
    {
        RunOnUi(() =>
        {
            _currentDownloadId = id;
            EnsureDownloadRow(id, track);
            SetRowStatuses(id, downloadAudio ? "Downloading" : "Skipped", downloadVideo ? "Pending" : "Skipped");
            _queuedPending = Math.Max(0, queuedRemaining);
            UpdateQueueLabel();

            _audioCard.SetProgress(downloadAudio ? 0 : 100, downloadAudio ? "Pending" : "Skipped");
            _videoCard.SetProgress(downloadVideo ? 0 : 100, downloadVideo ? "Pending" : "Skipped");
            _overallProgress.Value = 0;
            _statusText.Text = $"Downloading #{id}";

            AddDownloadLog(id, downloadAudio ? $"Target audio folder: {audioFolder}" : "Audio download skipped by selection.", false);
            AddDownloadLog(id, downloadVideo ? $"Target video folder: {videoFolder}" : "Video download skipped by selection.", false);
            SelectRowById(id);
            EnsureVisibleWindow();
        });
    }

    public void SetProgress(string source, int percent, string text)
    {
        RunOnUi(() =>
        {
            percent = Math.Clamp(percent, 0, 100);
            if (source == "TG")
            {
                _audioCard.SetProgress(percent, text);
                SetRowStatuses(_currentDownloadId, InferStatusFromProgress(percent, text), null);
            }
            else
            {
                _videoCard.SetProgress(percent, text);
                SetRowStatuses(_currentDownloadId, null, InferStatusFromProgress(percent, text));
            }

            int combined = (_audioCard.Progress + _videoCard.Progress) / 2;
            _overallProgress.Value = combined;
            string lane = source == "TG" ? "Audio" : "Video";
            _statusText.Text = $"Status: {lane} {percent}% ({text})";
        });
    }

    public int GetProgress(string source)
    {
        if (InvokeRequired) return (int)Invoke(new Func<int>(() => GetProgress(source)));
        return source == "TG" ? _audioCard.Progress : _videoCard.Progress;
    }

    public void AddActivity(string message, bool isError)
    {
        AddStatusMessage(message, isError);
    }

    public void AddStatusMessage(string message, bool isError)
    {
        RunOnUi(() => _statusText.Text = message);
    }

    public void ShowFailureWarning(int id, bool audioSucceeded, bool videoSucceeded)
    {
        RunOnUi(() =>
        {
            string failedParts = (!audioSucceeded, !videoSucceeded) switch
            {
                (true, true) => "audio and video",
                (true, false) => "audio",
                (false, true) => "video",
                _ => "download"
            };
            MessageBox.Show(
                this,
                $"Download #{id} failed for {failedParts}. Check logs in Downloads > Review.",
                "Download Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        });
    }

    public void SetDetectedSong(TrackMetadata? track, bool canDownload)
    {
        RunOnUi(() =>
        {
            if (_setupRequiredState)
            {
                _songTitleLabel.Text = "Setup required";
                _songHintLabel.Text = "Please complete setup in Controls before this feature works.";
                _songDownloadButton.Enabled = false;
                UpdateManualDownloadButtonState();
                return;
            }

            string title = track?.ToString() ?? "";
            bool hasSong = !string.IsNullOrWhiteSpace(title);
            _songTitleLabel.Text = hasSong ? title : "Waiting for song to be detected";
            _songHintLabel.Text = hasSong
                ? "Tap Download to add this track."
                : "Play audio and wait for recognition (approximately 25 seconds)...";
            _canDownloadCurrentSong = hasSong && canDownload;
            _songDownloadButton.Enabled = _downloadsEnabled && _canDownloadCurrentSong;
            UpdateManualDownloadButtonState();
        });
    }

    public void SetSetupRequiredState(bool required)
    {
        RunOnUi(() =>
        {
            _setupRequiredState = required;
            if (required)
            {
                _canDownloadCurrentSong = false;
                _songTitleLabel.Text = "Setup required";
                _songHintLabel.Text = "Please complete setup in Controls before this feature works.";
                _songDownloadButton.Enabled = false;
            }
            else
            {
                _songTitleLabel.Text = "Waiting for song to be detected";
                _songHintLabel.Text = "Play audio and wait for recognition (approximately 25 seconds)...";
                _songDownloadButton.Enabled = false;
            }
            UpdateManualDownloadButtonState();
        });
    }

    public void SetDownloadsEnabled(bool enabled)
    {
        RunOnUi(() =>
        {
            _downloadsEnabled = enabled;
            if (!enabled)
            {
                _songDownloadButton.Enabled = false;
            }
            else
            {
                _songDownloadButton.Enabled = _canDownloadCurrentSong;
            }
            UpdateManualDownloadButtonState();
        });
    }

    public void SetDetectionEnabled(bool enabled)
    {
        RunOnUi(() =>
        {
            _suppressDetectionToggleEvent = true;
            _detectionToggle.Checked = enabled;
            _detectionToggle.Text = enabled ? "Detection: ON" : "Detection: OFF";
            _suppressDetectionToggleEvent = false;
        });
    }

    public void SetDetectionSpectrum(float[] levels)
    {
        if (levels is null || levels.Length == 0) return;
        RunOnUi(() => _songSpectrum.SetLevels(levels));
    }

    public void AddDownloadLog(int id, string message, bool isError)
    {
        RunOnUi(() =>
        {
            EnsureDownloadRow(id, $"Track #{id}");
            var item = _downloads[id];
            string prefix = isError ? "[ERR]" : "[OK ]";
            item.Logs.Add($"{DateTime.Now:HH:mm:ss} {prefix} {message}");
            if (item.Logs.Count > 600) item.Logs.RemoveAt(0);
            if (id == _currentDownloadId) _statusText.Text = message;
            RefreshLogView();
        });
    }

    public void CompleteDownload(
        int id,
        bool tgSuccess,
        bool ytSuccess,
        string audioFolder,
        string videoFolder,
        bool downloadAudio,
        bool downloadVideo,
        string? audioFilePath,
        string? videoFilePath)
    {
        RunOnUi(() =>
        {
            bool success = tgSuccess && ytSuccess;
            SetRowStatuses(
                id,
                downloadAudio ? (tgSuccess ? "Success" : "Failed") : "Skipped",
                downloadVideo ? (ytSuccess ? "Success" : "Failed") : "Skipped");
            if (_downloads.TryGetValue(id, out var item))
            {
                item.AudioFilePath = audioFilePath;
                item.VideoFilePath = videoFilePath;
            }
            _overallProgress.Value = 100;
            _statusText.Text = success ? $"Download #{id} completed successfully" : $"Download #{id} completed with failures";
            AddDownloadLog(id, $"Summary: TG={(tgSuccess ? "Success" : "Failed")} | YT={(ytSuccess ? "Success" : "Failed")}", !success);
            if (downloadAudio) AddDownloadLog(id, $"Audio folder: {audioFolder}", false);
            if (downloadVideo) AddDownloadLog(id, $"Video folder: {videoFolder}", false);
            if (!string.IsNullOrWhiteSpace(audioFilePath)) AddDownloadLog(id, $"Audio file: {audioFilePath}", false);
            if (!string.IsNullOrWhiteSpace(videoFilePath)) AddDownloadLog(id, $"Video file: {videoFilePath}", false);
            if (_reviewDownloadId == id) RefreshReviewPanel(id);
        });
    }

    private void ReviewArtifact(int id, bool isAudio)
    {
        if (!_downloads.TryGetValue(id, out var item)) return;
        string? path = isAudio ? item!.AudioFilePath : item!.VideoFilePath;
        string kind = isAudio ? "audio" : "video";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AddDownloadLog(id, $"Cannot review {kind}: file not found.", true);
            _statusText.Text = $"No {kind} file available";
            return;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            AddDownloadLog(id, $"Opened {kind}: {path}", false);
        }
        catch (Exception ex)
        {
            AddDownloadLog(id, $"Failed to open {kind}: {ex.Message}", true);
        }
    }

    private void DeleteArtifact(int id, bool isAudio)
    {
        if (!_downloads.TryGetValue(id, out var item)) return;
        string kind = isAudio ? "audio" : "video";
        string? path = isAudio ? item!.AudioFilePath : item!.VideoFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AddDownloadLog(id, $"Cannot delete {kind}: file not found.", true);
            _statusText.Text = $"No {kind} file available";
            return;
        }

        var dr = MessageBox.Show(
            this,
            $"Delete this {kind} file?\n{path}",
            $"Delete {kind}",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (dr != DialogResult.Yes) return;

        try
        {
            File.Delete(path);
            if (isAudio)
            {
                item.AudioFilePath = null;
                SetRowStatuses(id, "Deleted", null);
            }
            else
            {
                item.VideoFilePath = null;
                SetRowStatuses(id, null, "Deleted");
            }
            AddDownloadLog(id, $"Deleted {kind}: {path}", false);
        }
        catch (Exception ex)
        {
            AddDownloadLog(id, $"Failed to delete {kind}: {ex.Message}", true);
        }
    }

    private void BuildLayout()
    {
        var shell = new Panel { Dock = DockStyle.Fill, BackColor = DarkTheme.Background };
        var sidebar = BuildSidebar();
        BuildSongDetectionPage();
        BuildDownloadsPage();
        BuildControlsPage();

        _statusStrip.BackColor = DarkTheme.PanelElevated;
        _statusStrip.ForeColor = DarkTheme.TextMuted;
        _statusStrip.SizingGrip = true;
        _statusStrip.Items.Add(_statusText);
        _statusStrip.Items.Add(_overallProgress);
        _statusText.Text = "Ready";

        _contentHost.Controls.Add(_songDetectionPage);
        _contentHost.Controls.Add(_downloadsPage);
        _contentHost.Controls.Add(_controlsPage);

        shell.Controls.Add(_contentHost);
        shell.Controls.Add(sidebar);
        Controls.Add(shell);
        Controls.Add(_statusStrip);

        SetActivePage("song");
    }

    private Panel BuildSidebar()
    {
        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = 190,
            BackColor = DarkTheme.Sidebar,
            Padding = new Padding(14, 18, 14, 14)
        };

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Text = "Download\r\nManager",
            ForeColor = DarkTheme.Text,
            Font = new Font("Segoe UI Semibold", 12f, FontStyle.Regular, GraphicsUnit.Point)
        };

        var sections = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Text = "Sections",
            ForeColor = DarkTheme.TextMuted,
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Point)
        };

        _songDetectionNav.Dock = DockStyle.Top;
        _songDetectionNav.Click += (_, _) => SetActivePage("song");

        _downloadsNav.Dock = DockStyle.Top;
        _downloadsNav.Click += (_, _) => SetActivePage("downloads");

        _controlsNav.Dock = DockStyle.Top;
        _controlsNav.Click += (_, _) => SetActivePage("controls");

        var navHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = 168,
            BackColor = Color.Transparent
        };
        _controlsNav.Top = 112;
        _downloadsNav.Top = 56;
        _songDetectionNav.Top = 0;
        _songDetectionNav.Left = 0;
        _downloadsNav.Left = 0;
        _controlsNav.Left = 0;
        _songDetectionNav.Width = navHost.Width;
        _downloadsNav.Width = navHost.Width;
        _controlsNav.Width = navHost.Width;
        _songDetectionNav.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _downloadsNav.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _controlsNav.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        navHost.Controls.Add(_songDetectionNav);
        navHost.Controls.Add(_downloadsNav);
        navHost.Controls.Add(_controlsNav);

        sidebar.Controls.Add(navHost);
        sidebar.Controls.Add(sections);
        sidebar.Controls.Add(title);
        return sidebar;
    }

    private void BuildSongDetectionPage()
    {
        _songDetectionPage.BackColor = DarkTheme.Background;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 182));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var progressCards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, 0, 0, 8)
        };
        progressCards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        progressCards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        progressCards.Controls.Add(_audioCard, 0, 0);
        progressCards.Controls.Add(_videoCard, 1, 0);

        _queueLabel.Text = "Queue: 0 pending";
        _queueLabel.ForeColor = DarkTheme.TextMuted;
        _queueLabel.Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        _queueLabel.Dock = DockStyle.Bottom;

        var cardsHost = new Panel { Dock = DockStyle.Fill };
        cardsHost.Controls.Add(progressCards);
        cardsHost.Controls.Add(_queueLabel);

        var infoCard = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DarkTheme.Panel,
            BorderColor = DarkTheme.Border,
            CornerRadius = 14,
            Padding = new Padding(18)
        };
        var infoTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Text = "Song Detection",
            ForeColor = DarkTheme.Text,
            Font = new Font("Segoe UI Semibold", 13f, FontStyle.Regular, GraphicsUnit.Point)
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _songTitleLabel.Text = "Waiting for song to be detected";
        _songHintLabel.Text = "Play audio and wait for recognition (approximately 25 seconds)...";
        _songDownloadButton.Enabled = false;
        _songDownloadButton.AutoSize = false;
        _songDownloadButton.Width = 160;
        _songDownloadButton.Height = 32;
        _songDownloadButton.Margin = new Padding(0, 4, 0, 0);
        _songDownloadButton.Click += (_, _) => DownloadRequested?.Invoke(this, EventArgs.Empty);
        _detectionToggle.Width = 150;
        _detectionToggle.Height = 30;
        _detectionToggle.FlatStyle = FlatStyle.Flat;
        _detectionToggle.FlatAppearance.BorderColor = DarkTheme.Border;
        _detectionToggle.FlatAppearance.BorderSize = 1;
        _detectionToggle.BackColor = DarkTheme.PanelElevated;
        _detectionToggle.ForeColor = DarkTheme.Text;
        _detectionToggle.Checked = true;
        _detectionToggle.Text = "Detection: ON";
        _detectionToggle.CheckedChanged += (_, _) =>
        {
            if (_suppressDetectionToggleEvent) return;
            bool enabled = _detectionToggle.Checked;
            _detectionToggle.Text = enabled ? "Detection: ON" : "Detection: OFF";
            DetectionToggled?.Invoke(enabled);
        };

        var leftActionHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _songDownloadButton.Location = new Point(0, 4);
        _detectionToggle.Location = new Point(0, 42);
        leftActionHost.Controls.Add(_songDownloadButton);
        leftActionHost.Controls.Add(_detectionToggle);

        var manualInlineCard = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DarkTheme.PanelAlt,
            BorderColor = DarkTheme.Border,
            CornerRadius = 10,
            Padding = new Padding(10, 8, 10, 8)
        };
        var manualInlineLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3
        };
        manualInlineLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
        manualInlineLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        manualInlineLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        manualInlineLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        manualInlineLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        manualInlineLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        var manualHeader = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Manual input",
            ForeColor = DarkTheme.TextMuted,
            Font = new Font("Segoe UI", 8.8f, FontStyle.Regular, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _manualArtistInput.Dock = DockStyle.Fill;
        _manualTitleInput.Dock = DockStyle.Fill;
        _manualArtistInput.PlaceholderText = "e.g. Burna Boy";
        _manualTitleInput.PlaceholderText = "e.g. Last Last";
        _manualArtistInput.Margin = new Padding(0, 1, 0, 1);
        _manualTitleInput.Margin = new Padding(0, 1, 0, 1);
        _manualArtistInput.TextChanged += (_, _) => UpdateManualDownloadButtonState();
        _manualTitleInput.TextChanged += (_, _) => UpdateManualDownloadButtonState();
        _manualDownloadButton.AutoSize = false;
        _manualDownloadButton.Width = 170;
        _manualDownloadButton.Height = 28;
        _manualDownloadButton.Enabled = false;
        _manualDownloadButton.Anchor = AnchorStyles.Left;
        _manualDownloadButton.Click += (_, _) => QueueManualDownload();

        manualInlineLayout.Controls.Add(manualHeader, 0, 0);
        manualInlineLayout.SetColumnSpan(manualHeader, 3);
        manualInlineLayout.Controls.Add(new Label { Text = "Artist", ForeColor = DarkTheme.Text, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
        manualInlineLayout.Controls.Add(_manualArtistInput, 1, 1);
        manualInlineLayout.Controls.Add(_manualTitleInput, 2, 1);
        manualInlineLayout.Controls.Add(_manualDownloadButton, 2, 2);
        manualInlineCard.Controls.Add(manualInlineLayout);

        var actionRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionRow.Controls.Add(leftActionHost, 0, 0);
        actionRow.Controls.Add(manualInlineCard, 1, 0);

        var vizCard = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DarkTheme.PanelAlt,
            BorderColor = DarkTheme.Border,
            CornerRadius = 10,
            Padding = new Padding(8)
        };
        var vizTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 20,
            Text = "Live Audio Activity",
            ForeColor = DarkTheme.TextMuted,
            Font = new Font("Segoe UI", 8.8f, FontStyle.Regular, GraphicsUnit.Point)
        };
        vizCard.Controls.Add(_songSpectrum);
        vizCard.Controls.Add(vizTitle);

        content.Controls.Add(_songTitleLabel, 0, 0);
        content.Controls.Add(_songHintLabel, 0, 1);
        content.Controls.Add(actionRow, 0, 2);
        content.Controls.Add(vizCard, 0, 3);

        infoCard.Controls.Add(content);
        infoCard.Controls.Add(infoTitle);

        root.Controls.Add(cardsHost, 0, 0);
        root.Controls.Add(infoCard, 0, 1);
        _songDetectionPage.Controls.Add(root);
        UpdateManualDownloadButtonState();
    }

    private void QueueManualDownload()
    {
        string artist = _manualArtistInput.Text.Trim();
        string title = _manualTitleInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            _statusText.Text = "Enter both artist and title to queue a manual download.";
            UpdateManualDownloadButtonState();
            return;
        }

        ManualDownloadRequested?.Invoke(new TrackMetadata(title, artist, null));
    }

    private void UpdateManualDownloadButtonState()
    {
        bool hasArtist = !string.IsNullOrWhiteSpace(_manualArtistInput.Text);
        bool hasTitle = !string.IsNullOrWhiteSpace(_manualTitleInput.Text);
        _manualDownloadButton.Enabled = _downloadsEnabled && !_setupRequiredState && hasArtist && hasTitle;
    }

    private void BuildDownloadsPage()
    {
        _downloadsPage.BackColor = DarkTheme.Background;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        BuildDownloadGrid();
        BuildLogsPanel();
        BuildReviewPanel();

        var middleHost = new Panel { Dock = DockStyle.Fill };
        middleHost.Controls.Add(_logsCard);
        middleHost.Controls.Add(_gridCard);
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            BackColor = DarkTheme.Background,
            FixedPanel = FixedPanel.None
        };
        split.Panel1.BackColor = DarkTheme.Background;
        split.Panel2.BackColor = DarkTheme.Background;
        bool splitterInitialized = false;
        split.Resize += (_, _) =>
        {
            int available = split.ClientSize.Width - split.SplitterWidth;
            if (available <= 0) return;

            int panel2Min = Math.Min(280, Math.Max(80, available / 3));
            int panel1Min = Math.Min(520, Math.Max(80, available - panel2Min - 40));
            split.Panel1MinSize = panel1Min;
            split.Panel2MinSize = panel2Min;

            if (splitterInitialized) return;
            int desiredRight = 320;
            int left = split.ClientSize.Width - desiredRight - split.SplitterWidth;
            int safeLeft = Math.Clamp(left, split.Panel1MinSize, split.ClientSize.Width - split.Panel2MinSize - split.SplitterWidth);
            split.SplitterDistance = safeLeft;
            splitterInitialized = true;
        };
        split.Panel1.Controls.Add(middleHost);
        split.Panel2.Controls.Add(_reviewCard);

        var bottomBar = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _toggleLogsButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _toggleLogsButton.Click += (_, _) =>
        {
            _logsVisible = !_logsVisible;
            _toggleLogsButton.Text = _logsVisible ? "Hide Logs" : "Show Logs";
            _gridCard.Visible = !_logsVisible;
            _logsCard.Visible = _logsVisible;
            RefreshLogView();
        };
        bottomBar.Controls.Add(_toggleLogsButton);
        bottomBar.Resize += (_, _) =>
        {
            _toggleLogsButton.Location = new Point(
                Math.Max(0, bottomBar.ClientSize.Width - _toggleLogsButton.Width - 4),
                Math.Max(0, bottomBar.ClientSize.Height - _toggleLogsButton.Height - 4));
        };

        root.Controls.Add(split, 0, 0);
        root.Controls.Add(bottomBar, 0, 1);
        _downloadsPage.Controls.Add(root);
    }

    private void BuildDownloadGrid()
    {
        _gridCard.BackColor = DarkTheme.Panel;
        _gridCard.BorderColor = DarkTheme.Border;
        _gridCard.CornerRadius = 14;

        _downloadGrid.Dock = DockStyle.Fill;
        _downloadGrid.ReadOnly = true;
        _downloadGrid.AllowUserToAddRows = false;
        _downloadGrid.AllowUserToDeleteRows = false;
        _downloadGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _downloadGrid.MultiSelect = false;
        _downloadGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _downloadGrid.RowHeadersVisible = false;
        _downloadGrid.BackgroundColor = DarkTheme.Panel;
        _downloadGrid.BorderStyle = BorderStyle.None;
        _downloadGrid.GridColor = DarkTheme.Border;
        _downloadGrid.EnableHeadersVisualStyles = false;
        _downloadGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _downloadGrid.ColumnHeadersHeight = 36;
        _downloadGrid.RowTemplate.Height = 30;
        _downloadGrid.DefaultCellStyle.BackColor = DarkTheme.Panel;
        _downloadGrid.DefaultCellStyle.ForeColor = DarkTheme.Text;
        _downloadGrid.DefaultCellStyle.SelectionBackColor = DarkTheme.AccentSoft;
        _downloadGrid.DefaultCellStyle.SelectionForeColor = Color.White;
        _downloadGrid.AlternatingRowsDefaultCellStyle.BackColor = DarkTheme.PanelAlt;
        _downloadGrid.ColumnHeadersDefaultCellStyle.BackColor = DarkTheme.PanelElevated;
        _downloadGrid.ColumnHeadersDefaultCellStyle.ForeColor = DarkTheme.Text;
        _downloadGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Regular, GraphicsUnit.Point);
        _downloadGrid.SelectionChanged += (_, _) =>
        {
            RefreshLogView();
            RefreshReviewPanelFromSelection();
        };

        _downloadGrid.Columns.Add("id", "ID");
        _downloadGrid.Columns.Add("track", "Track");
        _downloadGrid.Columns.Add("audioStatus", "Audio Status");
        _downloadGrid.Columns.Add("videoStatus", "Video Status");
        var reviewCol = new DataGridViewButtonColumn
        {
            Name = "review",
            HeaderText = "",
            Text = "Review",
            UseColumnTextForButtonValue = true,
            FlatStyle = FlatStyle.Flat
        };
        _downloadGrid.Columns.Add(reviewCol);
        _downloadGrid.CellContentClick += OnDownloadGridCellContentClick;
        _downloadGrid.Columns["id"]!.FillWeight = 12;
        _downloadGrid.Columns["track"]!.FillWeight = 49;
        _downloadGrid.Columns["audioStatus"]!.FillWeight = 15;
        _downloadGrid.Columns["videoStatus"]!.FillWeight = 15;
        _downloadGrid.Columns["review"]!.FillWeight = 9;
        _downloadGrid.Columns["review"]!.DefaultCellStyle.BackColor = DarkTheme.Accent;
        _downloadGrid.Columns["review"]!.DefaultCellStyle.ForeColor = Color.White;
        _downloadGrid.Columns["review"]!.DefaultCellStyle.SelectionBackColor = DarkTheme.Accent;
        _downloadGrid.Columns["review"]!.DefaultCellStyle.SelectionForeColor = Color.White;

        _gridCard.Controls.Add(_downloadGrid);
    }

    private void BuildLogsPanel()
    {
        _logsCard.BackColor = DarkTheme.Panel;
        _logsCard.BorderColor = DarkTheme.Border;
        _logsCard.CornerRadius = 14;

        _logList.Dock = DockStyle.Fill;
        _logList.BorderStyle = BorderStyle.None;
        _logList.BackColor = DarkTheme.PanelAlt;
        _logList.ForeColor = DarkTheme.Text;
        _logList.Font = new Font("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);
        _logsCard.Controls.Add(_logList);
    }

    private void BuildReviewPanel()
    {
        _reviewCard.BackColor = DarkTheme.Panel;
        _reviewCard.BorderColor = DarkTheme.Border;
        _reviewCard.CornerRadius = 14;

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Text = "Review",
            ForeColor = DarkTheme.Text,
            Font = new Font("Segoe UI Semibold", 12f, FontStyle.Regular, GraphicsUnit.Point)
        };

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4
        };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        _reviewTrackTitle.Text = "No download selected";
        _reviewAudioPath.Height = 56;
        _reviewVideoPath.Height = 56;

        _reviewAudioButton.Click += (_, _) =>
        {
            if (_reviewDownloadId > 0) ReviewArtifact(_reviewDownloadId, true);
            RefreshReviewPanel(_reviewDownloadId);
        };
        _deleteAudioButton.Click += (_, _) =>
        {
            if (_reviewDownloadId > 0) DeleteArtifact(_reviewDownloadId, true);
            RefreshReviewPanel(_reviewDownloadId);
        };
        _reviewVideoButton.Click += (_, _) =>
        {
            if (_reviewDownloadId > 0) ReviewArtifact(_reviewDownloadId, false);
            RefreshReviewPanel(_reviewDownloadId);
        };
        _deleteVideoButton.Click += (_, _) =>
        {
            if (_reviewDownloadId > 0) DeleteArtifact(_reviewDownloadId, false);
            RefreshReviewPanel(_reviewDownloadId);
        };

        body.Controls.Add(_reviewHint, 0, 0);
        body.Controls.Add(_reviewTrackTitle, 0, 1);
        body.Controls.Add(BuildReviewArtifactSection("Audio", _reviewAudioStatus, _reviewAudioPath, _reviewAudioButton, _deleteAudioButton), 0, 2);
        body.Controls.Add(BuildReviewArtifactSection("Video", _reviewVideoStatus, _reviewVideoPath, _reviewVideoButton, _deleteVideoButton), 0, 3);

        _reviewCard.Controls.Add(body);
        _reviewCard.Controls.Add(title);
    }

    private static Control BuildReviewArtifactSection(string sectionTitle, Label statusLabel, TextBox pathBox, Button openButton, Button deleteButton)
    {
        var section = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DarkTheme.PanelAlt,
            BorderColor = DarkTheme.Border,
            CornerRadius = 10,
            Padding = new Padding(10)
        };

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Text = sectionTitle,
            ForeColor = DarkTheme.Text,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Regular, GraphicsUnit.Point)
        };

        var meta = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        meta.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        meta.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        meta.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        actions.Controls.Add(openButton);
        actions.Controls.Add(deleteButton);

        meta.Controls.Add(statusLabel, 0, 0);
        meta.Controls.Add(pathBox, 0, 1);
        meta.Controls.Add(actions, 0, 2);

        section.Controls.Add(meta);
        section.Controls.Add(header);
        return section;
    }

    private void BuildControlsPage()
    {
        _controlsPage.BackColor = DarkTheme.Background;
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DarkTheme.Panel,
            BorderColor = DarkTheme.Border,
            CornerRadius = 14,
            Padding = new Padding(22)
        };
        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Text = "Controls",
            ForeColor = DarkTheme.Text,
            Font = new Font("Segoe UI Semibold", 13f, FontStyle.Regular, GraphicsUnit.Point)
        };
        var envPanel = BuildEnvironmentPanel();

        card.Controls.Add(envPanel);
        card.Controls.Add(title);
        _controlsPage.Controls.Add(card);
    }

    private Control BuildEnvironmentPanel()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = DarkTheme.Panel,
            ForeColor = DarkTheme.Text
        };

        var helper = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Text = "Configure and persist required keys. Values are saved locally and loaded automatically on startup.",
            ForeColor = DarkTheme.TextMuted,
            Font = new Font("Segoe UI", 9.2f, FontStyle.Regular, GraphicsUnit.Point)
        };
        var vegasReady = new CheckBox
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = "Vegas Ready Video (1080p H.264/AAC)",
            ForeColor = DarkTheme.Text,
            BackColor = Color.Transparent
        };
        vegasReady.Checked = UiPreferencesStore.Load().VegasReadyVideo;
        vegasReady.CheckedChanged += (_, _) =>
        {
            var prefs = UiPreferencesStore.Load();
            prefs.VegasReadyVideo = vegasReady.Checked;
            UiPreferencesStore.Save(prefs);
            _statusText.Text = vegasReady.Checked
                ? "Vegas Ready Video enabled."
                : "Vegas Ready Video disabled.";
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoScroll = false,
            AutoSize = false,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        (string Key, bool Secret, string Hint)[] vars =
        [
            ("ACR_HOST", false, "ACRCloud host"),
            ("ACR_ACCESS_KEY", false, "ACRCloud access key"),
            ("ACR_ACCESS_SECRET", true, "ACRCloud secret"),
            ("TG_API_ID", false, "Telegram API id"),
            ("TG_API_HASH", true, "Telegram API hash"),
            ("TG_PHONE", false, "Telegram phone number")
        ];

        grid.RowStyles.Clear();
        grid.RowCount = vars.Length;
        grid.GrowStyle = TableLayoutPanelGrowStyle.FixedSize;
        grid.Height = (vars.Length * 38) + 2;
        int row = 0;
        var existing = EnvSettingsStore.Load();
        foreach (var spec in vars)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            var lbl = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Text = spec.Key,
                ForeColor = DarkTheme.Text,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            };
            var tb = new TextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = DarkTheme.PanelAlt,
                ForeColor = DarkTheme.Text,
                PlaceholderText = spec.Hint,
                UseSystemPasswordChar = spec.Secret,
                Margin = new Padding(0, 6, 0, 6)
            };
            tb.Text = existing.TryGetValue(spec.Key, out var v) ? v : Environment.GetEnvironmentVariable(spec.Key) ?? "";
            _envInputs[spec.Key] = tb;

            grid.Controls.Add(lbl, 0, row);
            grid.Controls.Add(tb, 1, row);
            row++;
        }

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        var save = new RoundedButton { Text = "Save Env Settings", AutoSize = true };
        save.Click += (_, _) =>
        {
            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _envInputs) data[kv.Key] = kv.Value.Text.Trim();
            EnvSettingsStore.Save(data);
            EnvSettingsStore.ApplyToProcess(data);
            _controlsSaveStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
            _statusText.Text = "Environment settings saved.";
        };
        actions.Controls.Add(save);
        actions.Controls.Add(_controlsSaveStatus);

        page.Controls.Add(grid);
        page.Controls.Add(actions);
        page.Controls.Add(vegasReady);
        page.Controls.Add(helper);
        return page;
    }

    private void SetActivePage(string page)
    {
        bool showSong = string.Equals(page, "song", StringComparison.OrdinalIgnoreCase);
        bool showDownloads = string.Equals(page, "downloads", StringComparison.OrdinalIgnoreCase);
        bool showControls = string.Equals(page, "controls", StringComparison.OrdinalIgnoreCase);

        _songDetectionPage.Visible = showSong;
        _downloadsPage.Visible = showDownloads;
        _controlsPage.Visible = showControls;
        _songDetectionNav.IsActive = showSong;
        _downloadsNav.IsActive = showDownloads;
        _controlsNav.IsActive = showControls;
    }

    private void UpdateQueueLabel()
    {
        _queueLabel.Text = $"Queue: {_queuedPending} pending";
    }

    private void PositionTopRight()
    {
        Rectangle wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Left = wa.Right - Width - 12;
        Top = wa.Top + 12;
    }

    private void EnsureVisibleWindow()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (!Visible) Show();
        Activate();
    }

    private void EnsureDownloadRow(int id, string track)
    {
        if (_downloads.TryGetValue(id, out _)) return;
        var row = new DataGridViewRow();
        row.CreateCells(_downloadGrid, id.ToString(), track, "Pending", "Pending", "Review");
        _downloadGrid.Rows.Add(row);
        _downloads[id] = new DownloadItemState(id, track, row);
    }

    private void SetRowStatuses(int id, string? audioStatus, string? videoStatus)
    {
        if (!_downloads.TryGetValue(id, out var item)) return;
        if (!string.IsNullOrWhiteSpace(audioStatus))
        {
            item.AudioStatus = audioStatus;
            item.Row.Cells[2].Value = audioStatus;
        }
        if (!string.IsNullOrWhiteSpace(videoStatus))
        {
            item.VideoStatus = videoStatus;
            item.Row.Cells[3].Value = videoStatus;
        }

        item.Row.Cells[2].Style.ForeColor = StatusColor(item.AudioStatus);
        item.Row.Cells[3].Style.ForeColor = StatusColor(item.VideoStatus);
        if (_reviewDownloadId == id) RefreshReviewPanel(id);
    }

    private static string InferStatusFromProgress(int percent, string text)
    {
        if (text.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no results", StringComparison.OrdinalIgnoreCase))
            return "Failed";
        if (text.Contains("done", StringComparison.OrdinalIgnoreCase) || percent >= 100) return "Success";
        if (percent > 0 || text.Contains("download", StringComparison.OrdinalIgnoreCase) || text.Contains("running", StringComparison.OrdinalIgnoreCase)) return "Downloading";
        return "Pending";
    }

    private static Color StatusColor(string status)
    {
        if (status.Equals("Failed", StringComparison.OrdinalIgnoreCase)) return DarkTheme.Danger;
        if (status.Equals("Success", StringComparison.OrdinalIgnoreCase)) return DarkTheme.Success;
        if (status.Equals("Downloading", StringComparison.OrdinalIgnoreCase)) return DarkTheme.Accent;
        if (status.Equals("Skipped", StringComparison.OrdinalIgnoreCase)) return DarkTheme.TextMuted;
        if (status.Equals("Deleted", StringComparison.OrdinalIgnoreCase)) return DarkTheme.TextMuted;
        return DarkTheme.TextMuted;
    }

    private void SelectRowById(int id)
    {
        if (!_downloads.TryGetValue(id, out var item)) return;
        _downloadGrid.ClearSelection();
        item.Row.Selected = true;
        RefreshReviewPanel(id);
    }

    private void OnDownloadGridCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_downloadGrid.Columns[e.ColumnIndex].Name != "review") return;

        object? idObj = _downloadGrid.Rows[e.RowIndex].Cells[0].Value;
        if (idObj is null) return;
        if (!int.TryParse(idObj.ToString(), out int id)) return;
        _downloadGrid.ClearSelection();
        _downloadGrid.Rows[e.RowIndex].Selected = true;
        RefreshReviewPanel(id);
        _statusText.Text = $"Reviewing download #{id}";
    }

    private void RefreshLogView()
    {
        if (!_logsVisible)
        {
            _logList.Items.Clear();
            return;
        }

        if (_downloadGrid.SelectedRows.Count == 0)
        {
            _logList.Items.Clear();
            return;
        }

        if (_downloadGrid.SelectedRows[0].Cells[0].Value is not string idText || !int.TryParse(idText, out int id))
        {
            _logList.Items.Clear();
            return;
        }

        if (!_downloads.TryGetValue(id, out var item))
        {
            _logList.Items.Clear();
            return;
        }

        _logList.BeginUpdate();
        _logList.Items.Clear();
        for (int i = item.Logs.Count - 1; i >= 0; i--)
        {
            _logList.Items.Add(item.Logs[i]);
        }
        _logList.EndUpdate();
    }

    private void RefreshReviewPanelFromSelection()
    {
        if (_downloadGrid.SelectedRows.Count == 0)
        {
            RefreshReviewPanel(-1);
            return;
        }

        object? idObj = _downloadGrid.SelectedRows[0].Cells[0].Value;
        if (idObj is null || !int.TryParse(idObj.ToString(), out int id))
        {
            RefreshReviewPanel(-1);
            return;
        }

        RefreshReviewPanel(id);
    }

    private void RefreshReviewPanel(int id)
    {
        _reviewDownloadId = id;
        if (!_downloads.TryGetValue(id, out var item))
        {
            _reviewTrackTitle.Text = "No download selected";
            _reviewAudioStatus.Text = "Status: (none)";
            _reviewAudioPath.Text = "(none)";
            _reviewVideoStatus.Text = "Status: (none)";
            _reviewVideoPath.Text = "(none)";
            _reviewAudioButton.Enabled = false;
            _deleteAudioButton.Enabled = false;
            _reviewVideoButton.Enabled = false;
            _deleteVideoButton.Enabled = false;
            return;
        }

        _reviewTrackTitle.Text = $"#{item.Id}  {item.Track}";
        _reviewAudioStatus.Text = $"Status: {item.AudioStatus}";
        _reviewVideoStatus.Text = $"Status: {item.VideoStatus}";
        _reviewAudioStatus.ForeColor = StatusColor(item.AudioStatus);
        _reviewVideoStatus.ForeColor = StatusColor(item.VideoStatus);
        _reviewAudioPath.Text = string.IsNullOrWhiteSpace(item.AudioFilePath) ? "(none)" : item.AudioFilePath;
        _reviewVideoPath.Text = string.IsNullOrWhiteSpace(item.VideoFilePath) ? "(none)" : item.VideoFilePath;

        bool hasAudio = !string.IsNullOrWhiteSpace(item.AudioFilePath) && File.Exists(item.AudioFilePath);
        bool hasVideo = !string.IsNullOrWhiteSpace(item.VideoFilePath) && File.Exists(item.VideoFilePath);
        _reviewAudioButton.Enabled = hasAudio;
        _deleteAudioButton.Enabled = hasAudio;
        _reviewVideoButton.Enabled = hasVideo;
        _deleteVideoButton.Enabled = hasVideo;
    }

    private void RunOnUi(Action action)
    {
        if (InvokeRequired)
        {
            BeginInvoke(action);
            return;
        }
        if (IsDisposed) return;
        action();
    }

    private sealed class DownloadItemState(int id, string track, DataGridViewRow row)
    {
        public int Id { get; } = id;
        public string Track { get; } = track;
        public DataGridViewRow Row { get; } = row;
        public string AudioStatus { get; set; } = "Pending";
        public string VideoStatus { get; set; } = "Pending";
        public string? AudioFilePath { get; set; }
        public string? VideoFilePath { get; set; }
        public List<string> Logs { get; } = [];
    }
}

internal sealed class DownloadReviewForm : Form
{
    private readonly Func<string> _getAudioStatus;
    private readonly Func<string> _getVideoStatus;
    private readonly Func<string?> _getAudioPath;
    private readonly Func<string?> _getVideoPath;
    private readonly Action _reviewAudio;
    private readonly Action _reviewVideo;
    private readonly Action _deleteAudio;
    private readonly Action _deleteVideo;
    private readonly Label _audioStatus = new() { AutoSize = true };
    private readonly Label _videoStatus = new() { AutoSize = true };
    private readonly Label _audioPath = new() { AutoSize = true, MaximumSize = new Size(720, 0) };
    private readonly Label _videoPath = new() { AutoSize = true, MaximumSize = new Size(720, 0) };
    private readonly RoundedButton _reviewAudioButton = new() { Text = "Review Audio" };
    private readonly RoundedButton _deleteAudioButton = new() { Text = "Delete Audio" };
    private readonly RoundedButton _reviewVideoButton = new() { Text = "Review Video" };
    private readonly RoundedButton _deleteVideoButton = new() { Text = "Delete Video" };

    public DownloadReviewForm(
        int id,
        string track,
        Func<string> getAudioStatus,
        Func<string> getVideoStatus,
        Func<string?> getAudioPath,
        Func<string?> getVideoPath,
        Action reviewAudio,
        Action reviewVideo,
        Action deleteAudio,
        Action deleteVideo)
    {
        _getAudioStatus = getAudioStatus;
        _getVideoStatus = getVideoStatus;
        _getAudioPath = getAudioPath;
        _getVideoPath = getVideoPath;
        _reviewAudio = reviewAudio;
        _reviewVideo = reviewVideo;
        _deleteAudio = deleteAudio;
        _deleteVideo = deleteVideo;

        Text = $"Review Download #{id}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        Width = 820;
        Height = 360;
        BackColor = DarkTheme.Background;
        ForeColor = DarkTheme.Text;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        var title = new Label
        {
            Dock = DockStyle.Fill,
            Text = track,
            ForeColor = DarkTheme.Text,
            Font = new Font("Segoe UI Semibold", 12f, FontStyle.Regular, GraphicsUnit.Point)
        };

        root.Controls.Add(title, 0, 0);
        root.Controls.Add(BuildArtifactPanel("Audio", _audioStatus, _audioPath, _reviewAudioButton, _deleteAudioButton), 0, 1);
        root.Controls.Add(BuildArtifactPanel("Video", _videoStatus, _videoPath, _reviewVideoButton, _deleteVideoButton), 0, 2);
        Controls.Add(root);

        _reviewAudioButton.Click += (_, _) => { _reviewAudio(); RefreshState(); };
        _deleteAudioButton.Click += (_, _) => { _deleteAudio(); RefreshState(); };
        _reviewVideoButton.Click += (_, _) => { _reviewVideo(); RefreshState(); };
        _deleteVideoButton.Click += (_, _) => { _deleteVideo(); RefreshState(); };

        Shown += (_, _) => RefreshState();
    }

    private Control BuildArtifactPanel(string title, Label statusLabel, Label pathLabel, Button reviewButton, Button deleteButton)
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DarkTheme.Panel,
            BorderColor = DarkTheme.Border,
            CornerRadius = 12,
            Padding = new Padding(12)
        };
        var heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Text = title,
            ForeColor = DarkTheme.Text,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Regular, GraphicsUnit.Point)
        };
        statusLabel.ForeColor = DarkTheme.TextMuted;
        pathLabel.ForeColor = DarkTheme.TextMuted;

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        reviewButton.AutoSize = true;
        deleteButton.AutoSize = true;
        actions.Controls.Add(reviewButton);
        actions.Controls.Add(deleteButton);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(statusLabel, 0, 0);
        body.Controls.Add(pathLabel, 0, 1);

        card.Controls.Add(body);
        card.Controls.Add(actions);
        card.Controls.Add(heading);
        return card;
    }

    private void RefreshState()
    {
        string audioStatus = _getAudioStatus();
        string videoStatus = _getVideoStatus();
        string? audioPath = _getAudioPath();
        string? videoPath = _getVideoPath();

        _audioStatus.Text = $"Status: {audioStatus}";
        _videoStatus.Text = $"Status: {videoStatus}";
        _audioPath.Text = $"Path: {(string.IsNullOrWhiteSpace(audioPath) ? "(none)" : audioPath)}";
        _videoPath.Text = $"Path: {(string.IsNullOrWhiteSpace(videoPath) ? "(none)" : videoPath)}";

        bool audioExists = !string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath);
        bool videoExists = !string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath);
        _reviewAudioButton.Enabled = audioExists;
        _deleteAudioButton.Enabled = audioExists;
        _reviewVideoButton.Enabled = videoExists;
        _deleteVideoButton.Enabled = videoExists;
    }
}

internal sealed class SongDetectedPopup : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private readonly Label _title = new()
    {
        Dock = DockStyle.Top,
        Height = 22,
        Text = "Song detected",
        ForeColor = DarkTheme.TextMuted,
        Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Point)
    };
    private readonly Label _song = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        ForeColor = DarkTheme.Text,
        Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Regular, GraphicsUnit.Point)
    };
    private readonly RoundedButton _downloadButton = new()
    {
        Text = "Download",
        AutoSize = false,
        Width = 112,
        Height = 34
    };
    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 18000 };
    private TrackMetadata? _current;
    private Action<TrackMetadata>? _onDownload;

    public SongDetectedPopup()
    {
        ShowIcon = true;
        Icon = AppIconLoader.GetIconOrDefault();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = DarkTheme.Panel;
        ForeColor = DarkTheme.Text;
        Width = 370;
        Height = 110;
        TopMost = true;

        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DarkTheme.Panel,
            BorderColor = DarkTheme.Border,
            CornerRadius = 12,
            Padding = new Padding(12)
        };

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var actionHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _downloadButton.Location = new Point(8, 20);
        _downloadButton.Click += (_, _) =>
        {
            if (_current is null || _onDownload is null) return;
            _onDownload(_current);
            Hide();
        };
        actionHost.Controls.Add(_downloadButton);

        body.Controls.Add(_title, 0, 0);
        body.Controls.Add(_song, 0, 1);
        body.Controls.Add(actionHost, 1, 0);
        body.SetRowSpan(actionHost, 2);

        card.Controls.Add(body);
        Controls.Add(card);

        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
        Deactivate += (_, _) =>
        {
            // Keep it non-intrusive. If user clicks elsewhere, it can fade out naturally.
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    public void ShowSong(TrackMetadata track, Action<TrackMetadata> onDownload)
    {
        _current = track;
        _onDownload = onDownload;
        _song.Text = track.ToString();
        PositionBottomRight();
        if (!Visible) Show();
        BringToFront();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void PositionBottomRight()
    {
        Rectangle wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Left = wa.Right - Width - 14;
        Top = wa.Bottom - Height - 14;
    }
}

internal sealed class SetupRequiredForm : Form
{
    private readonly Dictionary<string, TextBox> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Label _errorLabel = new()
    {
        Dock = DockStyle.Bottom,
        Height = 22,
        ForeColor = DarkTheme.Danger
    };

    public SetupRequiredForm(Dictionary<string, string> existing)
    {
        Text = "Setup Required";
        ShowIcon = true;
        Icon = AppIconLoader.GetIconOrDefault();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        Width = 680;
        Height = 460;
        BackColor = DarkTheme.Background;
        ForeColor = DarkTheme.Text;
        Font = new Font("Segoe UI", 9.2f, FontStyle.Regular, GraphicsUnit.Point);

        var root = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DarkTheme.Panel,
            BorderColor = DarkTheme.Border,
            CornerRadius = 12,
            Padding = new Padding(16)
        };

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Text = "Setup Required",
            ForeColor = DarkTheme.Text,
            Font = new Font("Segoe UI Semibold", 13f, FontStyle.Regular, GraphicsUnit.Point)
        };
        var intro = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            Text = "Enter your own Telegram and ACR credentials to enable downloads.",
            ForeColor = DarkTheme.TextMuted
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(0, 8, 0, 8)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.GrowStyle = TableLayoutPanelGrowStyle.FixedSize;

        (string Key, bool Secret, string Hint)[] fields =
        [
            ("TG_API_ID", false, "Telegram API id (numeric)"),
            ("TG_API_HASH", true, "Telegram API hash"),
            ("TG_PHONE", false, "Phone format: +1234567890"),
            ("ACR_HOST", false, "identify-*.acrcloud.com"),
            ("ACR_ACCESS_KEY", false, "ACR access key"),
            ("ACR_ACCESS_SECRET", true, "ACR access secret")
        ];

        for (int i = 0; i < fields.Length; i++)
        {
            var spec = fields[i];
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            var label = new Label
            {
                Dock = DockStyle.Fill,
                Text = spec.Key,
                ForeColor = DarkTheme.Text,
                TextAlign = ContentAlignment.MiddleLeft
            };
            var tb = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = DarkTheme.PanelAlt,
                ForeColor = DarkTheme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                PlaceholderText = spec.Hint,
                UseSystemPasswordChar = spec.Secret,
                Margin = new Padding(0, 9, 0, 9)
            };
            tb.Text = existing.TryGetValue(spec.Key, out string? v) ? v : "";
            _inputs[spec.Key] = tb;
            grid.Controls.Add(label, 0, i);
            grid.Controls.Add(tb, 1, i);
        }
        grid.Height = (fields.Length * 48) + 20;

        var scrollHost = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.Transparent
        };
        scrollHost.Controls.Add(grid);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var save = new RoundedButton { Text = "Save and Continue", AutoSize = true };
        var cancel = new RoundedButton { Text = "Cancel", AutoSize = true, BackColor = DarkTheme.PanelElevated };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        save.Click += (_, _) =>
        {
            if (!TryValidate(out string error))
            {
                _errorLabel.Text = error;
                return;
            }
            DialogResult = DialogResult.OK;
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);

        root.Controls.Add(scrollHost);
        root.Controls.Add(_errorLabel);
        root.Controls.Add(actions);
        root.Controls.Add(intro);
        root.Controls.Add(title);
        Controls.Add(root);
    }

    public Dictionary<string, string> GetValues()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _inputs) result[kv.Key] = kv.Value.Text.Trim();
        return result;
    }

    private bool TryValidate(out string error)
    {
        error = "";
        foreach (var kv in _inputs)
        {
            if (string.IsNullOrWhiteSpace(kv.Value.Text))
            {
                error = $"Missing value: {kv.Key}";
                return false;
            }
        }

        string tgApiId = _inputs["TG_API_ID"].Text.Trim();
        if (!long.TryParse(tgApiId, out _))
        {
            error = "TG_API_ID must be numeric.";
            return false;
        }

        string tgPhone = _inputs["TG_PHONE"].Text.Trim();
        if (!Regex.IsMatch(tgPhone, @"^\+\d{7,15}$"))
        {
            error = "TG_PHONE must be in format +{countrycode}{number}.";
            return false;
        }

        return true;
    }
}

internal sealed class SimpleInputDialog : Form
{
    private readonly TextBox _input = new()
    {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = DarkTheme.PanelAlt,
        ForeColor = DarkTheme.Text
    };

    public string Value => _input.Text.Trim();

    public SimpleInputDialog(string title, string prompt, bool secret)
    {
        Text = title;
        ShowIcon = true;
        Icon = AppIconLoader.GetIconOrDefault();
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Width = 520;
        Height = 190;
        BackColor = DarkTheme.Background;
        ForeColor = DarkTheme.Text;
        Font = new Font("Segoe UI", 9.2f, FontStyle.Regular, GraphicsUnit.Point);

        _input.UseSystemPasswordChar = secret;

        var root = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DarkTheme.Panel,
            BorderColor = DarkTheme.Border,
            CornerRadius = 12,
            Padding = new Padding(14)
        };

        var lbl = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            Text = prompt,
            ForeColor = DarkTheme.Text,
            TextAlign = ContentAlignment.MiddleLeft
        };

        var inputHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = 34,
            BackColor = Color.Transparent
        };
        inputHost.Controls.Add(_input);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var ok = new RoundedButton { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new RoundedButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.Add(cancel);
        actions.Controls.Add(ok);

        AcceptButton = ok;
        CancelButton = cancel;

        root.Controls.Add(inputHost);
        root.Controls.Add(actions);
        root.Controls.Add(lbl);
        Controls.Add(root);
    }
}

internal static class AppPaths
{
    public static readonly string LocalDataFolderName = ResolveLocalDataFolderName();
    public static readonly string LocalDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        LocalDataFolderName);

    private static string ResolveLocalDataFolderName()
    {
        string raw = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyProductAttribute>()?.Product
            ?? Assembly.GetEntryAssembly()?.GetName().Name
            ?? "auroradl";

        if (string.IsNullOrWhiteSpace(raw)) raw = "auroradl";

        char[] invalid = Path.GetInvalidFileNameChars();
        var safe = new System.Text.StringBuilder(raw.Length);
        foreach (char ch in raw.Trim())
        {
            safe.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        string result = safe.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? "auroradl" : result;
    }
}

internal static class EnvSettingsStore
{
    private static readonly string BaseDir = AppPaths.LocalDataDir;

    private static readonly string FilePath = Path.Combine(BaseDir, "env-settings.json");
    private static readonly string[] RequiredKeys =
    [
        "TG_API_ID",
        "TG_API_HASH",
        "TG_PHONE",
        "ACR_HOST",
        "ACR_ACCESS_KEY",
        "ACR_ACCESS_SECRET"
    ];

    public static Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            string json = File.ReadAllText(FilePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict is null) return [];

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in dict)
            {
                result[kv.Key] = DecodeValue(kv.Value ?? "");
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    public static void Save(Dictionary<string, string> values)
    {
        Directory.CreateDirectory(BaseDir);
        var encoded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in values)
        {
            encoded[kv.Key] = EncodeValue(kv.Value ?? "");
        }
        string json = JsonSerializer.Serialize(encoded, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }

    public static bool HasRequiredCredentialsInFile()
    {
        if (!File.Exists(FilePath)) return false;
        var values = Load();
        foreach (string key in RequiredKeys)
        {
            if (!values.TryGetValue(key, out string? val) || string.IsNullOrWhiteSpace(val)) return false;
        }
        return ValidateCredentialFormat(values);
    }

    private static bool ValidateCredentialFormat(Dictionary<string, string> values)
    {
        if (!values.TryGetValue("TG_API_ID", out string? tgApiId) ||
            !long.TryParse((tgApiId ?? "").Trim(), out _))
            return false;

        if (!values.TryGetValue("TG_PHONE", out string? tgPhone) ||
            !Regex.IsMatch((tgPhone ?? "").Trim(), @"^\+\d{7,15}$"))
            return false;

        return true;
    }

    public static void LoadAndApply()
    {
        ApplyToProcess(Load());
    }

    public static void ApplyToProcess(Dictionary<string, string> values)
    {
        foreach (var kv in values)
        {
            string val = kv.Value ?? "";
            Environment.SetEnvironmentVariable(kv.Key, val, EnvironmentVariableTarget.Process);
        }

        Mirror(values, "ACR_HOST", "ACRCLOUD_HOST");
        Mirror(values, "ACR_ACCESS_KEY", "ACRCLOUD_ACCESS_KEY");
        Mirror(values, "ACR_ACCESS_SECRET", "ACRCLOUD_ACCESS_SECRET");
    }

    private static void Mirror(Dictionary<string, string> values, string primary, string alias)
    {
        string val = values.TryGetValue(primary, out var v) ? v : Environment.GetEnvironmentVariable(primary) ?? "";
        Environment.SetEnvironmentVariable(alias, val, EnvironmentVariableTarget.Process);
    }

    private static string EncodeValue(string plain)
    {
        try
        {
            byte[] input = System.Text.Encoding.UTF8.GetBytes(plain);
            byte[] obfuscated = XorObfuscate(input);
            return "enc:" + Convert.ToBase64String(obfuscated);
        }
        catch
        {
            return "plain:" + plain;
        }
    }

    private static string DecodeValue(string stored)
    {
        try
        {
            if (stored.StartsWith("enc:", StringComparison.Ordinal))
            {
                string b64 = stored.Substring(4);
                byte[] obfuscated = Convert.FromBase64String(b64);
                byte[] clear = XorObfuscate(obfuscated);
                return System.Text.Encoding.UTF8.GetString(clear);
            }
            if (stored.StartsWith("plain:", StringComparison.Ordinal))
            {
                return stored.Substring(6);
            }
            return stored;
        }
        catch
        {
            return "";
        }
    }

    private static byte[] XorObfuscate(byte[] input)
    {
        byte[] key = System.Text.Encoding.UTF8.GetBytes("auroradl:v1");
        byte[] output = new byte[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = (byte)(input[i] ^ key[i % key.Length]);
        }
        return output;
    }
}

internal sealed class UiPreferences
{
    public bool FollowMeMode { get; set; } = true;
    public bool VegasReadyVideo { get; set; }
}

internal static class UiPreferencesStore
{
    private static readonly string BaseDir = AppPaths.LocalDataDir;

    private static readonly string FilePath = Path.Combine(BaseDir, "ui-prefs.json");
    private static UiPreferences? _cache;

    public static UiPreferences Load()
    {
        if (_cache is not null) return _cache;
        try
        {
            if (!File.Exists(FilePath)) return _cache = new UiPreferences();
            string json = File.ReadAllText(FilePath);
            _cache = JsonSerializer.Deserialize<UiPreferences>(json) ?? new UiPreferences();
            return _cache;
        }
        catch
        {
            return _cache = new UiPreferences();
        }
    }

    public static bool GetFollowMeMode() => Load().FollowMeMode;
    public static bool GetVegasReadyVideo() => Load().VegasReadyVideo;

    public static void Save(UiPreferences prefs)
    {
        _cache = prefs;
        Directory.CreateDirectory(BaseDir);
        string json = JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}

internal sealed class DetectionHistoryEntry
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string? Isrc { get; set; }
}

internal static class DetectionHistoryStore
{
    private static readonly string BaseDir = AppPaths.LocalDataDir;

    private static readonly string FilePath = Path.Combine(BaseDir, "detection-history.json");

    public static List<TrackMetadata> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            string json = File.ReadAllText(FilePath);
            var entries = JsonSerializer.Deserialize<List<DetectionHistoryEntry>>(json) ?? [];
            var tracks = new List<TrackMetadata>(entries.Count);
            foreach (var item in entries)
            {
                if (string.IsNullOrWhiteSpace(item.Title)) continue;
                tracks.Add(new TrackMetadata(item.Title.Trim(), item.Artist?.Trim() ?? "", item.Isrc));
                if (tracks.Count >= 10) break;
            }
            return tracks;
        }
        catch
        {
            return [];
        }
    }

    public static void Save(List<TrackMetadata> tracks)
    {
        var entries = new List<DetectionHistoryEntry>(Math.Min(10, tracks.Count));
        for (int i = 0; i < tracks.Count && i < 10; i++)
        {
            var track = tracks[i];
            entries.Add(new DetectionHistoryEntry
            {
                Title = track.Title,
                Artist = track.Artist,
                Isrc = track.Isrc
            });
        }

        Directory.CreateDirectory(BaseDir);
        string json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}

internal static class DarkTheme
{
    public static Color Background => ColorTranslator.FromHtml("#121212");
    public static Color Sidebar => ColorTranslator.FromHtml("#171A21");
    public static Color Panel => ColorTranslator.FromHtml("#1E222B");
    public static Color PanelAlt => ColorTranslator.FromHtml("#191D25");
    public static Color PanelElevated => ColorTranslator.FromHtml("#252B36");
    public static Color Text => ColorTranslator.FromHtml("#E6EAF2");
    public static Color TextMuted => ColorTranslator.FromHtml("#9AA7BB");
    public static Color Accent => ColorTranslator.FromHtml("#5B8CFF");
    public static Color AccentSoft => ColorTranslator.FromHtml("#3D5D9A");
    public static Color ProgressBarTelegram => ColorTranslator.FromHtml("#2AABEE");
    public static Color ProgressBarYouTube => ColorTranslator.FromHtml("#8A63FF");
    public static Color Border => ColorTranslator.FromHtml("#2F3642");
    public static Color Success => ColorTranslator.FromHtml("#43C98B");
    public static Color Danger => ColorTranslator.FromHtml("#FF6B7A");
}

internal class RoundedPanel : Panel
{
    public int CornerRadius { get; set; } = 12;
    public Color BorderColor { get; set; } = DarkTheme.Border;

    public RoundedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiShape.RoundRect(rect, CornerRadius);
        using var bg = new SolidBrush(BackColor);
        using var border = new Pen(BorderColor, 1f);
        e.Graphics.FillPath(bg, path);
        e.Graphics.DrawPath(border, path);
        base.OnPaint(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 2 || Height <= 2) return;
        using var path = UiShape.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        Region?.Dispose();
        Region = new Region(path);
    }
}

internal sealed class SidebarNavButton : Button
{
    private bool _hover;
    private bool _active;

    public bool IsActive
    {
        get => _active;
        set
        {
            _active = value;
            Invalidate();
        }
    }

    public SidebarNavButton(string text)
    {
        Text = text;
        AutoSize = false;
        Width = 156;
        Height = 48;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 1;
        Cursor = Cursors.Hand;
        ForeColor = DarkTheme.TextMuted;
        BackColor = DarkTheme.Panel;
        FlatAppearance.BorderColor = DarkTheme.Border;
        TextAlign = ContentAlignment.MiddleLeft;
        Padding = new Padding(14, 0, 10, 0);
        Font = new Font("Segoe UI Semibold", 10f, FontStyle.Regular, GraphicsUnit.Point);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Color bg = _active ? DarkTheme.AccentSoft : _hover ? DarkTheme.PanelElevated : DarkTheme.Panel;
        Color fg = _active ? Color.White : DarkTheme.Text;
        using var path = UiShape.RoundRect(rect, 10);
        using var brush = new SolidBrush(bg);
        using var pen = new Pen(_active ? DarkTheme.Accent : DarkTheme.Border, 1f);
        pevent.Graphics.FillPath(brush, path);
        pevent.Graphics.DrawPath(pen, path);
        TextRenderer.DrawText(
            pevent.Graphics,
            Text,
            Font,
            rect,
            fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class RoundedButton : Button
{
    private bool _hover;

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        BackColor = DarkTheme.Accent;
        ForeColor = Color.White;
        Padding = new Padding(14, 6, 14, 6);
        Font = new Font("Segoe UI Semibold", 9f, FontStyle.Regular, GraphicsUnit.Point);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Color fill = _hover ? ControlPaint.Light(DarkTheme.Accent, 0.08f) : DarkTheme.Accent;
        using var path = UiShape.RoundRect(rect, 10);
        using var brush = new SolidBrush(fill);
        pevent.Graphics.FillPath(brush, path);
        TextRenderer.DrawText(pevent.Graphics, Text, Font, rect, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

internal sealed class DownloadProgressCard : RoundedPanel
{
    private readonly Label _title = new();
    private readonly Label _status = new();
    private readonly Label _percent = new();
    private readonly ModernProgressBar _bar = new();

    public int Progress => _bar.ValuePercent;

    public DownloadProgressCard(string title, string iconText, Color barColor)
    {
        BackColor = DarkTheme.Panel;
        BorderColor = DarkTheme.Border;
        CornerRadius = 14;
        Margin = new Padding(0, 0, 10, 0);
        Padding = new Padding(14);

        var icon = new IconBadge(iconText, barColor) { Dock = DockStyle.Left, Width = 46 };
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _title.Dock = DockStyle.Top;
        _title.Height = 22;
        _title.Font = new Font("Segoe UI Semibold", 10.2f, FontStyle.Regular, GraphicsUnit.Point);
        _title.ForeColor = DarkTheme.Text;
        _title.Text = title;

        _status.Dock = DockStyle.Top;
        _status.Height = 20;
        _status.ForeColor = DarkTheme.TextMuted;
        _status.Text = "Idle";

        _percent.Dock = DockStyle.Top;
        _percent.Height = 20;
        _percent.ForeColor = DarkTheme.TextMuted;
        _percent.Text = "0%";

        _bar.Dock = DockStyle.Top;
        _bar.Height = 16;
        _bar.FillColor = barColor;
        _bar.TrackColor = DarkTheme.PanelAlt;

        right.Controls.Add(_bar);
        right.Controls.Add(_percent);
        right.Controls.Add(_status);
        right.Controls.Add(_title);

        Controls.Add(right);
        Controls.Add(icon);
    }

    public void SetProgress(int value, string status)
    {
        value = Math.Clamp(value, 0, 100);
        _bar.ValuePercent = value;
        _status.Text = status;
        _percent.Text = $"{value}%";
        _bar.Invalidate();
    }
}

internal sealed class ModernProgressBar : Control
{
    public int ValuePercent { get; set; }
    public Color FillColor { get; set; } = DarkTheme.Accent;
    public Color TrackColor { get; set; } = DarkTheme.PanelAlt;

    public ModernProgressBar()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var trackPath = UiShape.RoundRect(rect, Math.Max(4, Height / 2));
        using var trackBrush = new SolidBrush(TrackColor);
        e.Graphics.FillPath(trackBrush, trackPath);

        int fillWidth = (int)Math.Round((Width - 1) * (Math.Clamp(ValuePercent, 0, 100) / 100.0));
        if (fillWidth <= 0) return;
        var fillRect = new Rectangle(0, 0, fillWidth, Height - 1);
        using var fillPath = UiShape.RoundRect(fillRect, Math.Max(4, Height / 2));
        using var fillBrush = new SolidBrush(FillColor);
        e.Graphics.FillPath(fillBrush, fillPath);
    }
}

internal sealed class SpectrumBarsControl : Control
{
    private float[] _levels = new float[48];
    private float[] _smoothed = new float[48];

    public SpectrumBarsControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        Height = 92;
    }

    public void SetLevels(float[] levels)
    {
        if (levels.Length != _levels.Length)
        {
            _levels = new float[levels.Length];
            Array.Resize(ref _smoothed, levels.Length);
        }
        Array.Copy(levels, _levels, Math.Min(levels.Length, _levels.Length));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(BackColor);

        int n = _levels.Length;
        if (n <= 0 || Width <= 8 || Height <= 8) return;

        float gap = 2f;
        float barW = Math.Max(2f, (Width - (n - 1) * gap) / n);
        float h = Height - 8f;
        const float dotGap = 2f;
        const float minDotH = 2f;
        for (int i = 0; i < n; i++)
        {
            float target = Math.Clamp(_levels[i], 0f, 1f);
            _smoothed[i] = target > _smoothed[i]
                ? (_smoothed[i] * 0.35f) + (target * 0.65f)
                : Math.Max(0f, _smoothed[i] - 0.03f);

            float x = i * (barW + gap);
            float barH = Math.Max(2f, _smoothed[i] * h);
            float y = Height - barH - 2f;
            float dotH = Math.Max(minDotH, Math.Min(4f, barW + 0.5f));
            int dots = Math.Max(1, (int)Math.Floor(barH / (dotH + dotGap)));
            for (int d = 0; d < dots; d++)
            {
                float dy = Height - 2f - ((d + 1) * dotH) - (d * dotGap);
                if (dy < y) break;
                float meterT = 1f - ((dy + dotH * 0.5f) / Math.Max(1f, Height));
                Color c = MeterColor(meterT);
                using var b = new SolidBrush(Color.FromArgb(230, c));
                e.Graphics.FillRectangle(b, x, dy, barW, dotH);
            }
        }
    }

    private static Color MeterColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t < 0.55f)
        {
            float q = t / 0.55f;
            int r = (int)(50 + (205 * q));
            return Color.FromArgb(r, 235, 65);
        }
        if (t < 0.82f)
        {
            float q = (t - 0.55f) / 0.27f;
            int g = (int)(235 - (25 * q));
            return Color.FromArgb(245, g, 60);
        }

        float p = (t - 0.82f) / 0.18f;
        int gg = (int)(200 - (140 * p));
        return Color.FromArgb(245, gg, 50);
    }
}

internal sealed class IconBadge : Control
{
    private readonly string _text;
    private readonly Color _color;

    public IconBadge(string text, Color color)
    {
        _text = text;
        _color = color;
        DoubleBuffered = true;
        Height = 46;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int size = Math.Min(34, Math.Min(Width - 2, Height - 2));
        var rect = new Rectangle(4, (Height - size) / 2, size, size);
        using var b = new SolidBrush(_color);
        e.Graphics.FillEllipse(b, rect);
        TextRenderer.DrawText(
            e.Graphics,
            _text,
            new Font("Segoe UI Semibold", 8.5f, FontStyle.Regular, GraphicsUnit.Point),
            rect,
            Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

internal static class UiShape
{
    public static GraphicsPath RoundRect(Rectangle rect, int radius)
    {
        int d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal static class AppIconLoader
{
    private static Icon? _cached;

    public static Icon GetIconOrDefault()
    {
        if (_cached is not null) return _cached;

        try
        {
            string pngPath = Path.Combine(AppContext.BaseDirectory, "auroradl.png");
            if (!File.Exists(pngPath))
            {
                string alt = Path.Combine(AppContext.BaseDirectory, "auroradl", "auroradl.png");
                if (File.Exists(alt)) pngPath = alt;
            }
            if (!File.Exists(pngPath)) return SystemIcons.Application;

            using var bmp = new Bitmap(pngPath);
            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using var temp = Icon.FromHandle(hIcon);
                _cached = (Icon)temp.Clone();
                return _cached;
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}

internal sealed class DownloadDestinationDialog : Form
{
    private readonly CheckBox _downloadAudio = new() { AutoSize = true, Text = "Download audio", Checked = true, ForeColor = DarkTheme.Text };
    private readonly CheckBox _downloadVideo = new() { AutoSize = true, Text = "Download video", Checked = true, ForeColor = DarkTheme.Text };
    private readonly TextBox _audioPath = new() { Width = 360, BorderStyle = BorderStyle.FixedSingle, BackColor = DarkTheme.PanelAlt, ForeColor = DarkTheme.Text };
    private readonly TextBox _videoPath = new() { Width = 360, BorderStyle = BorderStyle.FixedSingle, BackColor = DarkTheme.PanelAlt, ForeColor = DarkTheme.Text };
    private readonly bool _centerOnOwner;

    public string AudioPath => _audioPath.Text.Trim();
    public string VideoPath => _videoPath.Text.Trim();
    public bool DownloadAudio => _downloadAudio.Checked;
    public bool DownloadVideo => _downloadVideo.Checked;

    public DownloadDestinationDialog(string defaultAudioPath, string defaultVideoPath, bool centerOnOwner)
    {
        _centerOnOwner = centerOnOwner;
        Text = "Choose Download Destinations";
        ShowIcon = true;
        Icon = AppIconLoader.GetIconOrDefault();
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = centerOnOwner ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
        TopMost = !centerOnOwner;
        Width = 720;
        Height = 300;
        BackColor = DarkTheme.Background;
        ForeColor = DarkTheme.Text;
        Font = new Font("Segoe UI", 9.4f, FontStyle.Regular, GraphicsUnit.Point);

        _audioPath.Text = defaultAudioPath;
        _videoPath.Text = defaultVideoPath;

        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DarkTheme.Panel,
            BorderColor = DarkTheme.Border,
            CornerRadius = 12,
            Padding = new Padding(16)
        };
        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = "Choose Download Destinations",
            ForeColor = DarkTheme.Text,
            Font = new Font("Segoe UI Semibold", 12f, FontStyle.Regular, GraphicsUnit.Point)
        };
        var body = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent
        };

        var lblAudio = new Label
        {
            AutoSize = false,
            Width = 130,
            Dock = DockStyle.Left,
            Text = "Audio folder",
            ForeColor = DarkTheme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        var lblVideo = new Label
        {
            AutoSize = false,
            Width = 130,
            Dock = DockStyle.Left,
            Text = "Video folder",
            ForeColor = DarkTheme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        _audioPath.Dock = DockStyle.Fill;
        _videoPath.Dock = DockStyle.Fill;
        _audioPath.Margin = new Padding(0, 6, 0, 6);
        _videoPath.Margin = new Padding(0, 6, 0, 6);
        var btnAudioBrowse = new RoundedButton { Text = "Browse", AutoSize = false, Width = 84, Height = 30 };
        var btnVideoBrowse = new RoundedButton { Text = "Browse", AutoSize = false, Width = 84, Height = 30 };
        var btnOk = new RoundedButton { Text = "Confirm", AutoSize = true, DialogResult = DialogResult.OK };
        var btnCancel = new RoundedButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        btnAudioBrowse.Click += (_, _) => BrowseForFolder(_audioPath, "Select audio destination");
        btnVideoBrowse.Click += (_, _) => BrowseForFolder(_videoPath, "Select video destination");
        _downloadAudio.CheckedChanged += (_, _) => UpdateSelectionUi(btnAudioBrowse, btnVideoBrowse);
        _downloadVideo.CheckedChanged += (_, _) => UpdateSelectionUi(btnAudioBrowse, btnVideoBrowse);
        btnOk.Click += (_, e) =>
        {
            if (!DownloadAudio && !DownloadVideo)
            {
                MessageBox.Show(this, "Select at least one type to download (audio or video).", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            if (DownloadAudio && !ValidatePath(_audioPath.Text))
            {
                MessageBox.Show(this, "Please choose a valid folder for audio.", "Invalid Audio Path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            if (DownloadVideo && !ValidatePath(_videoPath.Text))
            {
                MessageBox.Show(this, "Please choose a valid folder for video.", "Invalid Video Path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
        };

        AcceptButton = btnOk;
        CancelButton = btnCancel;

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        actions.Controls.Add(btnCancel);
        actions.Controls.Add(btnOk);

        var audioRow = BuildDestinationRow(_downloadAudio, lblAudio, _audioPath, btnAudioBrowse);
        var videoRow = BuildDestinationRow(_downloadVideo, lblVideo, _videoPath, btnVideoBrowse);
        videoRow.Dock = DockStyle.Top;
        audioRow.Dock = DockStyle.Top;
        videoRow.Margin = new Padding(0, 10, 0, 0);
        body.Controls.Add(videoRow);
        body.Controls.Add(audioRow);
        UpdateSelectionUi(btnAudioBrowse, btnVideoBrowse);

        card.Controls.Add(body);
        card.Controls.Add(actions);
        card.Controls.Add(title);
        Controls.Add(card);
    }

    private static Panel BuildDestinationRow(CheckBox selector, Label label, TextBox path, Button browse)
    {
        var row = new Panel
        {
            Height = 42,
            BackColor = Color.Transparent
        };
        var checkHost = new Panel
        {
            Dock = DockStyle.Left,
            Width = 130,
            BackColor = Color.Transparent
        };
        selector.Left = 0;
        selector.Top = 10;
        checkHost.Controls.Add(selector);

        var browseHost = new Panel
        {
            Dock = DockStyle.Right,
            Width = 110,
            BackColor = Color.Transparent
        };
        browse.Left = 10;
        browse.Top = 6;
        browseHost.Controls.Add(browse);

        row.Controls.Add(path);
        row.Controls.Add(browseHost);
        row.Controls.Add(label);
        row.Controls.Add(checkHost);
        return row;
    }

    private void UpdateSelectionUi(Button btnAudioBrowse, Button btnVideoBrowse)
    {
        _audioPath.Enabled = DownloadAudio;
        btnAudioBrowse.Enabled = DownloadAudio;
        _videoPath.Enabled = DownloadVideo;
        btnVideoBrowse.Enabled = DownloadVideo;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        BringToFront();
        if (!_centerOnOwner) SetForegroundWindow(Handle);
    }

    private static bool ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void BrowseForFolder(TextBox target, string title)
    {
        using var folderDialog = new FolderBrowserDialog
        {
            Description = title,
            UseDescriptionForTitle = true,
            SelectedPath = target.Text
        };
        if (folderDialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(folderDialog.SelectedPath))
        {
            target.Text = folderDialog.SelectedPath;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

