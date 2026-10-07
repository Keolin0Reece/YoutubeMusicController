namespace YoutubeMusicController.Tray;

internal sealed class SettingsForm : Form
{
    private readonly TextBox _twitchClientId = new();
    private readonly CheckBox _discordEnabled = new();
    private readonly TextBox _discordApplicationId = new();
    private readonly TextBox _discordGuildId = new();
    private readonly TextBox _discordBotToken = new();
    private readonly TextBox _videoRequesterUsername = new();

    public SettingsForm(ControllerUserSettings settings)
    {
        Text = "YouTube Music Controller Settings";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(620, 430);

        _twitchClientId.Text = settings.Twitch.ClientId;
        _discordEnabled.Checked = settings.Discord.Enabled;
        _videoRequesterUsername.Text =
            settings.Discord.Commands.VideoRequesterUsername;
        _discordBotToken.UseSystemPasswordChar = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = 9
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(layout, 0, "Twitch Client ID", _twitchClientId);
        AddDescription(
            layout,
            1,
            "YouTube Music Desktop must be running with its remote API on port 26538. Use a Twitch application configured as a public client; no Twitch Client Secret is stored.");
        AddRow(layout, 2, "Enable Discord", _discordEnabled);
        AddRow(layout, 3, "Discord Application ID", _discordApplicationId);
        AddRow(layout, 4, "Discord Guild ID (optional)", _discordGuildId);
        AddRow(layout, 5, "Discord Bot Token", _discordBotToken);
        AddRow(layout, 6, "Video requester username", _videoRequesterUsername);
        AddDescription(
            layout,
            7,
            "The bot token is encrypted for your Windows account and is never written to settings.json.");

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true
        };
        var saveButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            AutoSize = true
        };
        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true
        };
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        layout.Controls.Add(buttons, 0, 8);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    public string TwitchClientId => _twitchClientId.Text.Trim();

    public bool DiscordEnabled => _discordEnabled.Checked;

    public string VideoRequesterUsername =>
        _videoRequesterUsername.Text.Trim();

    public bool HasDiscordCredentials =>
        !string.IsNullOrWhiteSpace(_discordApplicationId.Text) ||
        !string.IsNullOrWhiteSpace(_discordBotToken.Text);

    public bool TryGetDiscordCredentials(
        out ulong applicationId,
        out ulong? guildId,
        out string botToken)
    {
        applicationId = 0;
        guildId = null;
        botToken = _discordBotToken.Text.Trim();

        if (!HasDiscordCredentials)
        {
            return true;
        }

        if (!ulong.TryParse(_discordApplicationId.Text.Trim(), out applicationId) ||
            applicationId == 0 ||
            botToken.Length < 58)
        {
            MessageBox.Show(
                "Enter a valid Discord Application ID and Bot Token.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        var guildText = _discordGuildId.Text.Trim();

        if (guildText.Length > 0)
        {
            if (!ulong.TryParse(guildText, out var parsedGuildId) ||
                parsedGuildId == 0)
            {
                MessageBox.Show(
                    "The Discord Guild ID must be numeric.",
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }

            guildId = parsedGuildId;
        }

        return true;
    }

    private static void AddRow(
        TableLayoutPanel layout,
        int row,
        string label,
        Control control)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var labelControl = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 7, 3, 7);
        layout.Controls.Add(labelControl, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private static void AddDescription(
        TableLayoutPanel layout,
        int row,
        string text)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        var label = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true
        };
        layout.Controls.Add(label, 0, row);
        layout.SetColumnSpan(label, 2);
    }
}
