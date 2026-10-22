namespace UnblockDiscord;

public partial class Form1 : Form
{
    private readonly BypassController bypass = new();
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    private bool closing;

    public Form1()
    {
        InitializeComponent();
        bypass.EngineExited += () =>
        {
            if (!closing && IsHandleCreated)
                BeginInvoke(() =>
                {
                    if (busy || closing || !trayIcon.Visible) return;
                    bypass.Dispose();
                    UpdateState();
                    ShowWindow();
                    MessageBox.Show(this, "The bypass stopped. Your connection is back to normal.",
                        "Unblock Discord", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
        };
    }

    private async void StartStopButton_Click(object? sender, EventArgs e)
    {
        if (busy) return;
        busy = true;
        startStopButton.Enabled = false;
        try
        {
            if (bypass.IsRunning) bypass.Dispose();
            else
            {
                await bypass.StartAsync(message => startStopButton.Text = message, lifetime.Token);
                Hide();
                trayIcon.Visible = true;
                trayIcon.ShowBalloonTip(4000, "Unblock Discord",
                    "Working here. Open Discord (or fully quit and reopen it if already running).", ToolTipIcon.Info);
            }
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex)
        {
            if (!closing) MessageBox.Show(this, ex.Message, "Could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            busy = false;
            if (!closing) UpdateState();
        }
    }

    private void UpdateState()
    {
        startStopButton.Text = bypass.IsRunning ? "Stop" : "Start";
        startStopButton.Enabled = true;
        trayIcon.Visible = bypass.IsRunning;
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void TrayIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) ShowWindow();
    }

    private void Form1_Resize(object? sender, EventArgs e)
    {
        if (bypass.IsRunning && WindowState == FormWindowState.Minimized) Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        closing = true;
        lifetime.Cancel();
        bypass.Dispose();
        trayIcon.Visible = false;
        base.OnFormClosing(e);
    }
}
