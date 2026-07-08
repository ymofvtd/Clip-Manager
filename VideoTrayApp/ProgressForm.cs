namespace VideoTrayApp
{
    public partial class ProgressForm : Form, IOperationProgress
    {
        private CancellationTokenSource? cancellationTokenSource;

        public CancellationToken CancellationToken => cancellationTokenSource?.Token ?? CancellationToken.None;

        public ProgressForm()
        {
            InitializeComponent();
        }

        public void BeginOperation(string title, bool allowCancel = true)
        {
            cancellationTokenSource?.Dispose();
            cancellationTokenSource = new CancellationTokenSource();

            if (InvokeRequired)
            {
                Invoke(BeginOperationCore, title, allowCancel);
                return;
            }

            BeginOperationCore(title, allowCancel);
        }

        private void BeginOperationCore(string title, bool allowCancel)
        {
            lblTitle.Text = title;
            lblStatus.Text = "Starting...";
            progressBar.Style = ProgressBarStyle.Marquee;
            progressBar.MarqueeAnimationSpeed = 30;
            btnCancel.Visible = allowCancel;
            btnCancel.Enabled = allowCancel;
        }

        public void Report(int current, int total, string message)
        {
            if (InvokeRequired)
            {
                Invoke(ReportCore, current, total, message);
                return;
            }

            ReportCore(current, total, message);
        }

        private void ReportCore(int current, int total, string message)
        {
            lblStatus.Text = message;
            if (total <= 0)
            {
                progressBar.Style = ProgressBarStyle.Marquee;
                progressBar.MarqueeAnimationSpeed = 30;
                return;
            }

            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Maximum = Math.Max(total, 1);
            progressBar.Value = Math.Min(Math.Max(current, 0), progressBar.Maximum);
        }

        public void SetIndeterminate(string message)
        {
            Report(0, 0, message);
        }

        public void Complete(string message)
        {
            if (InvokeRequired)
            {
                Invoke(CompleteCore, message);
                return;
            }

            CompleteCore(message);
        }

        private void CompleteCore(string message)
        {
            lblStatus.Text = message;
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = progressBar.Maximum > 0 ? progressBar.Maximum : 0;
        }

        private void btnCancel_Click(object? sender, EventArgs e)
        {
            cancellationTokenSource?.Cancel();
            btnCancel.Enabled = false;
            lblStatus.Text = "Cancelling...";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            cancellationTokenSource?.Dispose();
            cancellationTokenSource = null;
            base.OnFormClosed(e);
        }
    }
}