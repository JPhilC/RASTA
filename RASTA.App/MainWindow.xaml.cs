using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RASTA.App.Services;
using RASTA.App.ViewModels;

namespace RASTA.App
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            var navigationVM = App.Services.GetRequiredService<NavigationViewModel>();

            DataContext = navigationVM;

            // Default page
            navigationVM.NavigatePrepareCommand.Execute(null);
        }
    }
}
