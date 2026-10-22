namespace UnblockDiscord;

partial class Form1
{
    private System.ComponentModel.IContainer components = null;
    private Button startStopButton;
    private NotifyIcon trayIcon;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        startStopButton = new Button();
        trayIcon = new NotifyIcon(components);
        SuspendLayout();
        //
        // startStopButton
        //
        startStopButton.Location = new Point(40, 60);
        startStopButton.Name = "startStopButton";
        startStopButton.Size = new Size(120, 80);
        startStopButton.TabIndex = 0;
        startStopButton.Text = "Start";
        startStopButton.UseVisualStyleBackColor = true;
        startStopButton.Click += StartStopButton_Click;
        //
        // trayIcon
        //
        trayIcon.Icon = SystemIcons.Application;
        trayIcon.Text = "Unblock Discord is running";
        trayIcon.MouseClick += TrayIcon_MouseClick;
        //
        // Form1
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(200, 200);
        Controls.Add(startStopButton);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Name = "Form1";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Unblock Discord";
        Resize += Form1_Resize;
        ResumeLayout(false);
    }

    #endregion
}
