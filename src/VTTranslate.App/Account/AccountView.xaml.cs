using System.Windows.Controls;

namespace VTTranslate.App.Account;

/// <summary>Phase 7.3 — pure view; all behavior lives in <see cref="AccountViewModel"/> (bound via DataContext by the host, e.g. MainWindow).</summary>
public partial class AccountView : UserControl
{
    public AccountView()
    {
        InitializeComponent();
    }
}
