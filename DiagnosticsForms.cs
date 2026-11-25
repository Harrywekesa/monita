namespace TrafficMonitor;

internal sealed class ConnectionsForm : Form
{
    public ConnectionsForm(UsageRecord record)
    {
        Text = $"Connections - {record.Application} (PID {record.ProcessId})";
        Width = 850;
        Height = 420;
        StartPosition = FormStartPosition.CenterParent;

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            DataSource = record.Connections.OrderByDescending(item => item.LastSeenUtc).ToArray()
        };
        AddColumn(grid, nameof(ConnectionUsage.Protocol), "Protocol", 45);
        AddColumn(grid, nameof(ConnectionUsage.LocalAddress), "Local address", 100);
        AddColumn(grid, nameof(ConnectionUsage.LocalPort), "Local port", 45);
        AddColumn(grid, nameof(ConnectionUsage.RemoteAddress), "Remote address", 100);
        AddColumn(grid, nameof(ConnectionUsage.RemotePort), "Remote port", 45);
        AddColumn(grid, nameof(ConnectionUsage.SentBytes), "Sent", 70);
        AddColumn(grid, nameof(ConnectionUsage.ReceivedBytes), "Received", 70);
        AddColumn(grid, nameof(ConnectionUsage.LastSeenUtc), "Last seen (UTC)", 100);

        var notice = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            Padding = new Padding(8, 6, 8, 0),
            Text = "Endpoints are observed from network events and may no longer be active."
        };
        Controls.Add(grid);
        Controls.Add(notice);
    }

    private static void AddColumn(DataGridView grid, string property, string title, float weight)
    {
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = property,
            HeaderText = title,
            FillWeight = weight,
            DefaultCellStyle = property.EndsWith("Bytes", StringComparison.Ordinal)
                ? new DataGridViewCellStyle { Format = "N0" }
                : null
        });
    }
}

internal sealed class DiagnosticsForm : Form
{
    public DiagnosticsForm(TrafficCollector collector, DateOnly date)
    {
        Text = "Monita - System diagnostics";
        Width = 900;
        Height = 580;
        StartPosition = FormStartPosition.CenterParent;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "System PID / unresolved traffic for " + date.ToString("yyyy-MM-dd")
        }, 0, 0);

        var systemGrid = CreateGrid();
        AddUsageColumn(systemGrid, nameof(UsageRecord.Application), "Process");
        AddUsageColumn(systemGrid, nameof(UsageRecord.Adapter), "Adapter");
        AddUsageColumn(systemGrid, nameof(UsageRecord.SentBytes), "Sent bytes");
        AddUsageColumn(systemGrid, nameof(UsageRecord.ReceivedBytes), "Received bytes");
        AddUsageColumn(systemGrid, nameof(UsageRecord.ProcessId), "PID");
        systemGrid.DataSource = collector.GetSystemDiagnostics(date);
        layout.Controls.Add(systemGrid, 0, 1);

        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "OS interface counters vs Monita ETW totals since this run started"
        }, 0, 2);
        var adapters = CreateGrid();
        AddAdapterColumn(adapters, nameof(AdapterDiagnostics.Name), "Adapter");
        AddAdapterColumn(adapters, nameof(AdapterDiagnostics.OsSentBytes), "OS sent");
        AddAdapterColumn(adapters, nameof(AdapterDiagnostics.OsReceivedBytes), "OS received");
        AddAdapterColumn(adapters, nameof(AdapterDiagnostics.EtwSentBytes), "ETW sent");
        AddAdapterColumn(adapters, nameof(AdapterDiagnostics.EtwReceivedBytes), "ETW received");
        adapters.DataSource = collector.GetAdapterDiagnostics();
        layout.Controls.Add(adapters, 0, 3);

        var explanation = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            Padding = new Padding(8, 5, 8, 0),
            Text = "System traffic may be kernel traffic and cannot always be attributed to an app or service. OS and ETW counters differ in scope and overhead."
        };
        Controls.Add(layout);
        Controls.Add(explanation);
    }

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoGenerateColumns = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false
    };

    private static void AddUsageColumn(DataGridView grid, string property, string title)
    {
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = property,
            HeaderText = title,
            DefaultCellStyle = property.EndsWith("Bytes", StringComparison.Ordinal)
                ? new DataGridViewCellStyle { Format = "N0" }
                : null
        });
    }

    private static void AddAdapterColumn(DataGridView grid, string property, string title)
    {
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = property,
            HeaderText = title,
            DefaultCellStyle = property.Contains("Bytes", StringComparison.Ordinal)
                ? new DataGridViewCellStyle { Format = "N0" }
                : null
        });
    }
}
