using System.Drawing.Drawing2D;
using System.Globalization;

namespace TrafficMonitor;

internal sealed class ReportsForm : Form
{
    private readonly TrafficCollector _collector;
    private readonly Label _cycleComparison = new();
    private readonly Label _topApplications = new();
    private readonly TrafficChartPanel _chart = new();
    private readonly DateTimePicker _startDate = new();
    private readonly DateTimePicker _endDate = new();

    public ReportsForm(TrafficCollector collector)
    {
        _collector = collector;
        Text = "Monita - Usage reports";
        Width = 900;
        Height = 620;
        MinimumSize = new Size(700, 450);
        StartPosition = FormStartPosition.CenterParent;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));

        var cycle = _collector.GetCurrentCycle();
        var priorTotals = _collector.GetTotals(cycle.Start.AddMonths(-1), cycle.Start);
        var currentTotals = _collector.GetTotals(cycle.Start, cycle.End);
        var difference = priorTotals.TotalBytes == 0
            ? 0
            : (currentTotals.TotalBytes - priorTotals.TotalBytes) / (double)priorTotals.TotalBytes;
        _cycleComparison.Dock = DockStyle.Fill;
        _cycleComparison.TextAlign = ContentAlignment.MiddleLeft;
        _cycleComparison.Text =
            $"Current cycle ({cycle.Start:MMM d}–{cycle.End.AddDays(-1):MMM d}): {FormatBytes(currentTotals.TotalBytes)}    " +
            $"Previous: {FormatBytes(priorTotals.TotalBytes)}    Change: {difference:+0%;-0%;0%}";
        layout.Controls.Add(_cycleComparison, 0, 0);

        _topApplications.Dock = DockStyle.Fill;
        _topApplications.TextAlign = ContentAlignment.MiddleLeft;
        _topApplications.Text = "Top apps this cycle: " + string.Join("  |  ",
            currentTotals.AppTotals.OrderByDescending(pair => pair.Value).Take(3)
                .Select(pair => $"{Path.GetFileName(pair.Key)} {FormatBytes(pair.Value)}"));
        layout.Controls.Add(_topApplications, 0, 1);

        _chart.Dock = DockStyle.Fill;
        layout.Controls.Add(_chart, 0, 2);

        var exportPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0)
        };
        _startDate.Format = DateTimePickerFormat.Short;
        _startDate.Value = DateTime.Today.AddDays(-29);
        _endDate.Format = DateTimePickerFormat.Short;
        _endDate.Value = DateTime.Today;
        exportPanel.Controls.Add(new Label { Text = "Export from:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        exportPanel.Controls.Add(_startDate);
        exportPanel.Controls.Add(new Label { Text = "through:", AutoSize = true, Margin = new Padding(12, 6, 6, 0) });
        exportPanel.Controls.Add(_endDate);
        var export = new Button { Text = "Export CSV...", AutoSize = true, Margin = new Padding(12, 0, 0, 0) };
        export.Click += ExportCsv;
        exportPanel.Controls.Add(export);
        layout.Controls.Add(exportPanel, 0, 3);

        Controls.Add(layout);

        var end = DateOnly.FromDateTime(DateTime.Today).AddDays(1);
        _chart.Data = _collector.GetDailyUsage(end.AddDays(-30), end);
    }

    private void ExportCsv(object? sender, EventArgs e)
    {
        var start = DateOnly.FromDateTime(_startDate.Value.Date);
        var end = DateOnly.FromDateTime(_endDate.Value.Date).AddDays(1);
        if (end <= start)
        {
            MessageBox.Show(this, "The end date must be on or after the start date.",
                "Invalid date range", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "csv",
            FileName = $"monita-usage-{start:yyyyMMdd}-{end.AddDays(-1):yyyyMMdd}.csv",
            Filter = "CSV files (*.csv)|*.csv"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            _collector.ExportCsv(start, end, dialog.FileName);
            MessageBox.Show(this, "Usage report exported.", "Export complete",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Could not export report",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private sealed class TrafficChartPanel : Panel
    {
        private IReadOnlyList<DailyUsage> _data = Array.Empty<DailyUsage>();

        public IReadOnlyList<DailyUsage> Data
        {
            get => _data;
            set
            {
                _data = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(BackColor);
            if (_data.Count == 0)
            {
                return;
            }

            var maximum = Math.Max(1, _data.Max(item => item.TotalBytes));
            const int marginLeft = 52;
            const int marginBottom = 28;
            var chartHeight = Math.Max(1, Height - marginBottom - 12);
            var chartWidth = Math.Max(1, Width - marginLeft - 12);
            var barWidth = Math.Max(2, chartWidth / (float)_data.Count * 0.62f);
            using var sentBrush = new SolidBrush(Color.FromArgb(21, 153, 181));
            using var receivedBrush = new SolidBrush(Color.FromArgb(44, 197, 153));
            using var axisPen = new Pen(SystemColors.ControlDark);
            using var textBrush = new SolidBrush(ForeColor);
            e.Graphics.DrawLine(axisPen, marginLeft, 8, marginLeft, chartHeight);
            e.Graphics.DrawLine(axisPen, marginLeft, chartHeight, Width - 8, chartHeight);

            for (var index = 0; index < _data.Count; index++)
            {
                var day = _data[index];
                var x = marginLeft + index * (chartWidth / (float)_data.Count)
                    + (chartWidth / (float)_data.Count - barWidth) / 2;
                var sentHeight = (float)(day.SentBytes / (double)maximum * (chartHeight - 8));
                var receivedHeight = (float)(day.ReceivedBytes / (double)maximum * (chartHeight - 8));
                e.Graphics.FillRectangle(sentBrush, x, chartHeight - sentHeight, barWidth, sentHeight);
                e.Graphics.FillRectangle(receivedBrush, x, chartHeight - sentHeight - receivedHeight,
                    barWidth, receivedHeight);
                if (index % 5 == 0 || index == _data.Count - 1)
                {
                    e.Graphics.DrawString(day.Date.ToString("MM/dd", CultureInfo.InvariantCulture),
                        Font, textBrush, x - 8, chartHeight + 4);
                }
            }

            e.Graphics.DrawString("Sent", Font, sentBrush, marginLeft + 8, 4);
            e.Graphics.DrawString("Received", Font, receivedBrush, marginLeft + 52, 4);
        }
    }
}
