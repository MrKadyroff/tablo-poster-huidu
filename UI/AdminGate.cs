namespace LedImageUpdaterService.UI;

/// <summary>
/// Small modal password prompt that gates the full settings window.
/// The expected password is read from <see cref="AppConfig.AdminPassword"/>.
/// </summary>
internal static class AdminGate
{
    /// <summary>
    /// Prompts for the admin password. Returns true if it matches the configured
    /// one. An empty configured password means no protection (always allowed).
    /// </summary>
    public static bool Authenticate(IWin32Window? owner)
    {
        var expected = AppSettingsManager.Load().AdminPassword ?? "";
        if (string.IsNullOrEmpty(expected)) return true;

        using var dlg = new PasswordPrompt();
        if (dlg.ShowDialog(owner) != DialogResult.OK)
            return false;

        if (string.Equals(dlg.Value, expected, StringComparison.Ordinal))
            return true;

        MessageBox.Show("Неверный пароль.", "eCash Tablo",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private sealed class PasswordPrompt : Form
    {
        private readonly TextBox _txt;

        public string Value => _txt.Text;

        public PasswordPrompt()
        {
            Text = "Доступ администратора";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(320, 130);
            BackColor = UITheme.Bg;
            Font = new Font("Segoe UI", 9f);
            Icon = TrayApplicationContext.CreateAppIcon();

            var lbl = new Label
            {
                Text = "Введите пароль администратора:",
                ForeColor = UITheme.Text,
                AutoSize = true,
                Location = new Point(14, 16),
            };

            _txt = new TextBox
            {
                Location = new Point(16, 42),
                Width = 288,
                PasswordChar = '●',
                BackColor = UITheme.Input,
                ForeColor = UITheme.Text,
                BorderStyle = BorderStyle.FixedSingle,
            };
            _txt.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };

            var btnOk = new RoundedButton
            {
                Text = "OK",
                Location = new Point(132, 84),
                Width = 80,
                Height = 30,
                BackColor = UITheme.Accent2,
                ForeColor = Color.White,
                CornerRadius = 8,
                DialogResult = DialogResult.OK,
            };
            var btnCancel = new RoundedButton
            {
                Text = "Отмена",
                Location = new Point(220, 84),
                Width = 84,
                Height = 30,
                BackColor = UITheme.Input,
                ForeColor = UITheme.Text,
                CornerRadius = 8,
                DialogResult = DialogResult.Cancel,
            };

            Controls.AddRange([lbl, _txt, btnOk, btnCancel]);
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }
    }
}
