using NexScreen.Core.Security;

namespace NexScreen.Tray;

/// <summary>
/// Ventana con el PIN para emparejar una tablet por WiFi. Cerrarla o tocar "Cancelar" anula el
/// emparejamiento; cuando termina (bien o mal) la cierra la bandeja con <see cref="CloseQuietly"/>.
/// </summary>
internal sealed class PairingForm : Form
{
    private readonly CancellationTokenSource _cancel;
    private bool _closingQuietly;

    public PairingForm(string device, string pin, CancellationTokenSource cancel)
    {
        _cancel = cancel;

        Text = "NexScreen: emparejar tablet";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 10f);
        if (Environment.ProcessPath is { } exe) Icon = Icon.ExtractAssociatedIcon(exe);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(20, 16, 20, 16),
        };

        layout.Controls.Add(new Label
        {
            Text = $"{device} quiere usar este PC como monitor por WiFi.\nPara permitirlo, ingresa este código en la tablet:",
            AutoSize = true,
            MaximumSize = new Size(380, 0),
            Margin = new Padding(0, 0, 0, 12),
        });
        layout.Controls.Add(new Label
        {
            Text = Pairing.Format(pin),
            AutoSize = true,
            Font = new Font("Segoe UI", 32f, FontStyle.Bold),
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 8),
        });
        layout.Controls.Add(new Label
        {
            Text = "El código vence en 2 minutos. Si no fuiste tú, toca Cancelar.",
            AutoSize = true,
            MaximumSize = new Size(380, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 12),
        });

        var cancelButton = new Button { Text = "Cancelar", AutoSize = true, Anchor = AnchorStyles.Right, DialogResult = DialogResult.Cancel };
        layout.Controls.Add(cancelButton);
        CancelButton = cancelButton;

        Controls.Add(layout);
    }

    /// <summary>Cierra la ventana sin cancelar (el emparejamiento ya terminó).</summary>
    public void CloseQuietly()
    {
        _closingQuietly = true;
        Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (!_closingQuietly)
        {
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // El emparejamiento terminó justo antes.
            }
        }

        base.OnFormClosed(e);
    }
}
