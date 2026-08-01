using System.Collections.ObjectModel;

namespace Telegram.Models
{
    public sealed class StickerSetViewModel
    {
        public long Id { get; set; }
        public string Title { get; set; }
        public string ShortName { get; set; }
        public string Kind { get; set; }
        public ObservableCollection<StickerItemViewModel> Stickers { get; private set; }

        public StickerSetViewModel()
        {
            Stickers = new ObservableCollection<StickerItemViewModel>();
        }

        public string DisplayTitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Title)) return Title;
                if (!string.IsNullOrWhiteSpace(ShortName)) return ShortName;
                return "Stickers";
            }
        }
    }
}
