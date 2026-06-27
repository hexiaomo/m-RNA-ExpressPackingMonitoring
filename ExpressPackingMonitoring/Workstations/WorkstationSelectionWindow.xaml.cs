using System.Windows;

namespace ExpressPackingMonitoring;

public partial class WorkstationSelectionWindow : Window
{
    public string? SelectedRole { get; private set; }

    public WorkstationSelectionWindow()
    {
        InitializeComponent();
    }

    private void CameraMonitor_Click(object sender, RoutedEventArgs e)
    {
        SelectedRole = WorkstationRoles.CameraMonitor;
        DialogResult = true;
    }

    private void PrintStation_Click(object sender, RoutedEventArgs e)
    {
        SelectedRole = WorkstationRoles.PrintStation;
        DialogResult = true;
    }
}
