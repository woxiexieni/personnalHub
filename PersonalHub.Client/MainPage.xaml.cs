using Personalhub;

namespace PersonalHub.Client
{
    public partial class MainPage : ContentPage
    {
        int count = 0;

        // private readonly Personalhub.System.SystemClient _client;
        //Personalhub.System.SystemClient client

        public MainPage()
        {

            InitializeComponent();
           // _client = client;
        }

        private void OnCounterClicked(object sender, EventArgs e)
        {
            count++;

            if (count == 1)
                CounterBtn.Text = $"Clicked {count} time";
            else
                CounterBtn.Text = $"Clicked {count} times";

            SemanticScreenReader.Announce(CounterBtn.Text);
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            try
            {
                System.Diagnostics.Debug.WriteLine("开始尝试连接服务器...");

                //实例化请求
               // var request = new Personalhub.PingRequest();


               // var response = await _client.PingAsync(request);

                //System.Diagnostics.Debug.WriteLine($"Ping 成功！ok: {response.Ok}");
                //System.Diagnostics.Debug.WriteLine($"server_version: {response.ServerVersion}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"连接服务器失败：{ex.Message}");
            }
        }


    }

}
