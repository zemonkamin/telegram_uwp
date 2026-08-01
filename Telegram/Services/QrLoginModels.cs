namespace Telegram.Services
{
    public sealed class QrLoginInfo
    {
        public string LoginUrl { get; set; }
        public string QrImageUrl { get; set; }
        public int ExpiresUnix { get; set; }
    }

    public enum QrLoginState
    {
        Waiting,
        TokenExpired,
        Accepted
    }

    // A chat's custom TDLib background (wallpaper). Display-only for now: either an image
    // file (ImageUri) or a solid/gradient fill.
    public sealed class ChatWallpaperInfo
    {
        public string ImageUri { get; set; }
        public bool IsBlurred { get; set; }

        public bool HasSolid { get; set; }
        public int SolidColor { get; set; }

        public bool HasGradient { get; set; }
        public int GradientTopColor { get; set; }
        public int GradientBottomColor { get; set; }
        public int GradientRotation { get; set; }

        public int[] FreeformColors { get; set; }

        public bool HasFill
        {
            get { return HasSolid || HasGradient || (FreeformColors != null && FreeformColors.Length > 0); }
        }

        public bool IsEmpty
        {
            get { return string.IsNullOrEmpty(ImageUri) && !HasFill; }
        }
    }
}
