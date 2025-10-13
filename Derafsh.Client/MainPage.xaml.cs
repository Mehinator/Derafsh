namespace Derafsh.Client; 
using Derafsh.Client.Models;

public partial class MainPage : ContentPage
{
    public List<Server> Servers { get; set; }
    private Server selectedServer;
    private bool isConnected = false; // ۱. حافظه‌ی جدید مهندس ما!

    public MainPage()
    {
        InitializeComponent();
        Servers = new List<Server>
        {
            new Server { Country = "Germany", City = "Frankfurt", FlagUrl = "germany_flag.png" },
            new Server { Country = "USA", City = "New York", FlagUrl = "usa_flag.png" },
            new Server { Country = "Netherlands", City = "Amsterdam", FlagUrl = "netherlands_flag.png" },
            new Server { Country = "Japan", City = "Tokyo", FlagUrl = "japan_flag.png" }
        };
        this.BindingContext = this;
    }

    private void OnServerSelected(object sender, SelectedItemChangedEventArgs e)
    {
        if (e.SelectedItem == null)
            return;
        selectedServer = (Server)e.SelectedItem;
    }

    private async void OnConnectButtonClicked(object sender, EventArgs e)
    {
        // ۲. حالا مهندس ما اول وضعیت رو چک می‌کنه
        if (isConnected == false) // اگه قطع بودیم...
        {
            if (selectedServer == null)
            {
                await DisplayAlert("خطا", "لطفاً ابتدا یک سرور را از لیست انتخاب کنید.", "باشه");
                return;
            }

            // دستورالعمل وصل شدن
            StatusLabel.Text = $"در حال اتصال به: {selectedServer.Country}...";
            ConnectButton.Text = "قطع اتصال";
            isConnected = true; // وضعیت رو به‌روز می‌کنه
        }
        else // اگه وصل بودیم...
        {
            // دستورالعمل قطع شدن
            StatusLabel.Text = "وضعیت: قطع";
            ConnectButton.Text = "اتصال";
            isConnected = false; // وضعیت رو به‌روز می‌کنه
        }
    }
}