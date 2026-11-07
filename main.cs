using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace HWMonitor;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed record SensorReading(string Id, string Name, string Category, double Value, string Unit);
internal readonly record struct SensorPoint(DateTimeOffset Timestamp, double Value);
internal sealed record SensorChoice(string Id, string Name, string Category, string Unit)
{
    public override string ToString() => $"{Category} | {Name} ({Unit})";
}

internal sealed class TimeSeriesStore
{
    private sealed class Series
    {
        public required SensorChoice Choice { get; init; }
        public List<SensorPoint> Points { get; } = [];
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Series> series = new(StringComparer.Ordinal);

    public void AddSample(DateTimeOffset timestamp, IReadOnlyCollection<SensorReading> readings)
    {
        lock (gate)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reading in readings)
            {
                if (!series.TryGetValue(reading.Id, out var item))
                {
                    item = new Series
                    {
                        Choice = new SensorChoice(reading.Id, reading.Name, reading.Category, reading.Unit)
                    };
                    series.Add(reading.Id, item);
                }

                item.Points.Add(new SensorPoint(timestamp, reading.Value));
                present.Add(reading.Id);
            }

            foreach (var item in series)
            {
                if (!present.Contains(item.Key))
                    item.Value.Points.Add(new SensorPoint(timestamp, double.NaN));
            }
        }
    }

    public SensorChoice[] GetChoices()
    {
        lock (gate)
            return series.Values.Select(item => item.Choice).OrderBy(item => item.Category)
                .ThenBy(item => item.Name).ToArray();
    }

    public SensorReading[] GetLatest()
    {
        lock (gate)
        {
            return series.Values
                .Where(item => item.Points.Count > 0)
                .Select(item =>
                {
                    var choice = item.Choice;
                    return (choice, point: item.Points[^1]);
                })
                .Select(item => new SensorReading(item.choice.Id, item.choice.Name, item.choice.Category,
                    item.point.Value, item.choice.Unit))
                .ToArray();
        }
    }

    public SensorPoint[] GetPoints(string id, DateTimeOffset start, DateTimeOffset end, int maximumPoints)
    {
        lock (gate)
        {
            if (!series.TryGetValue(id, out var item))
                return [];

            var points = item.Points;
            var low = LowerBound(points, start);
            var high = LowerBound(points, end);
            var count = high - low;
            if (count <= 0)
                return [];

            var step = Math.Max(1, (int)Math.Ceiling(count / (double)Math.Max(1, maximumPoints)));
            var sampled = new List<SensorPoint>(Math.Min(count, maximumPoints + 2));
            var previousIndex = low - 1;
            for (var index = low; index < high; index += step)
            {
                var hasGap = false;
                for (var skipped = previousIndex + 1; skipped < index; skipped++)
                    hasGap |= !double.IsFinite(points[skipped].Value);

                if (hasGap && sampled.Count > 0 && double.IsFinite(sampled[^1].Value))
                    sampled.Add(new SensorPoint(points[index].Timestamp, double.NaN));

                sampled.Add(points[index]);
                previousIndex = index;
            }

            if (previousIndex < high - 1)
            {
                var hasGap = false;
                for (var skipped = previousIndex + 1; skipped < high; skipped++)
                    hasGap |= !double.IsFinite(points[skipped].Value);
                if (hasGap && sampled.Count > 0 && double.IsFinite(sampled[^1].Value))
                    sampled.Add(new SensorPoint(points[high - 1].Timestamp, double.NaN));
                if ((high - 1 - low) % step != 0)
                    sampled.Add(points[high - 1]);
            }

            return sampled.ToArray();
        }
    }

    private static int LowerBound(List<SensorPoint> points, DateTimeOffset timestamp)
    {
        var low = 0;
        var high = points.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (points[middle].Timestamp < timestamp)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }
}

