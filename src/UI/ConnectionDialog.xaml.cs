using System.Windows;
using System.Windows.Input;

namespace LocalPanel.UI;

/// <summary>Small popup: view/copy connection details, edit credentials where applicable.</summary>
public partial class ConnectionDialog : Window
{
    /// <summary>Called with (user, password) when the user saves; returns an error message or null.</summary>
    public Func<string, string, Task<string?>>? OnSave { get; set; }
    /// <summary>Rebuilds the connection text after a save (new creds already stored).</summary>
    public Func<string>? BuildConnection { get; set; }

    public ConnectionDialog(string title, string? user, string? password, string connection, bool hasCredentials)
    {
        InitializeComponent();
        TitleText.Text = title;
        UserBox.Text = user ?? "";
        PassBox.Text = password ?? "";
        ConnBox.Text = connection;
        if (!hasCredentials)
        {
            CredGrid.Visibility = Visibility.Collapsed;
            SaveBtn.Visibility = Visibility.Collapsed;
        }
        Loaded += (_, _) =>
        {
            if (hasCredentials) UserBox.Focus();
            else { ConnBox.Focus(); ConnBox.SelectAll(); }
        };
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) { try { DragMove(); } catch { } }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ConnBox.Text);
            StatusText.Text = "Copied.";
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) => { t.Stop(); if (StatusText.Text == "Copied.") StatusText.Text = ""; };
            t.Start();
        }
        catch { }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (OnSave is null) { Close(); return; }
        var spinner = ButtonSpinner.Start(SaveBtn);
        StatusText.Text = "Applying…";
        try
        {
            var err = await OnSave(UserBox.Text.Trim(), PassBox.Text);
            if (BuildConnection is not null) ConnBox.Text = BuildConnection();
            StatusText.Text = err is null ? "Saved and applied." : err;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { spinner.Dispose(); }
    }
}
