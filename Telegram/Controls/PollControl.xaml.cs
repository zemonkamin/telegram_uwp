using System;
using Telegram.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Controls
{
    public sealed class PollVoteRequestedEventArgs : EventArgs
    {
        public PollVoteRequestedEventArgs(StructuredMediaItemViewModel option)
        {
            Option = option;
        }

        public StructuredMediaItemViewModel Option { get; private set; }
    }

    public sealed partial class PollControl : UserControl
    {
        public event EventHandler<PollVoteRequestedEventArgs> VoteRequested;
        public event EventHandler AddOptionRequested;

        public PollControl()
        {
            InitializeComponent();
        }

        private void PollOptionButton_Click(object sender, RoutedEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var option = fe == null ? null : fe.DataContext as StructuredMediaItemViewModel;
            if (option == null || option.IsBusy) return;

            var handler = VoteRequested;
            if (handler != null) handler(this, new PollVoteRequestedEventArgs(option));
        }

        private void AddOptionButton_Click(object sender, RoutedEventArgs e)
        {
            var handler = AddOptionRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}