internal sealed class TelemetryCollector : IDisposable
{
    private readonly Computer computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
        IsStorageEnabled = true
    };
    private ulong previousIdle;
    private ulong previousKernel;
    private ulong previousUser;
    private bool hasCpuBaseline;
    private bool hardwareAvailable;
    private readonly string? initializationError;

    public IReadOnlyCollection<SensorReading> Read()
    {
        var readings = new List<SensorReading>(64);
        var failures = new List<string>();
        if (initializationError is not null)
            failures.Add(initializationError);
        AddSystemReadings(readings, failures);
        if (hardwareAvailable)
        {
            try
            {
                foreach (var hardware in computer.Hardware)
                    ReadHardware(hardware, readings, failures);
            }
            catch (Exception exception)
            {
                failures.Add($"Hardware discovery: {exception.Message}");
            }
        }

        LastError = failures.Count == 0 ? null : string.Join("; ", failures.Distinct().Take(3));
        return readings;
    }

    public string? LastError { get; private set; }

    public TelemetryCollector()
    {
        try
        {
            computer.Open();
            hardwareAvailable = true;
        }
        catch (Exception exception)
        {
            initializationError = $"Hardware sensors unavailable: {exception.Message}";
        }
    }

    private void ReadHardware(IHardware hardware, List<SensorReading> readings, List<string> failures)
    {
        try
        {
            hardware.Update();
            var category = GetCategory(hardware.HardwareType);
            foreach (var sensor in hardware.Sensors)
            {
                try
                {
                    if (sensor.Value is not float value || !float.IsFinite(value))
                        continue;

                    var (unit, multiplier) = GetUnit(sensor.SensorType);
                    readings.Add(new SensorReading(
                        $"hardware:{sensor.Identifier}",
                        $"{hardware.Name} - {sensor.Name}",
                        category,
                        value * multiplier,
                        unit));
                }
                catch (Exception exception)
                {
                    failures.Add($"{sensor.Name}: {exception.Message}");
                }
            }

            foreach (var child in hardware.SubHardware)
                ReadHardware(child, readings, failures);
        }
        catch (Exception exception)
        {
            failures.Add($"{hardware.Name}: {exception.Message}");
        }
    }

    private void AddSystemReadings(List<SensorReading> readings, List<string> failures)
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            if (hasCpuBaseline)
            {
                var total = (kernel - previousKernel) + (user - previousUser);
                var idleDelta = idle - previousIdle;
                if (total > 0 && idleDelta <= total)
                    readings.Add(new SensorReading("system:cpu", "Total CPU utilization", "CPU",
                        Math.Clamp((total - idleDelta) * 100d / total, 0, 100), "%"));
            }

            previousIdle = idle;
            previousKernel = kernel;
            previousUser = user;
            hasCpuBaseline = true;
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            var used = memory.TotalPhysical - memory.AvailablePhysical;
            readings.Add(new SensorReading("system:ram-used", "Physical memory used", "RAM",
                used / 1024d / 1024d / 1024d, "GiB"));
            readings.Add(new SensorReading("system:ram-available", "Physical memory available", "RAM",
                memory.AvailablePhysical / 1024d / 1024d / 1024d, "GiB"));
            readings.Add(new SensorReading("system:ram-load", "Physical memory utilization", "RAM",
                memory.MemoryLoad, "%"));
            readings.Add(new SensorReading("system:ram-total", "Physical memory total", "RAM",
                memory.TotalPhysical / 1024d / 1024d / 1024d, "GiB"));
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;

                var id = $"system:drive:{drive.Name}";
                readings.Add(new SensorReading($"{id}:used", $"{drive.Name} used capacity", "Storage",
                    (drive.TotalSize - drive.AvailableFreeSpace) / 1024d / 1024d / 1024d, "GiB"));
                readings.Add(new SensorReading($"{id}:free", $"{drive.Name} free capacity", "Storage",
                    drive.AvailableFreeSpace / 1024d / 1024d / 1024d, "GiB"));
            }
            catch (Exception exception)
            {
                failures.Add($"Drive {drive.Name}: {exception.Message}");
            }
        }
    }

    private static string GetCategory(HardwareType hardwareType)
    {
        var value = hardwareType.ToString();
        if (value.Contains("Cpu", StringComparison.OrdinalIgnoreCase))
            return "CPU";
        if (value.Contains("Gpu", StringComparison.OrdinalIgnoreCase))
            return "GPU";
        if (value.Contains("Memory", StringComparison.OrdinalIgnoreCase))
            return "RAM";
        if (value.Contains("Storage", StringComparison.OrdinalIgnoreCase))
            return "Storage";
        if (value.Contains("Motherboard", StringComparison.OrdinalIgnoreCase))
            return "Motherboard";
        return "Cooling & other";
    }

    private static (string Unit, double Multiplier) GetUnit(SensorType type) => type switch
    {
        SensorType.Temperature => ("°C", 1),
        SensorType.Clock => ("MHz", 1),
        SensorType.Load or SensorType.Control => ("%", 1),
        SensorType.Power => ("W", 1),
        SensorType.Fan => ("RPM", 1),
        SensorType.Voltage => ("V", 1),
        SensorType.Data => ("GB", 1),
        SensorType.SmallData => ("MB", 1),
        SensorType.Throughput => ("B/s", 1),
        SensorType.Energy => ("mWh", 1),
        _ => (type.ToString(), 1)
    };

    public void Dispose()
    {
        if (hardwareAvailable)
            computer.Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);
}

