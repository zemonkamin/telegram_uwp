namespace Telegram.Services
{
    public static class TelegramSettings
    {
        public const int ApiId = 1429;
        public const string ApiHash = "2bd96732edd02ee97089bf74ca94cc9f";
        public const int Layer = 214;
        public const int DefaultDcId = 2;

        public static string GetDcAddress(int dcId)
        {
            switch (dcId)
            {
                case 1: return "149.154.175.50";
                case 2: return "149.154.167.50";
                case 3: return "149.154.175.100";
                case 4: return "149.154.167.91";
                case 5: return "149.154.171.5";
                default: return "149.154.167.50";
            }
        }

        public const int TelegramPort = 443;
    }
}
