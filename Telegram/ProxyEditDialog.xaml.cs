using Telegram.Services;
using Windows.UI.Xaml.Controls;

namespace Telegram
{
    public sealed partial class ProxyEditDialog : ContentDialog
    {
        private readonly string _id;

        public ProxyProfile Result { get; private set; }
        public bool Saved { get; private set; }

        public ProxyEditDialog(ProxyProfile existing)
        {
            InitializeComponent();

            _id = existing != null && !string.IsNullOrEmpty(existing.Id) ? existing.Id : ProxyStore.NewId();

            if (existing != null)
            {
                SelectType(existing.Mode);
                ServerBox.Text = existing.Server ?? "";
                PortBox.Text = existing.Port ?? "";
                SecretBox.Text = existing.Secret ?? "";
                UsernameBox.Text = existing.Username ?? "";
                PasswordBox.Password = existing.Password ?? "";
            }
            else
            {
                TypeBox.SelectedIndex = 0;
            }

            UpdateFieldVisibility();
        }

        private void SelectType(string mode)
        {
            if (mode == ProxySettings.ModeSocks)
                TypeBox.SelectedIndex = 1;
            else if (mode == ProxySettings.ModeHttp)
                TypeBox.SelectedIndex = 2;
            else
                TypeBox.SelectedIndex = 0;
        }

        private string SelectedMode()
        {
            switch (TypeBox.SelectedIndex)
            {
                case 1: return ProxySettings.ModeSocks;
                case 2: return ProxySettings.ModeHttp;
                default: return ProxySettings.ModeMtproto;
            }
        }

        private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateFieldVisibility();
        }

        private void UpdateFieldVisibility()
        {
            if (SecretBox == null)
                return;

            var mtproto = SelectedMode() == ProxySettings.ModeMtproto;
            SecretBox.Visibility = mtproto ? Windows.UI.Xaml.Visibility.Visible : Windows.UI.Xaml.Visibility.Collapsed;
            UsernameBox.Visibility = mtproto ? Windows.UI.Xaml.Visibility.Collapsed : Windows.UI.Xaml.Visibility.Visible;
            PasswordBox.Visibility = mtproto ? Windows.UI.Xaml.Visibility.Collapsed : Windows.UI.Xaml.Visibility.Visible;
        }

        private void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var mode = SelectedMode();
            var server = ServerBox.Text.Trim();
            var port = PortBox.Text.Trim();

            if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(port))
            {
                ErrorText.Text = "Enter a server and port.";
                args.Cancel = true;
                return;
            }

            if (mode == ProxySettings.ModeMtproto && string.IsNullOrEmpty(SecretBox.Text.Trim()))
            {
                ErrorText.Text = "MTProto proxies require a secret.";
                args.Cancel = true;
                return;
            }

            Result = new ProxyProfile
            {
                Id = _id,
                Mode = mode,
                Server = server,
                Port = port,
                Secret = SecretBox.Text.Trim(),
                Username = UsernameBox.Text.Trim(),
                Password = PasswordBox.Password
            };
            Saved = true;
        }
    }
}
