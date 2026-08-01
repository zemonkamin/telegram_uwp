using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace Telegram
{
    public sealed partial class MainPage : Page
    {
        private CancellationTokenSource _loginLoopCts;
        private Task _loginLoopTask;
        private bool _isNavigatingToChats;
        private bool _isStartingQrLogin;
        private bool _isSendingCode;
        private bool _isSigningIn;

        public MainPage()
        {
            InitializeComponent();
            InitializeCountryList();
            Loaded += MainPage_Loaded;
            Unloaded += Login_Unloaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isNavigatingToChats)
                return;

            PhonePanel.Visibility = Visibility.Visible;
            QrPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "Enter your phone number and press Continue.";
            try
            {
                PhoneNumberBox.Focus(FocusState.Programmatic);
            }
            catch
            {
            }
        }

        private void Login_Unloaded(object sender, RoutedEventArgs e)
        {
            StopLoginLoop();
        }

        private async void SendCodeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isNavigatingToChats || _isSendingCode)
                return;

            StopLoginLoop();
            PhonePanel.Visibility = Visibility.Visible;
            QrPanel.Visibility = Visibility.Collapsed;

            var phoneNumber = BuildPhoneNumber();
            if (string.IsNullOrEmpty(phoneNumber) || phoneNumber.Length < 8 || phoneNumber[0] != '+')
            {
                StatusText.Text = "Enter the phone number with country code.";
                return;
            }

            _isSendingCode = true;
            SendCodeButton.IsEnabled = false;
            SignInButton.IsEnabled = false;
            ShowQrButton.IsEnabled = false;
            StatusText.Text = "Sending Telegram auth.sendCode request...";

            try
            {
                var sent = await Task.Run(async () => await TelegramService.Instance.SendPhoneCodeAsync(phoneNumber));
                if (sent != null && sent.Authorized)
                {
                    StatusText.Text = "This session is already authorized. Loading chats...";
                    NavigateToChatsOnce();
                    return;
                }

                CodePanel.Visibility = Visibility.Visible;
                SignInButton.IsEnabled = true;
                StatusText.Text = sent == null || string.IsNullOrEmpty(sent.Message)
                    ? "Code sent. Enter the code from Telegram."
                    : sent.Message;

                try
                {
                    CodeBox.Focus(FocusState.Programmatic);
                }
                catch
                {
                }
            }
            catch (Exception ex)
            {
                if (!_isNavigatingToChats)
                    StatusText.Text = "Code request error: " + ToUserMessage(ex);
            }
            finally
            {
                _isSendingCode = false;
                if (!_isNavigatingToChats)
                {
                    SendCodeButton.IsEnabled = true;
                    ShowQrButton.IsEnabled = true;
                }
            }
        }

        private async void SignInButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isNavigatingToChats || _isSigningIn)
                return;

            var code = NormalizeCode(CodeBox.Text);
            if (string.IsNullOrEmpty(code))
            {
                StatusText.Text = "Enter the code from Telegram.";
                return;
            }

            _isSigningIn = true;
            SignInButton.IsEnabled = false;
            SendCodeButton.IsEnabled = false;
            ShowQrButton.IsEnabled = false;
            StatusText.Text = "Checking the code through Telegram auth.signIn...";

            try
            {
                await Task.Run(async () => await TelegramService.Instance.SignInWithPhoneCodeAsync(code));
                StatusText.Text = "Login completed. Loading chats...";
                NavigateToChatsOnce();
            }
            catch (Exception ex)
            {
                if (!_isNavigatingToChats)
                    StatusText.Text = "Login error: " + ToUserMessage(ex);
            }
            finally
            {
                _isSigningIn = false;
                if (!_isNavigatingToChats)
                {
                    SignInButton.IsEnabled = true;
                    SendCodeButton.IsEnabled = true;
                    ShowQrButton.IsEnabled = true;
                }
            }
        }

        private async void ShowQrButton_Click(object sender, RoutedEventArgs e)
        {
            await StartQrLoginAsync();
        }

        private void UsePhoneButton_Click(object sender, RoutedEventArgs e)
        {
            StopLoginLoop();
            QrPanel.Visibility = Visibility.Collapsed;
            PhonePanel.Visibility = Visibility.Visible;
            StatusText.Text = "Enter your phone number and press Continue.";
            try
            {
                PhoneNumberBox.Focus(FocusState.Programmatic);
            }
            catch
            {
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await StartQrLoginAsync();
        }

        private async void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            StopLoginLoop();
            await TelegramService.Instance.ResetSessionAsync();
            CodePanel.Visibility = Visibility.Collapsed;
            CodeBox.Text = string.Empty;
            TokenLinkBox.Text = string.Empty;
            QrImage.Source = null;
            PhonePanel.Visibility = Visibility.Visible;
            QrPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "Session has been reset. You can sign in again.";
        }

        private async Task StartQrLoginAsync()
        {
            if (_isNavigatingToChats || _isStartingQrLogin)
                return;

            _isStartingQrLogin = true;
            StopLoginLoop();
            PhonePanel.Visibility = Visibility.Collapsed;
            QrPanel.Visibility = Visibility.Visible;
            //RefreshButton.IsEnabled = false;
            ShowQrButton.IsEnabled = false;
            UsePhoneButton.IsEnabled = false;
            StatusText.Text = "Creating TDLib session and QR login token...";

            try
            {
                var qr = await Task.Run(async () => await TelegramService.Instance.CreateQrLoginAsync());
                ShowQr(qr);

                if (qr != null && qr.LoginUrl == "authorized")
                {
                    StatusText.Text = "Login confirmed. Loading chats...";
                    NavigateToChatsOnce();
                    return;
                }

                StatusText.Text = "QR is ready. Scan it in Telegram on your phone.";

                _loginLoopCts = new CancellationTokenSource();
                var token = _loginLoopCts.Token;
                _loginLoopTask = PollLoginAsync(token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!_isNavigatingToChats)
                    StatusText.Text = "QR login error: " + ToUserMessage(ex);
            }
            finally
            {
                _isStartingQrLogin = false;
                if (!_isNavigatingToChats)
                {
                    //RefreshButton.IsEnabled = true;
                    ShowQrButton.IsEnabled = true;
                    UsePhoneButton.IsEnabled = true;
                }
            }
        }

        private async Task PollLoginAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    token.ThrowIfCancellationRequested();

                    var status = await Task.Run(async () => await TelegramService.Instance.CheckQrLoginAsync());
                    token.ThrowIfCancellationRequested();

                    if (status == QrLoginState.Accepted)
                    {
                        StatusText.Text = "Login confirmed. Loading chats...";
                        NavigateToChatsOnce();
                        return;
                    }

                    var currentQr = TelegramService.Instance.CurrentQr;
                    if (currentQr != null && currentQr.LoginUrl != TokenLinkBox.Text)
                    {
                        ShowQr(currentQr);
                        StatusText.Text = "QR refreshed, waiting for confirmation.";
                    }

                    if (status == QrLoginState.TokenExpired)
                    {
                        var qr = await Task.Run(async () => await TelegramService.Instance.CreateQrLoginAsync());
                        token.ThrowIfCancellationRequested();
                        ShowQr(qr);
                        StatusText.Text = "Token expired, QR refreshed.";
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    StatusText.Text = "QR login polling error: " + ToUserMessage(ex);
            }
        }

        private void ShowQr(QrLoginInfo qr)
        {
            if (qr == null)
            {
                TokenLinkBox.Text = string.Empty;
                QrImage.Source = null;
                return;
            }

            TokenLinkBox.Text = qr.LoginUrl;

            if (string.IsNullOrEmpty(qr.LoginUrl) || qr.LoginUrl == "authorized")
            {
                QrImage.Source = null;
                return;
            }

            QrImage.Source = LocalQrBitmap.Create(qr.LoginUrl, 280);
        }

        private void StopLoginLoop()
        {
            if (_loginLoopCts != null)
            {
                _loginLoopCts.Cancel();
                _loginLoopCts.Dispose();
                _loginLoopCts = null;
            }

            _loginLoopTask = null;
        }

        private void NavigateToChatsOnce()
        {
            if (_isNavigatingToChats)
                return;

            _isNavigatingToChats = true;
            StopLoginLoop();

            SendCodeButton.IsEnabled = false;
            SignInButton.IsEnabled = false;
            ShowQrButton.IsEnabled = false;
            //RefreshButton.IsEnabled = false;
            //ResetButton.IsEnabled = false;
            UsePhoneButton.IsEnabled = false;

            if (Frame != null)
            {
                Frame.Navigate(typeof(Chats));
            }
        }

        private void InitializeCountryList()
        {
            CountryBox.ItemsSource = CreateCountries();
            CountryBox.SelectedIndex = 0;
            UpdateCountryCodeFromSelection();
        }

        private void CountryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCountryCodeFromSelection();
        }

        private void UpdateCountryCodeFromSelection()
        {
            var country = CountryBox.SelectedItem as CountryInfo;
            if (country == null)
                return;

            PhoneCodeBox.Text = country.PhoneCode;
            PhoneNumberBox.PlaceholderText = country.Example;
        }

        private static List<CountryInfo> CreateCountries()
        {
            return new List<CountryInfo>
            {
                new CountryInfo("France", "+33", "123456789"),
                new CountryInfo("Russia", "+7", "9991234567"),
                new CountryInfo("United States", "+1", "2025550187"),
                new CountryInfo("United Kingdom", "+44", "7123456789"),
                new CountryInfo("Ukraine", "+380", "501234567"),
                new CountryInfo("Belarus", "+375", "291234567"),
                new CountryInfo("Kazakhstan", "+7", "7011234567"),
                new CountryInfo("Armenia", "+374", "77123456"),
                new CountryInfo("Azerbaijan", "+994", "501234567"),
                new CountryInfo("Georgia", "+995", "555123456"),
                new CountryInfo("Moldova", "+373", "60123456"),
                new CountryInfo("Estonia", "+372", "51234567"),
                new CountryInfo("Latvia", "+371", "21234567"),
                new CountryInfo("Lithuania", "+370", "61234567"),
                new CountryInfo("Germany", "+49", "15123456789"),
                new CountryInfo("Italy", "+39", "3123456789"),
                new CountryInfo("Spain", "+34", "612345678"),
                new CountryInfo("Portugal", "+351", "912345678"),
                new CountryInfo("Netherlands", "+31", "612345678"),
                new CountryInfo("Belgium", "+32", "471234567"),
                new CountryInfo("Switzerland", "+41", "761234567"),
                new CountryInfo("Austria", "+43", "664123456"),
                new CountryInfo("Poland", "+48", "501234567"),
                new CountryInfo("Czech Republic", "+420", "601123456"),
                new CountryInfo("Slovakia", "+421", "901123456"),
                new CountryInfo("Hungary", "+36", "201234567"),
                new CountryInfo("Romania", "+40", "712345678"),
                new CountryInfo("Bulgaria", "+359", "881234567"),
                new CountryInfo("Greece", "+30", "6912345678"),
                new CountryInfo("Turkey", "+90", "5012345678"),
                new CountryInfo("Sweden", "+46", "701234567"),
                new CountryInfo("Norway", "+47", "41234567"),
                new CountryInfo("Denmark", "+45", "20123456"),
                new CountryInfo("Finland", "+358", "401234567"),
                new CountryInfo("Ireland", "+353", "851234567"),
                new CountryInfo("Israel", "+972", "501234567"),
                new CountryInfo("United Arab Emirates", "+971", "501234567"),
                new CountryInfo("Saudi Arabia", "+966", "501234567"),
                new CountryInfo("India", "+91", "9876543210"),
                new CountryInfo("China", "+86", "13123456789"),
                new CountryInfo("Japan", "+81", "9012345678"),
                new CountryInfo("South Korea", "+82", "1012345678"),
                new CountryInfo("Indonesia", "+62", "8123456789"),
                new CountryInfo("Vietnam", "+84", "912345678"),
                new CountryInfo("Thailand", "+66", "812345678"),
                new CountryInfo("Malaysia", "+60", "123456789"),
                new CountryInfo("Singapore", "+65", "81234567"),
                new CountryInfo("Philippines", "+63", "9123456789"),
                new CountryInfo("Australia", "+61", "412345678"),
                new CountryInfo("New Zealand", "+64", "211234567"),
                new CountryInfo("Canada", "+1", "4165550187"),
                new CountryInfo("Mexico", "+52", "5512345678"),
                new CountryInfo("Brazil", "+55", "11912345678"),
                new CountryInfo("Argentina", "+54", "91123456789"),
                new CountryInfo("Chile", "+56", "912345678"),
                new CountryInfo("Colombia", "+57", "3012345678"),
                new CountryInfo("Peru", "+51", "912345678"),
                new CountryInfo("Egypt", "+20", "1012345678"),
                new CountryInfo("South Africa", "+27", "721234567"),
                new CountryInfo("Nigeria", "+234", "8012345678")
            };
        }

        private sealed class CountryInfo
        {
            public CountryInfo(string name, string phoneCode, string example)
            {
                Name = name;
                PhoneCode = phoneCode;
                Example = example;
            }

            public string Name { get; private set; }
            public string PhoneCode { get; private set; }
            public string Example { get; private set; }

            public string DisplayName
            {
                get { return Name + " (" + PhoneCode + ")"; }
            }
        }

        private string BuildPhoneNumber()
        {
            var countryCode = NormalizePhoneNumber(PhoneCodeBox.Text);
            var localNumber = NormalizePhoneNumber(PhoneNumberBox.Text);

            if (string.IsNullOrEmpty(localNumber))
                return string.Empty;

            if (localNumber[0] == '+')
                return localNumber;

            if (string.IsNullOrEmpty(countryCode))
                return localNumber;

            if (countryCode[0] != '+')
                countryCode = "+" + countryCode;

            return countryCode + localNumber;
        }

        private static string NormalizePhoneNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var sb = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsDigit(c)) sb.Append(c);
                else if (c == '+' && sb.Length == 0) sb.Append(c);
            }

            return sb.ToString();
        }

        private static string NormalizeCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var sb = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsDigit(c) || char.IsLetter(c)) sb.Append(c);
            }

            return sb.ToString();
        }

        private static string ToUserMessage(Exception ex)
        {
            if (ex == null) return "неизвестная ошибка";
            return ex.Message;
        }

        private static class LocalQrBitmap
        {
            private const int Version = 6;
            private const int Size = 21 + (Version - 1) * 4;
            private const int DataCodewords = 136;
            private const int BlockCount = 2;
            private const int DataCodewordsPerBlock = 68;
            private const int EccCodewordsPerBlock = 18;

            public static WriteableBitmap Create(string text, int pixelSize)
            {
                var modules = EncodeText(text);
                return Render(modules, pixelSize);
            }

            private static WriteableBitmap Render(bool[,] modules, int pixelSize)
            {
                var quietZone = 4;
                var qrSize = Size + quietZone * 2;
                var scale = Math.Max(1, pixelSize / qrSize);
                var imageSize = qrSize * scale;
                var offset = (pixelSize - imageSize) / 2;

                var bitmap = new WriteableBitmap(pixelSize, pixelSize);
                var pixels = new byte[pixelSize * pixelSize * 4];

                for (var i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = 0;
                    pixels[i + 1] = 0;
                    pixels[i + 2] = 0;
                    pixels[i + 3] = 255;
                }

                for (var y = 0; y < Size; y++)
                {
                    for (var x = 0; x < Size; x++)
                    {
                        if (!modules[y, x])
                            continue;

                        var startX = offset + (x + quietZone) * scale;
                        var startY = offset + (y + quietZone) * scale;
                        for (var yy = 0; yy < scale; yy++)
                        {
                            for (var xx = 0; xx < scale; xx++)
                            {
                                var index = ((startY + yy) * pixelSize + startX + xx) * 4;
                                pixels[index] = 255;
                                pixels[index + 1] = 255;
                                pixels[index + 2] = 255;
                                pixels[index + 3] = 255;
                            }
                        }
                    }
                }

                using (var stream = bitmap.PixelBuffer.AsStream())
                {
                    stream.Seek(0, SeekOrigin.Begin);
                    stream.Write(pixels, 0, pixels.Length);
                }
                bitmap.Invalidate();
                return bitmap;
            }

            private static bool[,] EncodeText(string text)
            {
                var dataBytes = Encoding.UTF8.GetBytes(text);
                if (dataBytes.Length > 130)
                    throw new InvalidOperationException("QR token is too long for the built-in QR renderer.");

                var dataCodewords = CreateDataCodewords(dataBytes);
                var allCodewords = AddErrorCorrection(dataCodewords);

                var modules = new bool[Size, Size];
                var isFunction = new bool[Size, Size];
                DrawFunctionPatterns(modules, isFunction);
                DrawCodewords(modules, isFunction, allCodewords);

                var bestPenalty = int.MaxValue;
                bool[,] bestModules = null;

                for (var mask = 0; mask < 8; mask++)
                {
                    var candidate = (bool[,])modules.Clone();
                    ApplyMask(candidate, isFunction, mask);
                    DrawFormatBits(candidate, mask);

                    var penalty = GetPenaltyScore(candidate);
                    if (penalty < bestPenalty)
                    {
                        bestPenalty = penalty;
                        bestModules = candidate;
                    }
                }

                if (bestModules == null)
                    throw new InvalidOperationException("QR generation failed.");

                return bestModules;
            }

            private static byte[] CreateDataCodewords(byte[] dataBytes)
            {
                var bits = new BitBuffer();
                bits.AppendBits(0x4, 4); // byte mode
                bits.AppendBits(dataBytes.Length, 8); // versions 1-9 use 8 length bits in byte mode

                for (var i = 0; i < dataBytes.Length; i++)
                    bits.AppendBits(dataBytes[i] & 0xFF, 8);

                var capacityBits = DataCodewords * 8;
                var terminator = Math.Min(4, capacityBits - bits.Count);
                bits.AppendBits(0, terminator);

                while (bits.Count % 8 != 0)
                    bits.AppendBits(0, 1);

                var result = bits.ToByteArray();
                var codewords = new List<byte>(result);
                var pad = true;
                while (codewords.Count < DataCodewords)
                {
                    codewords.Add((byte)(pad ? 0xEC : 0x11));
                    pad = !pad;
                }

                return codewords.ToArray();
            }

            private static byte[] AddErrorCorrection(byte[] dataCodewords)
            {
                var divisor = ReedSolomonComputeDivisor(EccCodewordsPerBlock);
                var dataBlocks = new byte[BlockCount][];
                var eccBlocks = new byte[BlockCount][];

                for (var block = 0; block < BlockCount; block++)
                {
                    dataBlocks[block] = new byte[DataCodewordsPerBlock];
                    Array.Copy(dataCodewords, block * DataCodewordsPerBlock, dataBlocks[block], 0, DataCodewordsPerBlock);
                    eccBlocks[block] = ReedSolomonComputeRemainder(dataBlocks[block], divisor);
                }

                var result = new List<byte>();
                for (var i = 0; i < DataCodewordsPerBlock; i++)
                {
                    for (var block = 0; block < BlockCount; block++)
                        result.Add(dataBlocks[block][i]);
                }

                for (var i = 0; i < EccCodewordsPerBlock; i++)
                {
                    for (var block = 0; block < BlockCount; block++)
                        result.Add(eccBlocks[block][i]);
                }

                return result.ToArray();
            }

            private static void DrawFunctionPatterns(bool[,] modules, bool[,] isFunction)
            {
                DrawFinderPattern(modules, isFunction, 0, 0);
                DrawFinderPattern(modules, isFunction, Size - 7, 0);
                DrawFinderPattern(modules, isFunction, 0, Size - 7);

                for (var i = 8; i < Size - 8; i++)
                {
                    var dark = i % 2 == 0;
                    SetFunctionModule(modules, isFunction, 6, i, dark);
                    SetFunctionModule(modules, isFunction, i, 6, dark);
                }

                var centers = new[] { 6, 34 };
                for (var i = 0; i < centers.Length; i++)
                {
                    for (var j = 0; j < centers.Length; j++)
                    {
                        var x = centers[i];
                        var y = centers[j];
                        if (!isFunction[y, x])
                            DrawAlignmentPattern(modules, isFunction, x, y);
                    }
                }

                ReserveFormatBits(modules, isFunction);
            }

            private static void DrawFinderPattern(bool[,] modules, bool[,] isFunction, int left, int top)
            {
                for (var dy = -1; dy <= 7; dy++)
                {
                    for (var dx = -1; dx <= 7; dx++)
                    {
                        var x = left + dx;
                        var y = top + dy;
                        if (x < 0 || x >= Size || y < 0 || y >= Size)
                            continue;

                        var dark = dx >= 0 && dx <= 6 && dy >= 0 && dy <= 6 &&
                                   (dx == 0 || dx == 6 || dy == 0 || dy == 6 ||
                                    (dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4));
                        SetFunctionModule(modules, isFunction, x, y, dark);
                    }
                }
            }

            private static void DrawAlignmentPattern(bool[,] modules, bool[,] isFunction, int centerX, int centerY)
            {
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        SetFunctionModule(modules, isFunction, centerX + dx, centerY + dy, distance != 1);
                    }
                }
            }

            private static void ReserveFormatBits(bool[,] modules, bool[,] isFunction)
            {
                for (var i = 0; i <= 8; i++)
                {
                    if (i != 6)
                    {
                        SetFunctionModule(modules, isFunction, 8, i, false);
                        SetFunctionModule(modules, isFunction, i, 8, false);
                    }
                }

                for (var i = 0; i < 8; i++)
                {
                    SetFunctionModule(modules, isFunction, Size - 1 - i, 8, false);
                    SetFunctionModule(modules, isFunction, 8, Size - 1 - i, false);
                }

                SetFunctionModule(modules, isFunction, 8, Size - 8, true);
            }

            private static void DrawFormatBits(bool[,] modules, int mask)
            {
                var data = (1 << 3) | mask; // error correction level L
                var rem = data;
                for (var i = 0; i < 10; i++)
                    rem = (rem << 1) ^ (((rem >> 9) & 1) != 0 ? 0x537 : 0);

                var bits = ((data << 10) | rem) ^ 0x5412;

                for (var i = 0; i <= 5; i++)
                    SetModule(modules, 8, i, GetBit(bits, i));
                SetModule(modules, 8, 7, GetBit(bits, 6));
                SetModule(modules, 8, 8, GetBit(bits, 7));
                SetModule(modules, 7, 8, GetBit(bits, 8));
                for (var i = 9; i < 15; i++)
                    SetModule(modules, 14 - i, 8, GetBit(bits, i));

                for (var i = 0; i < 8; i++)
                    SetModule(modules, Size - 1 - i, 8, GetBit(bits, i));
                for (var i = 8; i < 15; i++)
                    SetModule(modules, 8, Size - 15 + i, GetBit(bits, i));
                SetModule(modules, 8, Size - 8, true);
            }

            private static void DrawCodewords(bool[,] modules, bool[,] isFunction, byte[] data)
            {
                var bitIndex = 0;
                var upward = true;

                for (var right = Size - 1; right >= 1; right -= 2)
                {
                    if (right == 6)
                        right = 5;

                    for (var vertical = 0; vertical < Size; vertical++)
                    {
                        var y = upward ? Size - 1 - vertical : vertical;
                        for (var j = 0; j < 2; j++)
                        {
                            var x = right - j;
                            if (isFunction[y, x])
                                continue;

                            var dark = false;
                            if (bitIndex < data.Length * 8)
                                dark = ((data[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;

                            modules[y, x] = dark;
                            bitIndex++;
                        }
                    }

                    upward = !upward;
                }
            }

            private static void ApplyMask(bool[,] modules, bool[,] isFunction, int mask)
            {
                for (var y = 0; y < Size; y++)
                {
                    for (var x = 0; x < Size; x++)
                    {
                        if (!isFunction[y, x] && GetMaskBit(mask, x, y))
                            modules[y, x] = !modules[y, x];
                    }
                }
            }

            private static bool GetMaskBit(int mask, int x, int y)
            {
                switch (mask)
                {
                    case 0: return (x + y) % 2 == 0;
                    case 1: return y % 2 == 0;
                    case 2: return x % 3 == 0;
                    case 3: return (x + y) % 3 == 0;
                    case 4: return (x / 3 + y / 2) % 2 == 0;
                    case 5: return x * y % 2 + x * y % 3 == 0;
                    case 6: return (x * y % 2 + x * y % 3) % 2 == 0;
                    case 7: return ((x + y) % 2 + x * y % 3) % 2 == 0;
                    default: throw new ArgumentOutOfRangeException("mask");
                }
            }

            private static int GetPenaltyScore(bool[,] modules)
            {
                var result = 0;

                for (var y = 0; y < Size; y++)
                {
                    var runColor = false;
                    var runLength = 0;
                    for (var x = 0; x < Size; x++)
                    {
                        if (x == 0 || modules[y, x] != runColor)
                        {
                            if (runLength >= 5)
                                result += runLength - 2;
                            runColor = modules[y, x];
                            runLength = 1;
                        }
                        else
                        {
                            runLength++;
                        }
                    }
                    if (runLength >= 5)
                        result += runLength - 2;
                }

                for (var x = 0; x < Size; x++)
                {
                    var runColor = false;
                    var runLength = 0;
                    for (var y = 0; y < Size; y++)
                    {
                        if (y == 0 || modules[y, x] != runColor)
                        {
                            if (runLength >= 5)
                                result += runLength - 2;
                            runColor = modules[y, x];
                            runLength = 1;
                        }
                        else
                        {
                            runLength++;
                        }
                    }
                    if (runLength >= 5)
                        result += runLength - 2;
                }

                for (var y = 0; y < Size - 1; y++)
                {
                    for (var x = 0; x < Size - 1; x++)
                    {
                        var color = modules[y, x];
                        if (color == modules[y, x + 1] && color == modules[y + 1, x] && color == modules[y + 1, x + 1])
                            result += 3;
                    }
                }

                for (var y = 0; y < Size; y++)
                {
                    for (var x = 0; x <= Size - 7; x++)
                    {
                        if (HasFinderLikePattern(modules, x, y, true))
                            result += 40;
                    }
                }

                for (var x = 0; x < Size; x++)
                {
                    for (var y = 0; y <= Size - 7; y++)
                    {
                        if (HasFinderLikePattern(modules, x, y, false))
                            result += 40;
                    }
                }

                var dark = 0;
                for (var y = 0; y < Size; y++)
                {
                    for (var x = 0; x < Size; x++)
                    {
                        if (modules[y, x])
                            dark++;
                    }
                }

                var total = Size * Size;
                var k = Math.Abs(dark * 20 - total * 10) / total;
                result += k * 10;

                return result;
            }

            private static bool HasFinderLikePattern(bool[,] modules, int x, int y, bool horizontal)
            {
                if (!GetPatternModule(modules, x, y, 0, horizontal)) return false;
                if (GetPatternModule(modules, x, y, 1, horizontal)) return false;
                if (!GetPatternModule(modules, x, y, 2, horizontal)) return false;
                if (!GetPatternModule(modules, x, y, 3, horizontal)) return false;
                if (!GetPatternModule(modules, x, y, 4, horizontal)) return false;
                if (GetPatternModule(modules, x, y, 5, horizontal)) return false;
                if (!GetPatternModule(modules, x, y, 6, horizontal)) return false;

                var beforeWhite = true;
                for (var i = 1; i <= 4; i++)
                {
                    var xx = horizontal ? x - i : x;
                    var yy = horizontal ? y : y - i;
                    if (xx >= 0 && yy >= 0 && modules[yy, xx])
                        beforeWhite = false;
                }

                var afterWhite = true;
                for (var i = 7; i <= 10; i++)
                {
                    var xx = horizontal ? x + i : x;
                    var yy = horizontal ? y : y + i;
                    if (xx < Size && yy < Size && modules[yy, xx])
                        afterWhite = false;
                }

                return beforeWhite || afterWhite;
            }

            private static bool GetPatternModule(bool[,] modules, int x, int y, int offset, bool horizontal)
            {
                return horizontal ? modules[y, x + offset] : modules[y + offset, x];
            }

            private static byte[] ReedSolomonComputeDivisor(int degree)
            {
                var result = new byte[degree];
                result[degree - 1] = 1;
                var root = 1;

                for (var i = 0; i < degree; i++)
                {
                    for (var j = 0; j < degree; j++)
                    {
                        result[j] = (byte)ReedSolomonMultiply(result[j] & 0xFF, root);
                        if (j + 1 < degree)
                            result[j] ^= result[j + 1];
                    }
                    root = ReedSolomonMultiply(root, 0x02);
                }

                return result;
            }

            private static byte[] ReedSolomonComputeRemainder(byte[] data, byte[] divisor)
            {
                var result = new byte[divisor.Length];

                for (var i = 0; i < data.Length; i++)
                {
                    var factor = (data[i] & 0xFF) ^ (result[0] & 0xFF);
                    Array.Copy(result, 1, result, 0, result.Length - 1);
                    result[result.Length - 1] = 0;

                    for (var j = 0; j < result.Length; j++)
                        result[j] = (byte)((result[j] & 0xFF) ^ ReedSolomonMultiply(divisor[j] & 0xFF, factor));
                }

                return result;
            }

            private static int ReedSolomonMultiply(int x, int y)
            {
                var z = 0;
                for (var i = 7; i >= 0; i--)
                {
                    z = ((z << 1) ^ (((z >> 7) & 1) * 0x11D)) & 0xFF;
                    if (((y >> i) & 1) != 0)
                        z ^= x;
                }
                return z;
            }

            private static bool GetBit(int value, int index)
            {
                return ((value >> index) & 1) != 0;
            }

            private static void SetFunctionModule(bool[,] modules, bool[,] isFunction, int x, int y, bool dark)
            {
                modules[y, x] = dark;
                isFunction[y, x] = true;
            }

            private static void SetModule(bool[,] modules, int x, int y, bool dark)
            {
                modules[y, x] = dark;
            }

            private sealed class BitBuffer
            {
                private readonly List<int> _bits = new List<int>();

                public int Count
                {
                    get { return _bits.Count; }
                }

                public void AppendBits(int value, int length)
                {
                    if (length < 0 || length > 31)
                        throw new ArgumentOutOfRangeException("length");

                    for (var i = length - 1; i >= 0; i--)
                        _bits.Add((value >> i) & 1);
                }

                public byte[] ToByteArray()
                {
                    var result = new byte[(_bits.Count + 7) / 8];
                    for (var i = 0; i < _bits.Count; i++)
                    {
                        if (_bits[i] != 0)
                            result[i >> 3] |= (byte)(1 << (7 - (i & 7)));
                    }
                    return result;
                }
            }
        }
    }
}
