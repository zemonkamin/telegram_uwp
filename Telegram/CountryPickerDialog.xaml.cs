using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;

namespace Telegram
{
    public sealed class CountryInfo
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
    }

    // A group is itself the list of its items so CollectionViewSource can group on it directly.
    public sealed class CountryGroup : List<CountryInfo>
    {
        public string Key { get; set; }
    }

    public sealed partial class CountryPickerDialog : ContentDialog
    {
        private readonly CollectionViewSource _groupedSource = new CollectionViewSource { IsSourceGrouped = true };

        public CountryInfo SelectedCountry { get; private set; }
        public bool Picked { get; private set; }

        public CountryPickerDialog(CountryInfo current)
        {
            InitializeComponent();
            SelectedCountry = current;

            FillView();
        }

        private void FillView()
        {
            var groups = BuildGroups();
            _groupedSource.Source = groups;

            var view = _groupedSource.View;
            CountryList.ItemsSource = view;

            // The zoomed-out (SemanticZoom) view lists the group headers themselves.
            if (GroupList != null && view != null)
                GroupList.ItemsSource = view.CollectionGroups;
        }

        private static List<CountryGroup> BuildGroups()
        {
            var groups = new List<CountryGroup>();
            CountryGroup current = null;
            var countries = CountryCatalog.All;

            for (var i = 0; i < countries.Count; i++)
            {
                var country = countries[i];
                var key = GroupKey(country.Name);
                if (current == null || current.Key != key)
                {
                    current = new CountryGroup { Key = key };
                    groups.Add(current);
                }
                current.Add(country);
            }

            return groups;
        }

        private static string GroupKey(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "#";

            var c = char.ToUpperInvariant(name[0]);
            return c >= 'A' && c <= 'Z' ? c.ToString() : "#";
        }

        private void CountryList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var country = e.ClickedItem as CountryInfo;
            if (country == null)
                return;

            SelectedCountry = country;
            Picked = true;
            Hide();
        }
    }

    public static class CountryCatalog
    {
        private static List<CountryInfo> _all;

        public static IList<CountryInfo> All
        {
            get
            {
                if (_all == null)
                    _all = BuildSorted();
                return _all;
            }
        }

        public static CountryInfo FindByName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            var all = All;
            for (var i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return all[i];
            }
            return null;
        }

        private static List<CountryInfo> BuildSorted()
        {
            var list = new List<CountryInfo>
            {
                new CountryInfo("Argentina", "+54", "91123456789"),
                new CountryInfo("Armenia", "+374", "77123456"),
                new CountryInfo("Australia", "+61", "412345678"),
                new CountryInfo("Austria", "+43", "664123456"),
                new CountryInfo("Azerbaijan", "+994", "501234567"),
                new CountryInfo("Belarus", "+375", "291234567"),
                new CountryInfo("Belgium", "+32", "471234567"),
                new CountryInfo("Brazil", "+55", "11912345678"),
                new CountryInfo("Bulgaria", "+359", "881234567"),
                new CountryInfo("Canada", "+1", "4165550187"),
                new CountryInfo("Chile", "+56", "912345678"),
                new CountryInfo("China", "+86", "13123456789"),
                new CountryInfo("Colombia", "+57", "3012345678"),
                new CountryInfo("Czech Republic", "+420", "601123456"),
                new CountryInfo("Denmark", "+45", "20123456"),
                new CountryInfo("Egypt", "+20", "1012345678"),
                new CountryInfo("Estonia", "+372", "51234567"),
                new CountryInfo("Finland", "+358", "401234567"),
                new CountryInfo("France", "+33", "123456789"),
                new CountryInfo("Georgia", "+995", "555123456"),
                new CountryInfo("Germany", "+49", "15123456789"),
                new CountryInfo("Greece", "+30", "6912345678"),
                new CountryInfo("Hungary", "+36", "201234567"),
                new CountryInfo("India", "+91", "9876543210"),
                new CountryInfo("Indonesia", "+62", "8123456789"),
                new CountryInfo("Ireland", "+353", "851234567"),
                new CountryInfo("Israel", "+972", "501234567"),
                new CountryInfo("Italy", "+39", "3123456789"),
                new CountryInfo("Japan", "+81", "9012345678"),
                new CountryInfo("Kazakhstan", "+7", "7011234567"),
                new CountryInfo("Latvia", "+371", "21234567"),
                new CountryInfo("Lithuania", "+370", "61234567"),
                new CountryInfo("Malaysia", "+60", "123456789"),
                new CountryInfo("Mexico", "+52", "5512345678"),
                new CountryInfo("Moldova", "+373", "60123456"),
                new CountryInfo("Netherlands", "+31", "612345678"),
                new CountryInfo("New Zealand", "+64", "211234567"),
                new CountryInfo("Nigeria", "+234", "8012345678"),
                new CountryInfo("Norway", "+47", "41234567"),
                new CountryInfo("Peru", "+51", "912345678"),
                new CountryInfo("Philippines", "+63", "9123456789"),
                new CountryInfo("Poland", "+48", "501234567"),
                new CountryInfo("Portugal", "+351", "912345678"),
                new CountryInfo("Romania", "+40", "712345678"),
                new CountryInfo("Russia", "+7", "9991234567"),
                new CountryInfo("Saudi Arabia", "+966", "501234567"),
                new CountryInfo("Singapore", "+65", "81234567"),
                new CountryInfo("Slovakia", "+421", "901123456"),
                new CountryInfo("South Africa", "+27", "721234567"),
                new CountryInfo("South Korea", "+82", "1012345678"),
                new CountryInfo("Spain", "+34", "612345678"),
                new CountryInfo("Sweden", "+46", "701234567"),
                new CountryInfo("Switzerland", "+41", "761234567"),
                new CountryInfo("Thailand", "+66", "812345678"),
                new CountryInfo("Turkey", "+90", "5012345678"),
                new CountryInfo("Ukraine", "+380", "501234567"),
                new CountryInfo("United Arab Emirates", "+971", "501234567"),
                new CountryInfo("United Kingdom", "+44", "7123456789"),
                new CountryInfo("United States", "+1", "2025550187"),
                new CountryInfo("Vietnam", "+84", "912345678")
            };

            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }
    }
}