internal sealed class MainForm : Form
{
    private readonly TimeSeriesStore store = new();
    private readonly TelemetryCollector collector;
    private readonly DateTimeOffset sessionStart = DateTimeOffset.Now;
    private readonly Stopwatch sessionClockWatch = Stopwatch.StartNew();
    private readonly System.Threading.Timer samplingTimer;
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 500 };
    private readonly TextBox frequency = new()
    {
        Text = "10",
        Width = 70
    };
    private readonly Label status = new() { AutoSize = true, ForeColor = Color.Gainsboro };
    private readonly Label sessionClock = new() { AutoSize = true, ForeColor = Color.Gainsboro };
    private readonly FlowLayoutPanel dashboard = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        Padding = new Padding(12),
        WrapContents = true
    };
    private readonly Dictionary<string, DashboardCard> cards = new(StringComparer.Ordinal);
    private readonly ComboBox sensorPicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 500 };
    private readonly TextBox rangeSeconds = new() { Text = "60", Width = 90 };
    private readonly HScrollBar historyScrollBar = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1, LargeChange = 1 };
    private readonly SensorGraph graph;
    private double samplingFrequency = 10;
    private double rangeDurationSeconds = 60;
    private DateTimeOffset visibleStart;
    private DateTimeOffset visibleEnd;
    private int sampleInProgress;
    private bool updatingScrollBar;
    private bool followLatest = true;
    private const double ScrollbarUnitsPerSecond = 10;
    private const int MaximumScrollbarValue = int.MaxValue - 1;

    public MainForm()
    {
        Text = "HWMonitor";
        MinimumSize = new Size(900, 650);
        Size = new Size(1280, 820);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(21, 25, 32);
        ForeColor = Color.WhiteSmoke;

        try
        {
            collector = new TelemetryCollector();
            status.Text = "Monitoring available sensors";
        }
        catch (Exception exception)
        {
            collector = null!;
            status.Text = $"Hardware sensor initialization failed: {exception.Message}";
            status.ForeColor = Color.Orange;
        }

        graph = new SensorGraph(store, () => SelectedSensorId, sessionStart)
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(17, 21, 27)
        };
        graph.RangeChanged += OnGraphRangeChanged;
        BuildInterface();
        visibleEnd = sessionStart.AddSeconds(rangeDurationSeconds);
        visibleStart = sessionStart;
        graph.SetRange(visibleStart, visibleEnd);

        samplingTimer = new System.Threading.Timer(Sample, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        frequency.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                CommitSamplingFrequency();
            }
        };
        frequency.Leave += (_, _) => CommitSamplingFrequency();
        rangeSeconds.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                CommitRangeDuration();
            }
        };
        rangeSeconds.Leave += (_, _) => CommitRangeDuration();
        historyScrollBar.ValueChanged += (_, _) => OnHistoryScroll();
        refreshTimer.Tick += (_, _) => RefreshInterface();
        refreshTimer.Start();
        FormClosing += (_, _) =>
        {
            refreshTimer.Stop();
            using var samplingStopped = new ManualResetEvent(false);
            if (samplingTimer.Dispose(samplingStopped))
                samplingStopped.WaitOne();
            collector?.Dispose();
        };
    }

    private string? SelectedSensorId => (sensorPicker.SelectedItem as SensorChoice)?.Id;

    private void BuildInterface()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(12, 10, 12, 8),
            BackColor = Color.FromArgb(31, 37, 47)
        };
        toolbar.Controls.Add(new Label { Text = "Sampling rate (Hz)", AutoSize = true, Margin = new Padding(0, 7, 7, 0) });
        toolbar.Controls.Add(frequency);
        toolbar.Controls.Add(status);
        status.Margin = new Padding(14, 7, 0, 0);
        toolbar.Controls.Add(sessionClock);
        sessionClock.Margin = new Padding(18, 7, 0, 0);
        root.Controls.Add(toolbar, 0, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var overviewTab = new TabPage("Overview") { BackColor = BackColor, ForeColor = ForeColor };
        var graphTab = new TabPage("Graphs") { BackColor = BackColor, ForeColor = ForeColor };
        tabs.TabPages.Add(overviewTab);
        tabs.TabPages.Add(graphTab);
        root.Controls.Add(tabs, 0, 1);

        dashboard.Controls.Add(new DashboardCard("CPU"));
        dashboard.Controls.Add(new DashboardCard("RAM"));
        dashboard.Controls.Add(new DashboardCard("GPU"));
        dashboard.Controls.Add(new DashboardCard("Storage"));
        dashboard.Controls.Add(new DashboardCard("Motherboard"));
        dashboard.Controls.Add(new DashboardCard("Cooling & other"));
        foreach (DashboardCard card in dashboard.Controls)
            cards.Add(card.Category, card);
        overviewTab.Controls.Add(dashboard);

        var graphLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = BackColor
        };
        graphLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        graphLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        graphLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        graphTab.Controls.Add(graphLayout);

        var controls = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(12),
            BackColor = Color.FromArgb(31, 37, 47)
        };
        controls.Controls.Add(new Label { Text = "Statistic", AutoSize = true, Margin = new Padding(0, 7, 5, 0) });
        controls.Controls.Add(sensorPicker);
        controls.Controls.Add(new Label { Text = "Range (seconds)", AutoSize = true, Margin = new Padding(12, 7, 5, 0) });
        controls.Controls.Add(rangeSeconds);
        graphLayout.Controls.Add(controls, 0, 0);
        graphLayout.Controls.Add(graph, 0, 1);
        graphLayout.Controls.Add(historyScrollBar, 0, 2);
        sensorPicker.SelectedIndexChanged += (_, _) => graph.Invalidate();
    }

    private void Sample(object? state)
    {
        if (Interlocked.Exchange(ref sampleInProgress, 1) != 0)
            return;

        IReadOnlyCollection<SensorReading> readings = [];
        if (collector is not null)
        {
            try
            {
                readings = collector.Read();
            }
            catch (Exception exception)
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(() =>
                    {
                        status.Text = $"Sampling error: {exception.Message}";
                        status.ForeColor = Color.Orange;
                    }));
                }
            }
        }

        try
        {
            store.AddSample(sessionStart + sessionClockWatch.Elapsed, readings);
        }
        catch (Exception exception)
        {
            if (!IsDisposed && IsHandleCreated)
                BeginInvoke(new Action(() =>
                {
                    status.Text = $"Unable to store sample: {exception.Message}";
                    status.ForeColor = Color.Orange;
                }));
        }
        finally
        {
            Volatile.Write(ref sampleInProgress, 0);
            if (!IsDisposed)
            {
                try
                {
                    samplingTimer.Change(FrequencyInterval(), Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private void RefreshInterface()
    {
        var latest = store.GetLatest();
        foreach (var card in cards.Values)
        {
            var lines = latest.Where(item => item.Category == card.Category && double.IsFinite(item.Value))
                .Take(7).Select(item => $"{item.Name}: {FormatValue(item.Value)} {item.Unit}");
            card.SetReadings(lines);
        }

        var choices = store.GetChoices();
        var selectedId = SelectedSensorId;
        if (sensorPicker.Items.Count != choices.Length ||
            !sensorPicker.Items.Cast<SensorChoice>().Select(item => item.Id).SequenceEqual(choices.Select(item => item.Id)))
        {
            sensorPicker.BeginUpdate();
            sensorPicker.Items.Clear();
            sensorPicker.Items.AddRange(choices);
            var selectedIndex = Array.FindIndex(choices, item => item.Id == selectedId);
            if (selectedIndex < 0 && choices.Length > 0)
                selectedIndex = 0;
            if (selectedIndex >= 0)
                sensorPicker.SelectedIndex = selectedIndex;
            sensorPicker.EndUpdate();
        }

        sessionClock.Text = $"Session: {sessionClockWatch.Elapsed:hh\\:mm\\:ss}";
        if (collector?.LastError is { Length: > 0 } error)
        {
            status.Text = $"Some sensor reads failed: {error}";
            status.ForeColor = Color.Orange;
        }
        else
        {
            status.Text = collector is null
                ? "System statistics only; hardware sensors unavailable"
                : "Monitoring available sensors";
            status.ForeColor = Color.Gainsboro;
        }

        if (followLatest)
        {
            visibleEnd = sessionStart + sessionClockWatch.Elapsed;
            visibleStart = visibleEnd - TimeSpan.FromSeconds(rangeDurationSeconds);
            if (visibleStart < sessionStart)
                visibleStart = sessionStart;
            graph.SetRange(visibleStart, visibleEnd);
        }
        UpdateHistoryScrollBar();
        graph.Invalidate();
    }

    private void CommitSamplingFrequency()
    {
        if (!TryParseInput(frequency.Text, out var value) || value < 0.2 || value > 10)
        {
            frequency.Text = samplingFrequency.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
            status.Text = "Sampling rate must be between 0.2 and 10 Hz";
            status.ForeColor = Color.Orange;
            return;
        }

        samplingFrequency = value;
        frequency.Text = value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        samplingTimer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void CommitRangeDuration()
    {
        if (!TryParseInput(rangeSeconds.Text, out var value) || value < 0.1 || value > MaximumScrollbarValue / ScrollbarUnitsPerSecond)
        {
            rangeSeconds.Text = rangeDurationSeconds.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
            status.Text = "Graph range must be at least 0.1 seconds";
            status.ForeColor = Color.Orange;
            return;
        }

        var follow = followLatest;
        rangeDurationSeconds = value;
        rangeSeconds.Text = value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        if (follow)
        {
            visibleEnd = sessionStart + sessionClockWatch.Elapsed;
            visibleStart = visibleEnd - TimeSpan.FromSeconds(rangeDurationSeconds);
            if (visibleStart < sessionStart)
                visibleStart = sessionStart;
        }
        else
        {
            visibleEnd = visibleStart + TimeSpan.FromSeconds(rangeDurationSeconds);
        }

        graph.SetRange(visibleStart, visibleEnd);
        UpdateHistoryScrollBar();
    }

    private TimeSpan FrequencyInterval() => TimeSpan.FromSeconds(1d / samplingFrequency);

    private static bool TryParseInput(string text, out double value)
    {
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.CurrentCulture, out value) && double.IsFinite(value)
               || double.TryParse(text, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private bool IsFollowingLatest()
    {
        return historyScrollBar.Value >= historyScrollBar.Maximum - historyScrollBar.LargeChange + 1;
    }

    private void UpdateHistoryScrollBar()
    {
        var elapsedSeconds = Math.Max(0, (sessionStart + sessionClockWatch.Elapsed - sessionStart).TotalSeconds);
        var totalUnits = (int)Math.Clamp(Math.Ceiling(elapsedSeconds * ScrollbarUnitsPerSecond), 0, MaximumScrollbarValue);
        var rangeUnits = (int)Math.Clamp(Math.Ceiling(rangeDurationSeconds * ScrollbarUnitsPerSecond), 1, MaximumScrollbarValue);
        var largeChange = Math.Min(rangeUnits + 1, totalUnits + 1);
        var effectiveMaximum = Math.Max(0, totalUnits - largeChange + 1);
        updatingScrollBar = true;
        historyScrollBar.Minimum = 0;
        historyScrollBar.LargeChange = Math.Max(1, largeChange);
        historyScrollBar.SmallChange = 1;
        historyScrollBar.Maximum = Math.Max(0, totalUnits);
        historyScrollBar.Value = Math.Clamp(
            (int)Math.Clamp(Math.Round((visibleStart - sessionStart).TotalSeconds * ScrollbarUnitsPerSecond),
                0, MaximumScrollbarValue),
            0, effectiveMaximum);
        updatingScrollBar = false;
    }

    private void OnHistoryScroll()
    {
        if (updatingScrollBar)
            return;

        followLatest = IsFollowingLatest();
        visibleStart = sessionStart + TimeSpan.FromSeconds(historyScrollBar.Value / ScrollbarUnitsPerSecond);
        visibleEnd = visibleStart + TimeSpan.FromSeconds(rangeDurationSeconds);
        graph.SetRange(visibleStart, visibleEnd);
    }

    private void OnGraphRangeChanged(object? sender, EventArgs e)
    {
        visibleStart = graph.RangeStart;
        visibleEnd = graph.RangeEnd;
        UpdateHistoryScrollBar();
    }

    private static string FormatValue(double value) => value.ToString("0.##");
}

internal sealed class DashboardCard : Panel
{
    private readonly Label readings = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        ForeColor = Color.Gainsboro,
        Padding = new Padding(12, 8, 10, 10)
    };

    public DashboardCard(string category)
    {
        Category = category;
        Width = 390;
        Height = 220;
        Margin = new Padding(10);
        BackColor = Color.FromArgb(31, 37, 47);
        var heading = new Label
        {
            Text = category,
            Dock = DockStyle.Top,
            Height = 42,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            ForeColor = Color.White,
            Padding = new Padding(12, 12, 8, 5)
        };
        Controls.Add(readings);
        Controls.Add(heading);
        SetReadings([]);
    }

    public string Category { get; }

    public void SetReadings(IEnumerable<string> lines)
    {
        var content = lines.ToArray();
        readings.Text = content.Length == 0 ? "No readings available" : string.Join(Environment.NewLine, content);
    }
}

internal sealed class SensorGraph : Control
{
    private readonly TimeSeriesStore store;
    private readonly Func<string?> selectedSensor;
    private readonly DateTimeOffset sessionStart;

    public SensorGraph(TimeSeriesStore store, Func<string?> selectedSensor, DateTimeOffset sessionStart)
    {
        this.store = store;
        this.selectedSensor = selectedSensor;
        this.sessionStart = sessionStart;
        DoubleBuffered = true;
        RangeStart = sessionStart;
        RangeEnd = sessionStart.AddSeconds(60);
        ForeColor = Color.Gainsboro;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public DateTimeOffset RangeStart { get; private set; }
    public DateTimeOffset RangeEnd { get; private set; }
    public event EventHandler? RangeChanged;

    public void SetRange(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
            return;
        RangeStart = start;
        RangeEnd = end;
        Invalidate();
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(BackColor);
        var plot = new Rectangle(76, 24, Math.Max(1, Width - 104), Math.Max(1, Height - 76));
        DrawGrid(e.Graphics, plot);

        var id = selectedSensor();
        if (id is null)
        {
            DrawCentered(e.Graphics, "Waiting for available sensors...", plot);
            return;
        }

        var points = store.GetPoints(id, RangeStart, RangeEnd, Math.Max(2, plot.Width * 2));
        var valid = points.Where(point => double.IsFinite(point.Value)).ToArray();
        if (valid.Length == 0)
        {
            DrawCentered(e.Graphics, "No readings in this time interval", plot);
            DrawTimeAxis(e.Graphics, plot);
            return;
        }

        var minimum = valid.Min(point => point.Value);
        var maximum = valid.Max(point => point.Value);
        if (maximum == minimum)
        {
            var padding = Math.Max(Math.Abs(maximum) * 0.05, 1);
            minimum -= padding;
            maximum += padding;
        }
        else
        {
            var padding = (maximum - minimum) * 0.08;
            minimum -= padding;
            maximum += padding;
        }

        for (var i = 0; i <= 4; i++)
        {
            var value = maximum - (maximum - minimum) * i / 4;
            var y = plot.Top + plot.Height * i / 4;
            TextRenderer.DrawText(e.Graphics, value.ToString("0.##"), Font, new Rectangle(4, (int)y - 8, 65, 18),
                Color.Silver, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        var choice = store.GetChoices().FirstOrDefault(item => item.Id == id);
        if (choice is not null)
            TextRenderer.DrawText(e.Graphics, $"{choice.Name} ({choice.Unit})", Font,
                new Point(plot.Left, 4), Color.WhiteSmoke);

        using var pen = new Pen(Color.FromArgb(74, 178, 255), 2f);
        var step = Math.Max(1, points.Length / Math.Max(1, plot.Width * 2));
        var hasPrevious = false;
        var previous = PointF.Empty;
        var previousIndex = -1;
        for (var i = 0; i < points.Length; i += step)
        {
            var point = points[i];
            var gap = !double.IsFinite(point.Value);
            for (var skipped = previousIndex + 1; !gap && skipped < i; skipped++)
                gap = !double.IsFinite(points[skipped].Value);
            if (gap)
            {
                hasPrevious = false;
                previousIndex = i;
                continue;
            }

            var x = plot.Left + (float)((point.Timestamp - RangeStart).Ticks /
                (double)(RangeEnd - RangeStart).Ticks * plot.Width);
            var y = plot.Bottom - (float)((point.Value - minimum) / (maximum - minimum) * plot.Height);
            var current = new PointF(x, y);
            if (hasPrevious)
                e.Graphics.DrawLine(pen, previous, current);
            previous = current;
            hasPrevious = true;
            previousIndex = i;
        }

        DrawTimeAxis(e.Graphics, plot);
    }

    private void DrawGrid(Graphics graphics, Rectangle plot)
    {
        using var gridPen = new Pen(Color.FromArgb(54, 63, 76), 1);
        using var axisPen = new Pen(Color.FromArgb(114, 126, 142), 1);
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + plot.Height * i / 4;
            graphics.DrawLine(i == 4 ? axisPen : gridPen, plot.Left, y, plot.Right, y);
        }

        graphics.DrawLine(axisPen, plot.Left, plot.Top, plot.Left, plot.Bottom);
    }

    private void DrawTimeAxis(Graphics graphics, Rectangle plot)
    {
        for (var i = 0; i <= 4; i++)
        {
            var x = plot.Left + plot.Width * i / 4;
            var elapsed = (RangeStart - sessionStart).TotalSeconds +
                (RangeEnd - RangeStart).TotalSeconds * i / 4;
            TextRenderer.DrawText(graphics, $"{elapsed:0.##} s", Font,
                new Rectangle(x - 40, plot.Bottom + 8, 80, 20), Color.Silver,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        }
    }

    private static void DrawCentered(Graphics graphics, string text, Rectangle bounds) =>
        TextRenderer.DrawText(graphics, text, SystemFonts.DefaultFont, bounds, Color.Silver,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
}