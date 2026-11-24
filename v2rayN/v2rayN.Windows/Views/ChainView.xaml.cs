using v2rayN.Manager;

namespace v2rayN.Views;

public partial class ChainView
{
    private const string ApplyMsg = "Saved. Turn Chain off and on again to apply.";

    public ChainView()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            txtIni.Text = ChainManager.ReadIni();
            txtPath.Text = ChainManager.IniPath;
            chkConsole.IsChecked = ChainManager.ShowConsole;
            txtMsg.Text = ChainManager.IniExists
                ? string.Empty
                : "dns_chain.ini not found. Turn Chain on once, or press Reset to default, so chain.exe creates it.";
        };

        btnSave.Click += BtnSave_Click;
        btnReset.Click += BtnReset_Click;
        chkConsole.Click += ChkConsole_Click;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(txtIni.Text))
        {
            txtMsg.Text = "Nothing to save. Use Reset to default to let chain.exe create the file.";
            return;
        }
        try
        {
            ChainManager.SaveIni(txtIni.Text);
            txtMsg.Text = ChainManager.IsRunning ? ApplyMsg : "Saved.";
        }
        catch (Exception ex)
        {
            txtMsg.Text = ex.Message;
        }
    }

    private async void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        if (UI.ShowYesNo("Reset dns_chain.ini to default?\nchain.exe will be started once (hidden) to create a fresh file.") != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        txtMsg.Text = "Creating default dns_chain.ini ...";
        try
        {
            var err = await ChainManager.ResetIniAsync();
            txtIni.Text = ChainManager.ReadIni();
            txtMsg.Text = err ?? "Reset to default.";
        }
        catch (Exception ex)
        {
            txtMsg.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        btnReset.IsEnabled = !busy;
        btnSave.IsEnabled = !busy;
        btnClose.IsEnabled = !busy;
        txtIni.IsEnabled = !busy;
    }

    private void ChkConsole_Click(object sender, RoutedEventArgs e)
    {
        ChainManager.ShowConsole = chkConsole.IsChecked == true;
        if (ChainManager.IsRunning)
        {
            txtMsg.Text = "Turn Chain off and on again to apply.";
        }
    }
}
