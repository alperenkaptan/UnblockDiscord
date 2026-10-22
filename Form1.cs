namespace UnblockDiscord;

public partial class Form1 : Form
{
    private bool isRunning;

    public Form1()
    {
        InitializeComponent();
    }

    private void StartStopButton_Click(object? sender, EventArgs e)
    {
        isRunning = !isRunning;
        startStopButton.Text = isRunning ? "Stop" : "Start";
        trayIcon.Visible = isRunning;

        if (isRunning)
        {
            Hide();
            trayIcon.ShowBalloonTip(3000, "Unblock Discord", "It's working here in the system tray.", ToolTipIcon.Info);
        }
    }

    private void TrayIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }
    }

    private void Form1_Resize(object? sender, EventArgs e)
    {
        if (isRunning && WindowState == FormWindowState.Minimized)
        {
            Hide();
        }
    }
}
