namespace VideoTrayApp
{
    partial class ProgressForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            lblTitle = new Label();
            lblStatus = new Label();
            progressBar = new ProgressBar();
            btnCancel = new Button();
            SuspendLayout();

            lblTitle.AutoSize = true;
            lblTitle.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            lblTitle.ForeColor = Color.White;
            lblTitle.Location = new Point(20, 20);
            lblTitle.Text = "Working...";

            lblStatus.AutoSize = false;
            lblStatus.Font = new Font("Segoe UI", 9F);
            lblStatus.ForeColor = Color.FromArgb(200, 200, 200);
            lblStatus.Location = new Point(20, 55);
            lblStatus.Size = new Size(440, 40);
            lblStatus.Text = "Please wait...";

            progressBar.Location = new Point(20, 105);
            progressBar.Size = new Size(440, 24);
            progressBar.Style = ProgressBarStyle.Continuous;

            btnCancel.Text = "Cancel";
            btnCancel.Font = new Font("Segoe UI", 9F);
            btnCancel.BackColor = Color.FromArgb(100, 100, 100);
            btnCancel.ForeColor = Color.White;
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.Size = new Size(90, 32);
            btnCancel.Location = new Point(370, 145);
            btnCancel.Cursor = Cursors.Hand;
            btnCancel.Click += btnCancel_Click;

            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.FromArgb(45, 45, 48);
            ClientSize = new Size(480, 190);
            ControlBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Text = "Progress";
            Controls.Add(lblTitle);
            Controls.Add(lblStatus);
            Controls.Add(progressBar);
            Controls.Add(btnCancel);
            ResumeLayout(false);
            PerformLayout();
        }

        private Label lblTitle;
        private Label lblStatus;
        private ProgressBar progressBar;
        private Button btnCancel;
    }
}